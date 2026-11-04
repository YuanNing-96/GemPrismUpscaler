using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace GemPrismUpscaler.Core.Rendering;

/// <summary>
/// Minimal Vulkan presentation path for M0a: upload the XShm BGRA frame into a
/// device-local image and blit it (linear filtering) into the X11 swapchain image.
/// No shader/pipeline yet; Anime4K/FSR compute comes later.
/// </summary>
public sealed unsafe class VulkanBlitter : IDisposable
{
    private readonly X11Display _display;
    private readonly ulong _window;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private readonly EffectMode _effect;

    private Vk _vk = null!;
    private Instance _instance;
    private KhrSurface _surfaceExt = null!;
    private KhrXlibSurface _xlibExt = null!;
    private KhrSwapchain _swapchainExt = null!;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private SwapchainKHR _swapchain;
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;
    private Image[] _swapchainImages = Array.Empty<Image>();
    private ImageView[] _swapchainViews = Array.Empty<ImageView>();
    private bool[] _swapchainFirstUse = Array.Empty<bool>();
    private VkSemaphore _imageAvailable;
    private VkSemaphore[] _renderSemaphores = Array.Empty<VkSemaphore>();
    private Fence _inFlight;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Image _sourceImage;
    private ImageView _sourceView;
    private DeviceMemory _sourceMemory;
    private Image _composedImage;
    private ImageView _composedView;
    private DeviceMemory _composedMemory;
    private ImageLayout _composedLayout = ImageLayout.Undefined;
    private VkBuffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private void* _stagingMapped;
    private readonly BufferImageCopy[] _copyRegions = new BufferImageCopy[4096];
    private int _sourceWidth;
    private int _sourceHeight;
    private int _sourceStride;
    private ImageLayout _sourceLayout = ImageLayout.Undefined;
    private Anime4KChain? _anime4k;
    private LanczosPresenter? _presenter;
    private QueryPool _timestampPool;
    private bool _timestampsAvailable;
    private float _timestampPeriodNs;
    private bool _hasPendingTimestamps;
    private double _lastGpuUploadMs;
    private double _lastGpuEffectMs;
    private double _lastGpuTailMs;
    private bool _lastGpuValid;
    private bool _disposed;

    public string DeviceName { get; private set; } = "?";
    public string SwapchainFormatName => _swapchainFormat.ToString();
    public Extent2D SwapchainExtent => _swapchainExtent;

    public double LastUploadCpuMs { get; private set; }
    public double LastEffectCpuMs { get; private set; }
    public double LastPresentCpuMs { get; private set; }
    public double LastTotalCpuMs { get; private set; }
    public double LastGpuUploadMs => _lastGpuUploadMs;
    public double LastGpuEffectMs => _lastGpuEffectMs;
    public double LastGpuTailMs => _lastGpuTailMs;
    public bool LastGpuTimingsValid => _lastGpuValid;

    /// <summary>Source-pixel halo required by the active Anime4K variant.</summary>
    public int DirtyHalo => _anime4k?.Radius ?? 0;

    public VulkanBlitter(X11Display display, ulong window, int outputWidth, int outputHeight,
        EffectMode effect = EffectMode.Anime4KS)
    {
        _display = display;
        _window = window;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _effect = effect;
        CreateInstance();
        CreateSurface();
        PickDevice();
        CreateDevice();
        CreateSwapchain();
        CreateSyncAndCommands();
        CreateTimestampPool();
        CreateStagingBuffer();
        if (_effect.IsAnime4K())
        {
            CreateComposedImage();
            _presenter = new LanczosPresenter(_vk, _device, _composedView,
                _swapchainFormat, _swapchainExtent);
        }
    }

    private void CreateInstance()
    {
        _vk = Vk.GetApi();

        var appName = Marshal.StringToHGlobalAnsi("GemPrismUpscaler");
        var engineName = Marshal.StringToHGlobalAnsi("GemPrism");
        try
        {
            var app = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)engineName,
                EngineVersion = 1,
                ApiVersion = Vk.MakeVersion(1, 2, 0)
            };

            var extensions = new[] { "VK_KHR_surface", "VK_KHR_xlib_surface" };
            var extPtrs = extensions.Select(Marshal.StringToHGlobalAnsi).ToArray();
            var layers = new[] { "VK_LAYER_KHRONOS_validation" };
            var layerPtrs = layers.Select(Marshal.StringToHGlobalAnsi).ToArray();
            var ep = GCHandle.Alloc(extPtrs, GCHandleType.Pinned);
            var lp = GCHandle.Alloc(layerPtrs, GCHandleType.Pinned);
            try
            {
                var ci = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &app,
                    EnabledExtensionCount = (uint)extPtrs.Length,
                    PpEnabledExtensionNames = (byte**)ep.AddrOfPinnedObject(),
                    EnabledLayerCount = (uint)layerPtrs.Length,
                    PpEnabledLayerNames = (byte**)lp.AddrOfPinnedObject()
                };

                Result r = _vk.CreateInstance(&ci, null, out _instance);
                if (r != Result.Success)
                {
                    ci.EnabledLayerCount = 0;
                    ci.PpEnabledLayerNames = null;
                    r = _vk.CreateInstance(&ci, null, out _instance);
                }
                if (r != Result.Success) throw new InvalidOperationException($"vkCreateInstance failed: {r}");
            }
            finally
            {
                ep.Free();
                lp.Free();
                foreach (var p in layerPtrs) Marshal.FreeHGlobal(p);
                foreach (var p in extPtrs) Marshal.FreeHGlobal(p);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(appName);
            Marshal.FreeHGlobal(engineName);
        }

        if (!_vk.TryGetInstanceExtension(_instance, out _surfaceExt, null))
            throw new InvalidOperationException("VK_KHR_surface unavailable");
        if (!_vk.TryGetInstanceExtension(_instance, out _xlibExt, null))
            throw new InvalidOperationException("VK_KHR_xlib_surface unavailable");
    }

    private void CreateSurface()
    {
        var ci = new XlibSurfaceCreateInfoKHR
        {
            SType = StructureType.XlibSurfaceCreateInfoKhr,
            Dpy = (IntPtr*)_display.Handle,
            Window = (IntPtr)_window
        };
        Result r = _xlibExt.CreateXlibSurface(_instance, &ci, null, out _surface);
        if (r != Result.Success) throw new InvalidOperationException($"vkCreateXlibSurfaceKHR failed: {r}");
    }

    private void PickDevice()
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, &count, null);
        if (count == 0) throw new InvalidOperationException("no Vulkan physical devices");
        var devices = stackalloc PhysicalDevice[(int)count];
        _vk.EnumeratePhysicalDevices(_instance, &count, devices);

        PhysicalDevice best = default;
        int bestScore = -1;
        var qprops = stackalloc QueueFamilyProperties[64];
        for (int i = 0; i < count; i++)
        {
            var dev = devices[i];
            _vk.GetPhysicalDeviceProperties(dev, out var props);
            int score = props.DeviceType == PhysicalDeviceType.DiscreteGpu ? 100 : 0;
            if (props.VendorID == 0x10DE) score += 50;
            if (props.DeviceType == PhysicalDeviceType.IntegratedGpu) score += 10;

            uint qcount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(dev, &qcount, null);
            if (qcount > 64) qcount = 64;
            _vk.GetPhysicalDeviceQueueFamilyProperties(dev, &qcount, qprops);
            bool hasGraphics = false, hasPresent = false;
            for (int q = 0; q < qcount; q++)
            {
                if ((qprops[q].QueueFlags & QueueFlags.GraphicsBit) != 0) hasGraphics = true;
                _surfaceExt.GetPhysicalDeviceSurfaceSupport(dev, (uint)q, _surface, out Bool32 supported);
                if (supported) hasPresent = true;
            }
            if (hasGraphics && hasPresent) score += 20;

            if (score > bestScore)
            {
                bestScore = score;
                best = dev;
                _physicalDevice = dev;
            }
        }

        if (best.Handle == 0) throw new InvalidOperationException("no suitable Vulkan device");
        _physicalDevice = best;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out var chosen);
        DeviceName = $"{chosen.DeviceType} vendor=0x{chosen.VendorID:x}";
        try
        {
            var s = Marshal.PtrToStringAnsi((IntPtr)chosen.DeviceName);
            if (!string.IsNullOrWhiteSpace(s)) DeviceName = s;
        }
        catch { }
    }

    private void CreateDevice()
    {
        uint qcount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &qcount, null);
        var qprops = stackalloc QueueFamilyProperties[(int)qcount];
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &qcount, qprops);

        uint family = uint.MaxValue;
        for (uint q = 0; q < qcount; q++)
        {
            bool graphics = (qprops[q].QueueFlags & QueueFlags.GraphicsBit) != 0;
            _surfaceExt.GetPhysicalDeviceSurfaceSupport(_physicalDevice, q, _surface, out Bool32 present);
            if (graphics && present) { family = q; break; }
        }
        if (family == uint.MaxValue) throw new InvalidOperationException("no graphics+present queue family");
        _queueFamily = family;

        float priority = 1.0f;
        var qci = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = family,
            QueueCount = 1,
            PQueuePriorities = &priority
        };
        var extName = Marshal.StringToHGlobalAnsi("VK_KHR_swapchain");
        var extPtrs = new[] { extName };
        var ep = GCHandle.Alloc(extPtrs, GCHandleType.Pinned);
        try
        {
            var dci = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &qci,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = (byte**)ep.AddrOfPinnedObject()
            };
            Result r = _vk.CreateDevice(_physicalDevice, &dci, null, out _device);
            if (r != Result.Success) throw new InvalidOperationException($"vkCreateDevice failed: {r}");
        }
        finally
        {
            ep.Free();
            Marshal.FreeHGlobal(extName);
        }

        _vk.GetDeviceQueue(_device, family, 0, out _queue);
        if (!_vk.TryGetDeviceExtension(_instance, _device, out _swapchainExt, null))
            throw new InvalidOperationException("VK_KHR_swapchain unavailable");
    }

    private void CreateSwapchain()
    {
        _surfaceExt.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var caps);

        uint formatCount = 0;
        _surfaceExt.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null);
        var formats = stackalloc SurfaceFormatKHR[(int)formatCount];
        _surfaceExt.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formats);
        SurfaceFormatKHR chosen = formats[0];
        for (int i = 0; i < formatCount; i++)
        {
            if (formats[i].Format == Format.B8G8R8A8Unorm)
            {
                chosen = formats[i];
                break;
            }
        }
        _swapchainFormat = chosen.Format;

        uint presentCount = 0;
        _surfaceExt.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentCount, null);
        var presentModes = stackalloc PresentModeKHR[(int)presentCount];
        _surfaceExt.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentCount, presentModes);
        PresentModeKHR presentMode = PresentModeKHR.FifoKhr;
        for (int i = 0; i < presentCount; i++)
        {
            if (presentModes[i] == PresentModeKHR.MailboxKhr) { presentMode = PresentModeKHR.MailboxKhr; break; }
        }

        Extent2D extent = caps.CurrentExtent;
        if (extent.Width == uint.MaxValue)
            extent = new Extent2D((uint)_outputWidth, (uint)_outputHeight);
        _swapchainExtent = extent;

        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount) imageCount = caps.MaxImageCount;

        CompositeAlphaFlagsKHR alpha = caps.SupportedCompositeAlpha.HasFlag(CompositeAlphaFlagsKHR.OpaqueBitKhr)
            ? CompositeAlphaFlagsKHR.OpaqueBitKhr
            : CompositeAlphaFlagsKHR.InheritBitKhr;

        var ci = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = alpha,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default
        };

        Result r = _swapchainExt.CreateSwapchain(_device, &ci, null, out _swapchain);
        if (r != Result.Success) throw new InvalidOperationException($"vkCreateSwapchainKHR failed: {r}");

        uint count = 0;
        _swapchainExt.GetSwapchainImages(_device, _swapchain, &count, null);
        _swapchainImages = new Image[count];
        fixed (Image* p = _swapchainImages)
            _swapchainExt.GetSwapchainImages(_device, _swapchain, &count, p);

        _swapchainViews = new ImageView[count];
        _swapchainFirstUse = Enumerable.Repeat(true, (int)count).ToArray();
        for (int i = 0; i < count; i++)
        {
            var iv = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = chosen.Format,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity, G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity, A = ComponentSwizzle.Identity
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0, LevelCount = 1,
                    BaseArrayLayer = 0, LayerCount = 1
                }
            };
            r = _vk.CreateImageView(_device, &iv, null, out _swapchainViews[i]);
            if (r != Result.Success) throw new InvalidOperationException($"vkCreateImageView failed: {r}");
        }
    }

    /// <summary>
/// Persistent full-screen image that holds the last composed frame. Dirty
/// updates patch this image, then a single image copy feeds the acquired
/// swapchain image. Without it a partial render would lose the untouched areas
/// because swapchain images rotate.
/// </summary>
    private void CreateComposedImage()
    {
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _swapchainFormat,
            Extent = new Extent3D(_swapchainExtent.Width, _swapchainExtent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        Check(_vk.CreateImage(_device, &ci, null, out _composedImage), "CreateImage(composed)");
        _vk.GetImageMemoryRequirements(_device, _composedImage, out var req);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        Check(_vk.AllocateMemory(_device, &alloc, null, out _composedMemory), "AllocateMemory(composed)");
        Check(_vk.BindImageMemory(_device, _composedImage, _composedMemory, 0), "BindImageMemory(composed)");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _composedImage,
            ViewType = ImageViewType.Type2D,
            Format = _swapchainFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        Check(_vk.CreateImageView(_device, &viewInfo, null, out _composedView), "CreateImageView(composed)");
        _composedLayout = ImageLayout.Undefined;
    }

    private void CreateSyncAndCommands()
    {
        var sem = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        Check(_vk.CreateSemaphore(_device, &sem, null, out _imageAvailable), "CreateSemaphore");
        _renderSemaphores = new VkSemaphore[_swapchainImages.Length];
        for (int i = 0; i < _renderSemaphores.Length; i++)
            Check(_vk.CreateSemaphore(_device, &sem, null, out _renderSemaphores[i]), "CreateSemaphore");

        var fence = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        Check(_vk.CreateFence(_device, &fence, null, out _inFlight), "CreateFence");

        var pool = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };
        Check(_vk.CreateCommandPool(_device, &pool, null, out _commandPool), "CreateCommandPool");

        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        Check(_vk.AllocateCommandBuffers(_device, &alloc, out _commandBuffer), "AllocateCommandBuffers");
    }

    /// <summary>
    /// Advances per-stage GPU timing. Four timestamps per frame are enough:
    /// frame start, after source upload, after effect/presenter, command end.
    /// The values are read at the next Render after the fence has signaled.
    /// </summary>
    private void CreateTimestampPool()
    {
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &count, null);
        if (count == 0) return;

        var properties = stackalloc QueueFamilyProperties[(int)count];
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &count, properties);
        if (_queueFamily >= count || properties[(int)_queueFamily].TimestampValidBits == 0)
            return;

        _vk.GetPhysicalDeviceProperties(_physicalDevice, out var deviceProperties);
        _timestampPeriodNs = deviceProperties.Limits.TimestampPeriod;

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = 4
        };
        if (_vk.CreateQueryPool(_device, &info, null, out _timestampPool) != Result.Success)
        {
            _timestampPool = default;
            return;
        }
        _timestampsAvailable = true;
    }

    private void ReadGpuTimestamps()
    {
        if (!_timestampsAvailable || !_hasPendingTimestamps) return;
        _hasPendingTimestamps = false;

        ulong* data = stackalloc ulong[4];
        Result result = _vk.GetQueryPoolResults(_device, _timestampPool, 0, 4,
            (nuint)(4 * sizeof(ulong)), data, sizeof(ulong),
            QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
        if (result != Result.Success || data[1] < data[0] || data[2] < data[1] || data[3] < data[2])
        {
            _lastGpuValid = false;
            return;
        }

        double millisecondsPerTick = _timestampPeriodNs / 1_000_000.0;
        _lastGpuUploadMs = (data[1] - data[0]) * millisecondsPerTick;
        _lastGpuEffectMs = (data[2] - data[1]) * millisecondsPerTick;
        _lastGpuTailMs = (data[3] - data[2]) * millisecondsPerTick;
        _lastGpuValid = true;
    }

    private void WriteTimestamp(PipelineStageFlags stage, uint index)
    {
        if (_timestampsAvailable)
            _vk.CmdWriteTimestamp(_commandBuffer, stage, _timestampPool, index);
    }

    private void CreateStagingBuffer()
    {
        // Buffer is sized for the maximum expected source; recreated on resize.
        var ci = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = 64 * 1024 * 1024,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateBuffer(_device, &ci, null, out _stagingBuffer), "CreateBuffer(staging)");
        _vk.GetBufferMemoryRequirements(_device, _stagingBuffer, out var req);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        Check(_vk.AllocateMemory(_device, &alloc, null, out _stagingMemory), "AllocateMemory(staging)");
        Check(_vk.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0), "BindBufferMemory");
        void* mapped;
        Check(_vk.MapMemory(_device, _stagingMemory, 0, req.Size, 0, &mapped), "MapMemory");
        _stagingMapped = mapped;
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags wanted)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var props);
        var types = (MemoryType*)Unsafe.AsPointer(ref props.MemoryTypes);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 && (types[i].PropertyFlags & wanted) == wanted)
                return i;
        }
        throw new InvalidOperationException("no suitable memory type");
    }

    private void EnsureSource(int width, int height)
    {
        if (_sourceImage.Handle != 0 && width == _sourceWidth && height == _sourceHeight)
            return;

        _anime4k?.Dispose();
        _anime4k = null;

        if (_sourceView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _sourceView, null);
            _sourceView = default;
        }
        if (_sourceImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _sourceImage, null);
            _vk.FreeMemory(_device, _sourceMemory, null);
            _sourceImage = default;
            _sourceMemory = default;
        }

        _sourceWidth = width;
        _sourceHeight = height;
        _sourceStride = width * 4;
        _sourceLayout = ImageLayout.Undefined;

        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.B8G8R8A8Unorm,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        Check(_vk.CreateImage(_device, &ci, null, out _sourceImage), "CreateImage(source)");
        _vk.GetImageMemoryRequirements(_device, _sourceImage, out var req);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        Check(_vk.AllocateMemory(_device, &alloc, null, out _sourceMemory), "AllocateMemory(source)");
        Check(_vk.BindImageMemory(_device, _sourceImage, _sourceMemory, 0), "BindImageMemory");

        var view = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _sourceImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.B8G8R8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        Check(_vk.CreateImageView(_device, &view, null, out _sourceView), "CreateImageView(source)");
    }

    /// <summary>Uploads one BGRA frame and presents it into destRect.</summary>
    public void Render(ReadOnlySpan<byte> frame, int width, int height, int stride, RectI destRect,
        DirtyFrame? dirty = null)
    {
        if (frame.Length < stride * height) throw new ArgumentException("frame buffer too small");
        var totalClock = Stopwatch.StartNew();

        var inFlight = _inFlight;
        Check(_vk.WaitForFences(_device, 1, &inFlight, true, 2_000_000_000), "WaitForFences");
        ReadGpuTimestamps();
        EnsureSource(width, height);
        _vk.ResetFences(_device, 1, &inFlight);

        uint imageIndex = 0;
        var imageAvailable = _imageAvailable;
        Result acquire = _swapchainExt.AcquireNextImage(_device, _swapchain, 2_000_000_000, imageAvailable, default, &imageIndex);
        if (acquire != Result.Success && acquire != Result.SuboptimalKhr)
            throw new InvalidOperationException($"vkAcquireNextImageKHR failed: {acquire}");

        bool fullFrame = dirty is null || dirty.FullFrame || !_effect.IsAnime4K();
        var uploadClock = Stopwatch.StartNew();
        if (!fullFrame && dirty!.UploadRects.Count > 0)
            CopyTilesToStaging(frame, width, height, stride, dirty.UploadRects);
        else
            CopyToStaging(frame, width, height, stride);
        LastUploadCpuMs = uploadClock.Elapsed.TotalMilliseconds;

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        Check(_vk.BeginCommandBuffer(_commandBuffer, &begin), "BeginCommandBuffer");

        var effectClock = Stopwatch.StartNew();
        if (_timestampsAvailable)
            _vk.CmdResetQueryPool(_commandBuffer, _timestampPool, 0, 4);
        WriteTimestamp(PipelineStageFlags.TopOfPipeBit, 0);

        if (!_effect.IsAnime4K())
            RecordBilinear(imageIndex, width, height, destRect);
        else
            RecordAnime4K(imageIndex, width, height, destRect, dirty, fullFrame);

        WriteTimestamp(PipelineStageFlags.BottomOfPipeBit, 3);
        Check(_vk.EndCommandBuffer(_commandBuffer), "EndCommandBuffer");
        LastEffectCpuMs = effectClock.Elapsed.TotalMilliseconds;
        _hasPendingTimestamps = _timestampsAvailable;

        var waitStage = _effect.IsAnime4K()
            ? PipelineStageFlags.TransferBit | PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.TransferBit;
        var presentClock = Stopwatch.StartNew();
        var cb = _commandBuffer;
        var renderFinished = _renderSemaphores[imageIndex];
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &imageAvailable,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &renderFinished
        };
        Check(_vk.QueueSubmit(_queue, 1, &submit, inFlight), "QueueSubmit");

        var sc = _swapchain;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            PSwapchains = &sc,
            PImageIndices = &imageIndex
        };
        Result pr = _swapchainExt.QueuePresent(_queue, &present);
        if (pr != Result.Success && pr != Result.SuboptimalKhr)
            throw new InvalidOperationException($"vkQueuePresentKHR failed: {pr}");
        LastPresentCpuMs = presentClock.Elapsed.TotalMilliseconds;
        LastTotalCpuMs = totalClock.Elapsed.TotalMilliseconds;
    }

    private void RecordBilinear(uint imageIndex, int width, int height, RectI destRect)
    {
        ImageLayout oldSwap = _swapchainFirstUse[imageIndex] ? ImageLayout.Undefined : ImageLayout.PresentSrcKhr;
        _swapchainFirstUse[imageIndex] = false;
        TransitionImage(_swapchainImages[imageIndex], oldSwap, ImageLayout.TransferDstOptimal,
            new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1
            });

        var black = new ClearColorValue { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 };
        var fullRange = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1
        };
        _vk.CmdClearColorImage(_commandBuffer, _swapchainImages[imageIndex], ImageLayout.TransferDstOptimal, &black, 1, &fullRange);

        if (_sourceLayout == ImageLayout.Undefined)
        {
            TransitionImage(_sourceImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, fullRange);
            _sourceLayout = ImageLayout.TransferDstOptimal;
        }
        else if (_sourceLayout == ImageLayout.TransferSrcOptimal)
        {
            TransitionImage(_sourceImage, ImageLayout.TransferSrcOptimal, ImageLayout.TransferDstOptimal, fullRange);
            _sourceLayout = ImageLayout.TransferDstOptimal;
        }

        CopySourceImage(width, height, ImageLayout.TransferDstOptimal);
        TransitionImage(_sourceImage, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, fullRange);
        _sourceLayout = ImageLayout.TransferSrcOptimal;
        WriteTimestamp(PipelineStageFlags.TransferBit, 1);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1
            }
        };
        blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
        blit.SrcOffsets.Element1 = new Offset3D(width, height, 1);
        blit.DstOffsets.Element0 = new Offset3D(destRect.X, destRect.Y, 0);
        blit.DstOffsets.Element1 = new Offset3D(destRect.Right, destRect.Bottom, 1);
        _vk.CmdBlitImage(_commandBuffer, _sourceImage, ImageLayout.TransferSrcOptimal,
            _swapchainImages[imageIndex], ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
        WriteTimestamp(PipelineStageFlags.TransferBit, 2);

        TransitionImage(_swapchainImages[imageIndex], ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, fullRange);
    }

    private void RecordAnime4K(uint imageIndex, int width, int height, RectI destRect,
        DirtyFrame? dirty, bool fullFrame)
    {
        if (_sourceLayout == ImageLayout.Undefined)
        {
            VulkanUtil.ImageBarrier(_vk, _commandBuffer, _sourceImage, ImageLayout.Undefined, ImageLayout.General,
                0, AccessFlags.TransferWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
            _sourceLayout = ImageLayout.General;
        }
        else if (_sourceLayout != ImageLayout.General)
        {
            VulkanUtil.ImageBarrier(_vk, _commandBuffer, _sourceImage, _sourceLayout, ImageLayout.General,
                0, AccessFlags.TransferWriteBit, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit);
            _sourceLayout = ImageLayout.General;
        }

        if (fullFrame || dirty is null)
            CopySourceImage(width, height, ImageLayout.General);
        else
            CopySourceRegions(dirty.UploadRects, width, height, ImageLayout.General);
        VulkanUtil.ImageBarrier(_vk, _commandBuffer, _sourceImage, ImageLayout.General, ImageLayout.General,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit);
        WriteTimestamp(PipelineStageFlags.TransferBit, 1);

        _anime4k ??= new Anime4KChain(_vk, _device, _physicalDevice, _sourceView, width, height, _effect);
        _presenter!.SetSource(_anime4k.OutputView);
        if (fullFrame || dirty is null)
            _anime4k.Record(_commandBuffer, new RectI(0, 0, width, height));
        else
            _anime4k.Record(_commandBuffer, dirty.ComputeRects);
        WriteTimestamp(PipelineStageFlags.ComputeShaderBit, 2);

        bool firstUse = _swapchainFirstUse[imageIndex];
        _swapchainFirstUse[imageIndex] = false;

        PrepareComposedForRender(fullFrame);
        _presenter.Record(_commandBuffer, destRect, dirty?.OutputRects, fullFrame);
        FinishComposedAndPresent(imageIndex, firstUse);
    }

    private void PrepareComposedForRender(bool fullFrame)
    {
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1
        };

        if (fullFrame)
        {
            if (_composedLayout == ImageLayout.Undefined)
            {
                VulkanUtil.ImageBarrier(_vk, _commandBuffer, _composedImage,
                    ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
            }
            else
            {
                VulkanUtil.ImageBarrier(_vk, _commandBuffer, _composedImage,
                    _composedLayout, ImageLayout.TransferDstOptimal,
                    AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
            }

            var black = new ClearColorValue { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 };
            _vk.CmdClearColorImage(_commandBuffer, _composedImage, ImageLayout.TransferDstOptimal,
                &black, 1, &range);
            VulkanUtil.ImageBarrier(_vk, _commandBuffer, _composedImage,
                ImageLayout.TransferDstOptimal, ImageLayout.ColorAttachmentOptimal,
                AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentWriteBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit);
        }
        else if (_composedLayout != ImageLayout.ColorAttachmentOptimal)
        {
            AccessFlags srcAccess = _composedLayout == ImageLayout.Undefined
                ? 0
                : AccessFlags.TransferReadBit;
            PipelineStageFlags srcStage = _composedLayout == ImageLayout.Undefined
                ? PipelineStageFlags.TopOfPipeBit
                : PipelineStageFlags.TransferBit;
            VulkanUtil.ImageBarrier(_vk, _commandBuffer, _composedImage,
                _composedLayout, ImageLayout.ColorAttachmentOptimal,
                srcAccess, AccessFlags.ColorAttachmentWriteBit,
                srcStage, PipelineStageFlags.ColorAttachmentOutputBit);
        }

        _composedLayout = ImageLayout.ColorAttachmentOptimal;
    }

    private void FinishComposedAndPresent(uint imageIndex, bool firstUse)
    {
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1
        };

        VulkanUtil.ImageBarrier(_vk, _commandBuffer, _composedImage,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit);
        _composedLayout = ImageLayout.TransferSrcOptimal;

        ImageLayout oldSwapchainLayout = firstUse ? ImageLayout.Undefined : ImageLayout.PresentSrcKhr;
        TransitionImage(_swapchainImages[imageIndex], oldSwapchainLayout, ImageLayout.TransferDstOptimal, range);

        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1
            },
            Extent = new Extent3D(_swapchainExtent.Width, _swapchainExtent.Height, 1)
        };
        _vk.CmdCopyImage(_commandBuffer, _composedImage, ImageLayout.TransferSrcOptimal,
            _swapchainImages[imageIndex], ImageLayout.TransferDstOptimal, 1, &copy);

        TransitionImage(_swapchainImages[imageIndex], ImageLayout.TransferDstOptimal,
            ImageLayout.PresentSrcKhr, range);
    }

    private void CopyTilesToStaging(ReadOnlySpan<byte> frame, int width, int height, int stride,
        IReadOnlyList<RectI> rects)
    {
        byte* destination = (byte*)_stagingMapped;
        fixed (byte* source = frame)
        {
            foreach (var rect in rects)
            {
                int rowBytes = rect.Width * 4;
                for (int row = 0; row < rect.Height; row++)
                {
                    byte* src = source + (rect.Y + row) * stride + rect.X * 4;
                    byte* dst = destination + ((rect.Y + row) * width + rect.X) * 4;
                    System.Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
                }
            }
        }
    }

    private void CopySourceRegions(IReadOnlyList<RectI> rects, int width, int height, ImageLayout layout)
    {
        if (rects.Count == 0) return;
        if (rects.Count > _copyRegions.Length)
            throw new InvalidOperationException($"too many upload regions ({rects.Count})");

        int count = 0;
        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            _copyRegions[count] = new BufferImageCopy
            {
                BufferOffset = ((ulong)rect.Y * (ulong)width + (ulong)rect.X) * 4,
                // The staging buffer is a full-width frame, so a sub-rectangle
                // must tell Vulkan the real row stride; 0 would mean tightly
                // packed rows of the sub-rectangle width (the stripe bug).
                BufferRowLength = (uint)width,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1
                },
                ImageOffset = new Offset3D(rect.X, rect.Y, 0),
                ImageExtent = new Extent3D((uint)rect.Width, (uint)rect.Height, 1)
            };
            count++;
        }

        if (count == 0) return;
        fixed (BufferImageCopy* regions = _copyRegions)
            _vk.CmdCopyBufferToImage(_commandBuffer, _stagingBuffer, _sourceImage, layout,
                (uint)count, regions);
    }

    private void CopySourceImage(int width, int height, ImageLayout layout)
    {
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1)
        };
        _vk.CmdCopyBufferToImage(_commandBuffer, _stagingBuffer, _sourceImage, layout, 1, &region);
    }

    private void CopyToStaging(ReadOnlySpan<byte> frame, int width, int height, int stride)
    {
        byte* dst = (byte*)_stagingMapped;
        int rowBytes = width * 4;
        fixed (byte* src = frame)
        {
            for (int y = 0; y < height; y++)
                System.Buffer.MemoryCopy(src + y * stride, dst + y * rowBytes, rowBytes, rowBytes);
        }
    }

    private void TransitionImage(Image image, ImageLayout oldLayout, ImageLayout newLayout, ImageSubresourceRange range)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range
        };

        PipelineStageFlags srcStage, dstStage;
        if (oldLayout == ImageLayout.Undefined)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (newLayout == ImageLayout.PresentSrcKhr)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = 0;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.BottomOfPipeBit;
        }
        else if (oldLayout == ImageLayout.PresentSrcKhr)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.BottomOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit | AccessFlags.TransferReadBit;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit | AccessFlags.TransferReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.TransferBit;
        }

        _vk.CmdPipelineBarrier(_commandBuffer, srcStage, dstStage, 0,
            0, null, 0, null, 1, &barrier);
    }

    private static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"{what} failed: {r}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
            _anime4k?.Dispose();
            _presenter?.Dispose();
            if (_composedView.Handle != 0) _vk.DestroyImageView(_device, _composedView, null);
            if (_composedImage.Handle != 0) _vk.DestroyImage(_device, _composedImage, null);
            if (_composedMemory.Handle != 0) _vk.FreeMemory(_device, _composedMemory, null);
            if (_sourceView.Handle != 0) _vk.DestroyImageView(_device, _sourceView, null);
            if (_stagingMapped != null) _vk.UnmapMemory(_device, _stagingMemory);
            if (_stagingBuffer.Handle != 0) _vk.DestroyBuffer(_device, _stagingBuffer, null);
            if (_stagingMemory.Handle != 0) _vk.FreeMemory(_device, _stagingMemory, null);
            if (_sourceImage.Handle != 0) _vk.DestroyImage(_device, _sourceImage, null);
            if (_sourceMemory.Handle != 0) _vk.FreeMemory(_device, _sourceMemory, null);
            if (_timestampPool.Handle != 0) _vk.DestroyQueryPool(_device, _timestampPool, null);
            if (_commandPool.Handle != 0) _vk.DestroyCommandPool(_device, _commandPool, null);
            if (_inFlight.Handle != 0) _vk.DestroyFence(_device, _inFlight, null);
            if (_imageAvailable.Handle != 0) _vk.DestroySemaphore(_device, _imageAvailable, null);
            foreach (var s in _renderSemaphores) if (s.Handle != 0) _vk.DestroySemaphore(_device, s, null);
            foreach (var v in _swapchainViews) if (v.Handle != 0) _vk.DestroyImageView(_device, v, null);
            if (_swapchain.Handle != 0) _swapchainExt.DestroySwapchain(_device, _swapchain, null);
            _vk.DestroyDevice(_device, null);
        }
        if (_surface.Handle != 0) _surfaceExt.DestroySurface(_instance, _surface, null);
        if (_instance.Handle != 0) _vk.DestroyInstance(_instance, null);
    }
}
