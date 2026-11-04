using System.Runtime.InteropServices;

namespace GemPrismUpscaler.Core.Diagnostics;

internal readonly record struct GpuSample(
    bool Available,
    double UtilizationPercent,
    double MemoryUsedMiB,
    double MemoryTotalMiB);

/// <summary>
/// Optional NVML sampling for the dashboard/log: card-wide GPU utilization and
/// memory. It is deliberately best-effort; AMD/Intel or a missing
/// libnvidia-ml simply report "not available" and never affect rendering.
/// </summary>
internal static class NvmlMetrics
{
    private const string Library = "libnvidia-ml.so.1";

    private static readonly Lazy<State> LazyState = new(Init, LazyThreadSafetyMode.ExecutionAndPublication);

    public static GpuSample Sample()
    {
        State state = LazyState.Value;
        if (!state.Available) return default;

        try
        {
            if (nvmlDeviceGetUtilizationRates(state.Device, out var utilization) != 0)
                return default;
            if (nvmlDeviceGetMemoryInfo(state.Device, out var memory) != 0)
                return default;
            return new GpuSample(
                true,
                utilization.Gpu,
                memory.Used / (1024.0 * 1024.0),
                memory.Total / (1024.0 * 1024.0));
        }
        catch
        {
            return default;
        }
    }

    private static State Init()
    {
        try
        {
            if (nvmlInit_v2() != 0) return State.Unavailable;
            if (nvmlDeviceGetCount_v2(out uint count) != 0 || count == 0) return State.Unavailable;
            if (nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device) != 0) return State.Unavailable;
            return new State(true, device);
        }
        catch (DllNotFoundException)
        {
            return State.Unavailable;
        }
        catch (EntryPointNotFoundException)
        {
            return State.Unavailable;
        }
        catch (BadImageFormatException)
        {
            return State.Unavailable;
        }
    }

    private sealed record State(bool Available, IntPtr Device)
    {
        public static readonly State Unavailable = new(false, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlInit_v2();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetCount_v2(out uint count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
}