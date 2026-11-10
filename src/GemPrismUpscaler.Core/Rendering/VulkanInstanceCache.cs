using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace GemPrismUpscaler.Core.Rendering;

/// <summary>
/// One Vulkan instance per platform extension set, shared by every presenter
/// in the process.
///
/// <para>Windows come and go: a nested dialog gets its own host window and its
/// own <see cref="VulkanBlitter"/>, and closing it used to destroy that
/// blitter's instance while the game window kept presenting. With the NVIDIA
/// Wayland WSI that tears down driver state another surface still depends on —
/// the surviving window's next <c>vkQueuePresentKHR</c> jumped to a null
/// pointer inside libnvidia-glcore (SIGSEGV, ip=0). Destroying the surface but
/// keeping the instance is safe, so the instance is process-wide and lives
/// until exit; each window still owns its device, swapchain and surface.</para>
/// </summary>
internal static class VulkanInstanceCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Instance> Instances = new(StringComparer.Ordinal);

    /// <summary>
    /// Return the shared instance for <paramref name="extensions"/>, creating
    /// it on first use. The returned instance is owned by the cache and must
    /// never be destroyed by a blitter.
    /// </summary>
    public static Instance Get(Vk vk, string[] extensions)
    {
        string key = BuildKey(extensions);
        lock (Gate)
        {
            if (Instances.TryGetValue(key, out Instance existing)) return existing;
            Instance created = Create(vk, extensions);
            Instances[key] = created;
            return created;
        }
    }

    private static unsafe Instance Create(Vk vk, string[] extensions)
    {
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

            var extPtrs = extensions.Select(Marshal.StringToHGlobalAnsi).ToArray();
            bool enableValidation = DiagnosticsRuntime.ValidationLayerEnabled;
            var layers = enableValidation
                ? new[] { "VK_LAYER_KHRONOS_validation" }
                : Array.Empty<string>();
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
                    PpEnabledLayerNames = layerPtrs.Length == 0
                        ? null
                        : (byte**)lp.AddrOfPinnedObject()
                };

                Result r = vk.CreateInstance(&ci, null, out Instance instance);
                if (r != Result.Success && enableValidation)
                {
                    Log.Warn("Vulkan 校验层不可用，回退为无校验层创建实例");
                    ci.EnabledLayerCount = 0;
                    ci.PpEnabledLayerNames = null;
                    r = vk.CreateInstance(&ci, null, out instance);
                }
                if (r != Result.Success)
                    throw new InvalidOperationException($"vkCreateInstance failed: {r}");
                Log.Info($"vulkan instance created for [{BuildKey(extensions)}]");
                return instance;
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
    }

    private static string BuildKey(string[] extensions)
        => string.Join(",", extensions.OrderBy(e => e, StringComparer.Ordinal));
}