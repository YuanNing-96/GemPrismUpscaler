using GemPrismUpscaler.Core.Geometry;
using Silk.NET.Vulkan;

namespace GemPrismUpscaler.Core.Rendering;

/// <summary>
/// Final composition pass: draws the 2x Anime4K result into the swapchain with a
/// Lanczos-2 filter, clearing the letterbox/pillarbox bars to black.
/// </summary>
internal sealed unsafe class LanczosPresenter : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Image[] _swapchainImages;
    private readonly ImageView[] _swapchainViews;
    private readonly Format _format;
    private readonly Extent2D _extent;

    private RenderPass _renderPass;
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _set;
    private Sampler _sampler;
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();
    private ImageView? _sourceView;
    private bool _disposed;

    private static readonly byte[] EntryPoint = "main\0"u8.ToArray();

    public LanczosPresenter(Vk vk, Device device, Image[] swapchainImages, ImageView[] swapchainViews,
        Format format, Extent2D extent)
    {
        _vk = vk;
        _device = device;
        _swapchainImages = swapchainImages;
        _swapchainViews = swapchainViews;
        _format = format;
        _extent = extent;

        try
        {
            CreateSampler();
            CreateRenderPass();
            CreateFramebuffers();
            CreateDescriptors();
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void CreateSampler()
    {
        var ci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0,
            MaxLod = 0
        };
        VulkanUtil.Check(_vk.CreateSampler(_device, &ci, null, out _sampler), "CreateSampler(lanczos)");
    }

    private void CreateRenderPass()
    {
        var attachment = new AttachmentDescription
        {
            Format = _format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.PresentSrcKhr
        };
        var colorRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };
        var ci = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };
        VulkanUtil.Check(_vk.CreateRenderPass(_device, &ci, null, out _renderPass), "CreateRenderPass(lanczos)");
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new Framebuffer[_swapchainViews.Length];
        for (int i = 0; i < _swapchainViews.Length; i++)
        {
            var view = _swapchainViews[i];
            var ci = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &view,
                Width = _extent.Width,
                Height = _extent.Height,
                Layers = 1
            };
            VulkanUtil.Check(_vk.CreateFramebuffer(_device, &ci, null, out _framebuffers[i]), "CreateFramebuffer(lanczos)");
        }
    }

    private void CreateDescriptors()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        VulkanUtil.Check(_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _setLayout),
            "CreateDescriptorSetLayout(lanczos)");

        var setLayout = _setLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout
        };
        VulkanUtil.Check(_vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayout),
            "CreatePipelineLayout(lanczos)");

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize
        };
        VulkanUtil.Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool),
            "CreateDescriptorPool(lanczos)");

        var setLayout2 = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout2
        };
        DescriptorSet set = default;
        VulkanUtil.Check(_vk.AllocateDescriptorSets(_device, &allocInfo, &set), "AllocateDescriptorSets(lanczos)");
        _set = set;
    }

    private void CreatePipeline()
    {
        var vert = CreateShaderModule(VulkanUtil.LoadSpirv("lanczos.vert.spv"));
        var frag = CreateShaderModule(VulkanUtil.LoadSpirv("lanczos.frag.spv"));
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            fixed (byte* entry = EntryPoint)
            {
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vert,
                    PName = entry
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = frag,
                    PName = entry
                };
            }

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList
            };

            var viewport = new Viewport { Width = _extent.Width, Height = _extent.Height, MaxDepth = 1 };
            var scissor = new Rect2D { Extent = _extent };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor
            };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment
            };
            var dynamicStates = stackalloc DynamicState[2];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };

            var ci = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &raster,
                PMultisampleState = &multisample,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0
            };
            Pipeline pipeline = default;
            VulkanUtil.Check(_vk.CreateGraphicsPipelines(_device, default, 1, &ci, null, &pipeline),
                "CreateGraphicsPipelines(lanczos)");
            _pipeline = pipeline;
        }
        finally
        {
            _vk.DestroyShaderModule(_device, vert, null);
            _vk.DestroyShaderModule(_device, frag, null);
        }
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* codePtr = code)
        {
            var ci = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)codePtr
            };
            VulkanUtil.Check(_vk.CreateShaderModule(_device, &ci, null, out var module), "CreateShaderModule(lanczos)");
            return module;
        }
    }

    public void SetSource(ImageView view)
    {
        if (_sourceView is { } current && current.Handle == view.Handle) return;
        _sourceView = view;

        var imageInfo = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = view,
            ImageLayout = ImageLayout.General
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    public void Record(CommandBuffer cmd, uint imageIndex, RectI destRect, bool firstUse)
    {
        if (_sourceView is null)
            throw new InvalidOperationException("LanczosPresenter.SetSource must be called before Record");

        var image = _swapchainImages[imageIndex];
        VulkanUtil.ImageBarrier(_vk, cmd, image,
            firstUse ? ImageLayout.Undefined : ImageLayout.PresentSrcKhr,
            ImageLayout.ColorAttachmentOptimal,
            0, AccessFlags.ColorAttachmentWriteBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit);

        var clear = new ClearValue
        {
            Color = new ClearColorValue { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 }
        };
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers[imageIndex],
            RenderArea = new Rect2D { Offset = new Offset2D(0, 0), Extent = _extent },
            ClearValueCount = 1,
            PClearValues = &clear
        };
        _vk.CmdBeginRenderPass(cmd, &begin, SubpassContents.Inline);

        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        var set = _set;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);

        var viewport = new Viewport
        {
            X = destRect.X,
            Y = destRect.Y,
            Width = destRect.Width,
            Height = destRect.Height,
            MinDepth = 0,
            MaxDepth = 1
        };
        var scissor = new Rect2D
        {
            Offset = new Offset2D(destRect.X, destRect.Y),
            Extent = new Extent2D((uint)destRect.Width, (uint)destRect.Height)
        };
        _vk.CmdSetViewport(cmd, 0, 1, &viewport);
        _vk.CmdSetScissor(cmd, 0, 1, &scissor);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(cmd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle == 0) return;

        foreach (var fb in _framebuffers)
            if (fb.Handle != 0) _vk.DestroyFramebuffer(_device, fb, null);
        if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
        if (_pipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        if (_setLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
        if (_descriptorPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        if (_sampler.Handle != 0) _vk.DestroySampler(_device, _sampler, null);
        if (_renderPass.Handle != 0) _vk.DestroyRenderPass(_device, _renderPass, null);
    }
}
