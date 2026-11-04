using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Geometry;
using Silk.NET.Vulkan;

namespace GemPrismUpscaler.Core.Rendering;

/// <summary>
/// Manifest-driven Anime4K 2x CNN chain (S/M/L).
///
/// The shader build emits one manifest describing each variant as a small pass
/// graph: every pass declares its input textures, output texture and output
/// scale. This class allocates the intermediate rgba16f images, creates one
/// compute pipeline and descriptor set per pass, and records the graph in
/// manifest order.
///
/// M1b adds a dispatched-region parameter. A full frame passes the whole source
/// rectangle; a dirty-rectangle update passes an expanded rectangle. Every
/// shader receives (regionOffset, regionSize) in its own output pixel space and
/// derives normalized coordinates from the full output size, so the same shader
/// works for both paths.
/// </summary>
internal sealed unsafe class Anime4KChain : IDisposable
{
    private const int MaxTextures = 13;
    private const int PushConstantLimit = 128;
    private const int MaxInputsPerPass = 8;

    private static readonly byte[] EntryPoint = "main\0"u8.ToArray();

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ImageView _sourceView;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly Anime4KVariant _variant;
    private readonly Dictionary<string, Anime4KTexture> _textureMap;

    private readonly Dictionary<string, Image> _images = new();
    private readonly Dictionary<string, DeviceMemory> _memories = new();
    private readonly Dictionary<string, ImageView> _views = new();
    private readonly DescriptorSetLayout[] _setLayouts;
    private readonly PipelineLayout[] _pipelineLayouts;
    private readonly Pipeline[] _pipelines;
    private readonly DescriptorSet[] _sets;

    private readonly int _pushSize;
    private DescriptorPool _descriptorPool;
    private Sampler _sampler;
    private bool _imagesInitialized;
    private bool _disposed;

    public Anime4KChain(Vk vk, Device device, PhysicalDevice physicalDevice, ImageView sourceView,
        int sourceWidth, int sourceHeight, EffectMode effect)
    {
        _vk = vk;
        _device = device;
        _physicalDevice = physicalDevice;
        _sourceView = sourceView;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _variant = Anime4KShaders.For(effect);
        _textureMap = _variant.Textures.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _pushSize = 3 * 8 + _variant.Textures.Count * 8;

        ValidateVariant();

        _setLayouts = new DescriptorSetLayout[_variant.Passes.Count];
        _pipelineLayouts = new PipelineLayout[_variant.Passes.Count];
        _pipelines = new Pipeline[_variant.Passes.Count];
        _sets = new DescriptorSet[_variant.Passes.Count];

        try
        {
            CheckFormatSupport();
            CreateSampler();
            CreateImages();
            CreateDescriptorsAndPipelines();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ImageView OutputView => ResolveView(_variant.OutputTexture);
    public Image OutputImage => ResolveImage(_variant.OutputTexture);
    public int OutputWidth => _sourceWidth * _textureMap[_variant.OutputTexture].Scale;
    public int OutputHeight => _sourceHeight * _textureMap[_variant.OutputTexture].Scale;

    /// <summary>Conservative source-pixel halo needed around a dirty rectangle.</summary>
    public int Radius => _variant.Radius;

    private void ValidateVariant()
    {
        if (_variant.Passes.Count == 0)
            throw new InvalidOperationException("Anime4K variant has no passes");
        if (_variant.Textures.Count > MaxTextures)
            throw new InvalidOperationException(
                $"Anime4K variant has {_variant.Textures.Count} textures (limit {MaxTextures})");
        if (!_textureMap.TryGetValue(_variant.OutputTexture, out var output) || output.External)
            throw new InvalidOperationException("Anime4K variant has an invalid output texture");
        if (output.Scale != 2)
            throw new InvalidOperationException("Anime4K output texture must be 2x");
        if (_pushSize > PushConstantLimit)
            throw new InvalidOperationException(
                $"Anime4K push constant block is {_pushSize} bytes (limit {PushConstantLimit})");

        foreach (var pass in _variant.Passes)
        {
            if (pass.OutputScale is not (1 or 2))
                throw new InvalidOperationException($"pass {pass.Index} has unsupported scale {pass.OutputScale}");
            if (!_textureMap.ContainsKey(pass.Output))
                throw new InvalidOperationException($"pass {pass.Index} writes unknown texture '{pass.Output}'");
            if (pass.Inputs.Count is < 1 or > MaxInputsPerPass)
                throw new InvalidOperationException($"pass {pass.Index} has {pass.Inputs.Count} inputs");
            foreach (string input in pass.Inputs)
            {
                if (!_textureMap.TryGetValue(input, out var texture))
                    throw new InvalidOperationException($"pass {pass.Index} reads unknown texture '{input}'");
                if (texture.Scale > pass.OutputScale)
                    throw new InvalidOperationException(
                        $"pass {pass.Index} reads {texture.Scale}x texture '{input}' but writes {pass.OutputScale}x");
            }
        }
    }

    private void CheckFormatSupport()
    {
        _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, Format.R16G16B16A16Sfloat, out var props);
        if ((props.OptimalTilingFeatures & FormatFeatureFlags.StorageImageBit) == 0)
            throw new InvalidOperationException("VK_FORMAT_R16G16B16A16_SFLOAT storage images are not supported");
    }

    private void CreateSampler()
    {
        var ci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0,
            MaxLod = 0
        };
        Check(_vk.CreateSampler(_device, &ci, null, out _sampler), "CreateSampler");
    }

    private void CreateImages()
    {
        foreach (var texture in _variant.Textures)
        {
            if (texture.External || texture.Name == "MAIN") continue;

            int width = _sourceWidth * texture.Scale;
            int height = _sourceHeight * texture.Scale;
            var ci = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format.R16G16B16A16Sfloat,
                Extent = new Extent3D((uint)width, (uint)height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
            Check(_vk.CreateImage(_device, &ci, null, out var image), $"CreateImage({texture.Name})");
            _vk.GetImageMemoryRequirements(_device, image, out var req);
            var alloc = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = VulkanUtil.FindMemoryType(_vk, _physicalDevice, req.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit)
            };
            Check(_vk.AllocateMemory(_device, &alloc, null, out var memory), $"AllocateMemory({texture.Name})");
            Check(_vk.BindImageMemory(_device, image, memory, 0), $"BindImageMemory({texture.Name})");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = Format.R16G16B16A16Sfloat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), $"CreateImageView({texture.Name})");

            _images[texture.Name] = image;
            _memories[texture.Name] = memory;
            _views[texture.Name] = view;
        }
    }

    private void CreateDescriptorsAndPipelines()
    {
        int passCount = _variant.Passes.Count;
        int samplerCount = _variant.Passes.Sum(p => p.Inputs.Count);
        int storageCount = passCount;

        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = (uint)samplerCount
        };
        poolSizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = (uint)storageCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)passCount,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes
        };
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool),
            "CreateDescriptorPool(anime4k)");

        var layouts = stackalloc DescriptorSetLayout[passCount];
        var bindings = stackalloc DescriptorSetLayoutBinding[MaxInputsPerPass + 1];
        for (int i = 0; i < passCount; i++)
        {
            var pass = _variant.Passes[i];
            for (int b = 0; b < pass.Inputs.Count; b++)
            {
                bindings[b] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)b,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.ComputeBit
                };
            }
            bindings[pass.Inputs.Count] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)pass.Inputs.Count,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)(pass.Inputs.Count + 1),
                PBindings = bindings
            };
            Check(_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _setLayouts[i]),
                $"CreateDescriptorSetLayout(pass {i})");

            var range = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)_pushSize
            };
            var setLayout = _setLayouts[i];
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &range
            };
            Check(_vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayouts[i]),
                $"CreatePipelineLayout(pass {i})");
            layouts[i] = _setLayouts[i];
        }

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = (uint)passCount,
            PSetLayouts = layouts
        };
        var sets = stackalloc DescriptorSet[passCount];
        Check(_vk.AllocateDescriptorSets(_device, &allocInfo, sets), "AllocateDescriptorSets");
        for (int i = 0; i < passCount; i++) _sets[i] = sets[i];

        for (int i = 0; i < passCount; i++)
            CreateComputePipeline(i, _variant.Passes[i].Shader);

        UpdateDescriptors();
    }

    private void CreateComputePipeline(int index, string shaderName)
    {
        var code = VulkanUtil.LoadSpirv(shaderName);
        ShaderModule module;
        fixed (byte* codePtr = code)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)codePtr
            };
            Check(_vk.CreateShaderModule(_device, &moduleInfo, null, out module),
                $"CreateShaderModule({shaderName})");
        }

        try
        {
            fixed (byte* entryPtr = EntryPoint)
            {
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entryPtr
                };
                var ci = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _pipelineLayouts[index]
                };
                Pipeline pipeline = default;
                Check(_vk.CreateComputePipelines(_device, default, 1, &ci, null, &pipeline),
                    $"CreateComputePipelines({shaderName})");
                _pipelines[index] = pipeline;
            }
        }
        finally
        {
            _vk.DestroyShaderModule(_device, module, null);
        }
    }

    private void UpdateDescriptors()
    {
        for (int i = 0; i < _variant.Passes.Count; i++)
        {
            var pass = _variant.Passes[i];
            for (int b = 0; b < pass.Inputs.Count; b++)
            {
                var imageInfo = new DescriptorImageInfo
                {
                    Sampler = _sampler,
                    ImageView = ResolveView(pass.Inputs[b]),
                    ImageLayout = ImageLayout.General
                };
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[i],
                    DstBinding = (uint)b,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfo
                };
                _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
            }

            var outputInfo = new DescriptorImageInfo
            {
                ImageView = ResolveView(pass.Output),
                ImageLayout = ImageLayout.General
            };
            var outputWrite = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _sets[i],
                DstBinding = (uint)pass.Inputs.Count,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &outputInfo
            };
            _vk.UpdateDescriptorSets(_device, 1, &outputWrite, 0, null);
        }
    }

    private ImageView ResolveView(string name)
    {
        if (name == "MAIN") return _sourceView;
        if (_views.TryGetValue(name, out var view)) return view;
        throw new InvalidOperationException($"unknown texture '{name}'");
    }

    private Image ResolveImage(string name)
    {
        if (_images.TryGetValue(name, out var image)) return image;
        throw new InvalidOperationException($"texture '{name}' is not a GPU image");
    }

    /// <summary>
    /// Records the whole chain for a source-space rectangle. Every pass gets the
    /// corresponding rectangle in its own output space; the caller is expected
    /// to expand the rectangle by <see cref="Radius"/> before calling.
    /// </summary>
    public void Record(CommandBuffer cmd, RectI sourceRegion)
    {
        EnsureImagesInitialized(cmd);

        byte* push = stackalloc byte[_pushSize];
        for (int i = 0; i < _variant.Passes.Count; i++)
        {
            var pass = _variant.Passes[i];
            int fullWidth = _sourceWidth * pass.OutputScale;
            int fullHeight = _sourceHeight * pass.OutputScale;
            var region = ScaleAndClamp(sourceRegion, pass.OutputScale, fullWidth, fullHeight);
            if (region.Width <= 0 || region.Height <= 0) continue;

            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[i]);
            var set = _sets[i];
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayouts[i], 0, 1, &set, 0, null);

            WritePushConstants(push, pass, region, fullWidth, fullHeight);
            _vk.CmdPushConstants(cmd, _pipelineLayouts[i], ShaderStageFlags.ComputeBit, 0,
                (uint)_pushSize, push);

            uint groupsX = (uint)((region.Width + 7) / 8);
            uint groupsY = (uint)((region.Height + 7) / 8);
            _vk.CmdDispatch(cmd, groupsX, groupsY, 1);

            var output = ResolveImage(pass.Output);
            bool final = i == _variant.Passes.Count - 1;
            VulkanUtil.ImageBarrier(_vk, cmd, output, ImageLayout.General, ImageLayout.General,
                AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.ComputeShaderBit,
                final ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.ComputeShaderBit);
        }
    }

    private void EnsureImagesInitialized(CommandBuffer cmd)
    {
        if (_imagesInitialized) return;
        foreach (var image in _images.Values)
        {
            VulkanUtil.ImageBarrier(_vk, cmd, image, ImageLayout.Undefined, ImageLayout.General,
                0, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ComputeShaderBit);
        }
        _imagesInitialized = true;
    }

    private void WritePushConstants(byte* destination, Anime4KPass pass, RectI region,
        int fullWidth, int fullHeight)
    {
        int offset = 0;
        WriteFloat2(destination, ref offset, fullWidth, fullHeight);
        WriteFloat2(destination, ref offset, region.X, region.Y);
        WriteFloat2(destination, ref offset, region.Width, region.Height);
        foreach (var texture in _variant.Textures)
        {
            int width = _sourceWidth * texture.Scale;
            int height = _sourceHeight * texture.Scale;
            WriteFloat2(destination, ref offset, width, height);
        }

        if (offset != _pushSize)
            throw new InvalidOperationException(
                $"push constant size mismatch: wrote {offset}, expected {_pushSize}");
    }

    private static void WriteFloat2(byte* destination, ref int offset, float x, float y)
    {
        var value = new Vector2(x, y);
        MemoryMarshal.Write(new Span<byte>(destination + offset, 8), in value);
        offset += 8;
    }

    private static RectI ScaleAndClamp(RectI region, int scale, int fullWidth, int fullHeight)
    {
        int left = Math.Clamp(region.X * scale, 0, fullWidth);
        int top = Math.Clamp(region.Y * scale, 0, fullHeight);
        int right = Math.Clamp(region.Right * scale, 0, fullWidth);
        int bottom = Math.Clamp(region.Bottom * scale, 0, fullHeight);
        return new RectI(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void Check(Result result, string what)
    {
        if (result != Result.Success) throw new InvalidOperationException($"{what} failed: {result}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle == 0) return;

        for (int i = 0; i < _pipelines.Length; i++)
            if (_pipelines[i].Handle != 0) _vk.DestroyPipeline(_device, _pipelines[i], null);
        for (int i = 0; i < _pipelineLayouts.Length; i++)
            if (_pipelineLayouts[i].Handle != 0) _vk.DestroyPipelineLayout(_device, _pipelineLayouts[i], null);
        for (int i = 0; i < _setLayouts.Length; i++)
            if (_setLayouts[i].Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _setLayouts[i], null);
        if (_descriptorPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        if (_sampler.Handle != 0) _vk.DestroySampler(_device, _sampler, null);

        foreach (var view in _views.Values)
            if (view.Handle != 0) _vk.DestroyImageView(_device, view, null);
        foreach (var image in _images.Values)
            if (image.Handle != 0) _vk.DestroyImage(_device, image, null);
        foreach (var memory in _memories.Values)
            if (memory.Handle != 0) _vk.FreeMemory(_device, memory, null);
    }
}