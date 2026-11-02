using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace GemPrismUpscaler.Core.Rendering;

/// <summary>
/// The five-pass Anime4K 2x (S) CNN, expanded from mpv's user-shader format.
/// All passes run as Vulkan compute dispatches on rgba16f intermediates.
/// </summary>
internal sealed unsafe class Anime4KChain : IDisposable
{
    private static readonly string[] PassShaders =
    {
        "anime4k_conv0.comp.spv",
        "anime4k_conv1.comp.spv",
        "anime4k_conv2.comp.spv",
        "anime4k_conv3.comp.spv",
        "anime4k_d2s.comp.spv"
    };

    private static readonly string[][] PassInputs =
    {
        new[] { "MAIN" },
        new[] { "conv2d_tf" },
        new[] { "conv2d_1_tf" },
        new[] { "conv2d_2_tf" },
        new[] { "conv2d_last_tf", "MAIN" }
    };

    private static readonly string[] ImageNames = { "conv2d_tf", "conv2d_1_tf", "conv2d_2_tf", "conv2d_last_tf", "upscaled2x" };

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ImageView _sourceView;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;

    private readonly Image[] _images = new Image[5];
    private readonly DeviceMemory[] _memories = new DeviceMemory[5];
    private readonly ImageView[] _views = new ImageView[5];
    private readonly DescriptorSetLayout[] _setLayouts = new DescriptorSetLayout[5];
    private readonly PipelineLayout[] _pipelineLayouts = new PipelineLayout[5];
    private readonly Pipeline[] _pipelines = new Pipeline[5];
    private readonly DescriptorSet[] _sets = new DescriptorSet[5];
    private DescriptorPool _descriptorPool;
    private Sampler _sampler;
    private bool _imagesInitialized;
    private bool _disposed;

    public ImageView OutputView => _views[4];
    public Image OutputImage => _images[4];
    public int OutputWidth => _sourceWidth * 2;
    public int OutputHeight => _sourceHeight * 2;

    public Anime4KChain(Vk vk, Device device, PhysicalDevice physicalDevice, ImageView sourceView,
        int sourceWidth, int sourceHeight)
    {
        _vk = vk;
        _device = device;
        _physicalDevice = physicalDevice;
        _sourceView = sourceView;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public Vector2 OutSize;
        public Vector2 MainSize;
        public Vector2 Conv0Size;
        public Vector2 Conv1Size;
        public Vector2 Conv2Size;
        public Vector2 Conv3Size;
    }

    private static readonly byte[] EntryPoint = "main\0"u8.ToArray();

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
        for (int i = 0; i < _images.Length; i++)
        {
            bool output = i == 4;
            int width = output ? _sourceWidth * 2 : _sourceWidth;
            int height = output ? _sourceHeight * 2 : _sourceHeight;
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
            Check(_vk.CreateImage(_device, &ci, null, out _images[i]), "CreateImage(anime4k)");
            _vk.GetImageMemoryRequirements(_device, _images[i], out var req);
            var alloc = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = VulkanUtil.FindMemoryType(_vk, _physicalDevice, req.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit)
            };
            Check(_vk.AllocateMemory(_device, &alloc, null, out _memories[i]), "AllocateMemory(anime4k)");
            Check(_vk.BindImageMemory(_device, _images[i], _memories[i], 0), "BindImageMemory(anime4k)");

            var view = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
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
            Check(_vk.CreateImageView(_device, &view, null, out _views[i]), "CreateImageView(anime4k)");
        }
    }

    private void CreateDescriptorsAndPipelines()
    {
        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 7 };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 5 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 5,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes
        };
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool), "CreateDescriptorPool(anime4k)");

        var layouts = stackalloc DescriptorSetLayout[5];
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (int i = 0; i < 5; i++)
        {
            for (int b = 0; b < PassInputs[i].Length; b++)
            {
                bindings[b] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)b,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.ComputeBit
                };
            }
            bindings[PassInputs[i].Length] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)PassInputs[i].Length,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)(PassInputs[i].Length + 1),
                PBindings = bindings
            };
            Check(_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _setLayouts[i]), "CreateDescriptorSetLayout");

            var range = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)sizeof(PushConstants)
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
            Check(_vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayouts[i]), "CreatePipelineLayout");

            layouts[i] = _setLayouts[i];
        }

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 5,
            PSetLayouts = layouts
        };
        var sets = stackalloc DescriptorSet[5];
        Check(_vk.AllocateDescriptorSets(_device, &allocInfo, sets), "AllocateDescriptorSets");
        for (int i = 0; i < 5; i++) _sets[i] = sets[i];

        for (int i = 0; i < 5; i++)
        {
            CreateComputePipeline(i, PassShaders[i]);
        }

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
            Check(_vk.CreateShaderModule(_device, &moduleInfo, null, out module), $"CreateShaderModule({shaderName})");
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
        for (int pass = 0; pass < 5; pass++)
        {
            for (int b = 0; b < PassInputs[pass].Length; b++)
            {
                var imageInfo = new DescriptorImageInfo
                {
                    Sampler = _sampler,
                    ImageView = ResolveView(PassInputs[pass][b]),
                    ImageLayout = ImageLayout.General
                };
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[pass],
                    DstBinding = (uint)b,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfo
                };
                _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
            }

            var outputInfo = new DescriptorImageInfo
            {
                ImageView = _views[OutputIndex(pass)],
                ImageLayout = ImageLayout.General
            };
            int outputBinding = PassInputs[pass].Length;
            var outputWrite = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _sets[pass],
                DstBinding = (uint)outputBinding,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &outputInfo
            };
            _vk.UpdateDescriptorSets(_device, 1, &outputWrite, 0, null);
        }
    }

    private ImageView ResolveView(string name) => name switch
    {
        "MAIN" => _sourceView,
        "conv2d_tf" => _views[0],
        "conv2d_1_tf" => _views[1],
        "conv2d_2_tf" => _views[2],
        "conv2d_last_tf" => _views[3],
        _ => throw new InvalidOperationException($"unknown input texture {name}")
    };

    private static int OutputIndex(int pass) => pass switch
    {
        0 => 0,
        1 => 1,
        2 => 2,
        3 => 3,
        _ => 4
    };

    public void Record(CommandBuffer cmd)
    {
        if (!_imagesInitialized)
        {
            for (int i = 0; i < _images.Length; i++)
            {
                VulkanUtil.ImageBarrier(_vk, cmd, _images[i], ImageLayout.Undefined, ImageLayout.General,
                    0, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ComputeShaderBit);
            }
            _imagesInitialized = true;
        }

        var sizes = new PushConstants
        {
            MainSize = new Vector2(_sourceWidth, _sourceHeight),
            Conv0Size = new Vector2(_sourceWidth, _sourceHeight),
            Conv1Size = new Vector2(_sourceWidth, _sourceHeight),
            Conv2Size = new Vector2(_sourceWidth, _sourceHeight),
            Conv3Size = new Vector2(_sourceWidth, _sourceHeight)
        };

        for (int pass = 0; pass < 5; pass++)
        {
            int outWidth = pass == 4 ? _sourceWidth * 2 : _sourceWidth;
            int outHeight = pass == 4 ? _sourceHeight * 2 : _sourceHeight;
            sizes.OutSize = new Vector2(outWidth, outHeight);

            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[pass]);
            var set = _sets[pass];
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayouts[pass], 0, 1, &set, 0, null);
            _vk.CmdPushConstants(cmd, _pipelineLayouts[pass], ShaderStageFlags.ComputeBit, 0,
                (uint)sizeof(PushConstants), &sizes);

            uint groupsX = (uint)((outWidth + 7) / 8);
            uint groupsY = (uint)((outHeight + 7) / 8);
            _vk.CmdDispatch(cmd, groupsX, groupsY, 1);

            var output = _images[OutputIndex(pass)];
            if (pass == 4)
            {
                VulkanUtil.ImageBarrier(_vk, cmd, output, ImageLayout.General, ImageLayout.General,
                    AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
                    PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.FragmentShaderBit);
            }
            else
            {
                VulkanUtil.ImageBarrier(_vk, cmd, output, ImageLayout.General, ImageLayout.General,
                    AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
                    PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit);
            }
        }
    }

    private void Check(Result result, string what)
    {
        if (result != Result.Success) throw new InvalidOperationException($"{what} failed: {result}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle != 0)
        {
            for (int i = 0; i < _pipelines.Length; i++)
                if (_pipelines[i].Handle != 0) _vk.DestroyPipeline(_device, _pipelines[i], null);
            for (int i = 0; i < _pipelineLayouts.Length; i++)
                if (_pipelineLayouts[i].Handle != 0) _vk.DestroyPipelineLayout(_device, _pipelineLayouts[i], null);
            for (int i = 0; i < _setLayouts.Length; i++)
                if (_setLayouts[i].Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _setLayouts[i], null);
            if (_descriptorPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
            if (_sampler.Handle != 0) _vk.DestroySampler(_device, _sampler, null);
            for (int i = 0; i < _views.Length; i++)
                if (_views[i].Handle != 0) _vk.DestroyImageView(_device, _views[i], null);
            for (int i = 0; i < _images.Length; i++)
                if (_images[i].Handle != 0) _vk.DestroyImage(_device, _images[i], null);
            for (int i = 0; i < _memories.Length; i++)
                if (_memories[i].Handle != 0) _vk.FreeMemory(_device, _memories[i], null);
        }
    }
}
