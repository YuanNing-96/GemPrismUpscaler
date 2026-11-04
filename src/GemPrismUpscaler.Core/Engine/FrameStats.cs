using System.Diagnostics;
using System.Globalization;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Engine;

/// <summary>
/// One frame's timing sample. CPU values are measured around the work in
/// <see cref="UpscaleSession"/>; GPU values come from Vulkan timestamps and may
/// lag the CPU sample by one frame (the fence is waited on at the next submit).
/// </summary>
public readonly record struct FrameTimings(
    double CaptureMs,
    double DiffMs,
    double UploadCpuMs,
    double EffectCpuMs,
    double PresentCpuMs,
    double TotalCpuMs,
    double GpuUploadMs,
    double GpuEffectMs,
    double GpuTailMs,
    bool GpuValid);

/// <summary>Aggregated once-per-second report used by the log and the CSV sink.</summary>
public sealed record FrameReport(
    double Fps,
    long Captured,
    long Presented,
    double CaptureAvgMs,
    double CaptureP95Ms,
    double DiffAvgMs,
    double DiffP95Ms,
    double UploadAvgMs,
    double UploadP95Ms,
    double EffectAvgMs,
    double EffectP95Ms,
    double PresentAvgMs,
    double PresentP95Ms,
    bool GpuValid,
    double GpuUploadMs,
    double GpuEffectMs,
    double GpuTailMs,
    double CpuPercent,
    bool NvmlAvailable,
    double GpuUtilPercent,
    double VramUsedMiB,
    double VramTotalMiB)
{
    /// <summary>
    /// Human-readable one-line report. Stage values are average/p95 in
    /// milliseconds; GPU values are the last completed frame's timestamps.
    /// </summary>
    public string ToLogString()
    {
        string gpu = GpuValid
            ? $"gpuUpload={GpuUploadMs:F2}ms gpuEffect={GpuEffectMs:F2}ms gpuTail={GpuTailMs:F2}ms"
            : "gpu=n/a";
        string nvml = NvmlAvailable
            ? $"gpuUtil={GpuUtilPercent:F0}% vram={VramUsedMiB:F0}/{VramTotalMiB:F0}MiB"
            : "gpuUtil=n/a vram=n/a";
        return string.Format(CultureInfo.InvariantCulture,
            "fps={0:F1} captured={1} presented={2} " +
            "capture={3:F2}/{4:F2}ms diff={5:F2}/{6:F2}ms " +
            "upload={7:F2}/{8:F2}ms effect={9:F2}/{10:F2}ms " +
            "present={11:F2}/{12:F2}ms {13} cpu={14:F1}% {15}",
            Fps, Captured, Presented,
            CaptureAvgMs, CaptureP95Ms,
            DiffAvgMs, DiffP95Ms,
            UploadAvgMs, UploadP95Ms,
            EffectAvgMs, EffectP95Ms,
            PresentAvgMs, PresentP95Ms,
            gpu, CpuPercent, nvml);
    }

    public string ToCsvLine()
    {
        string timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.InvariantCulture,
            "{0},{1:F2},{2},{3},{4:F3},{5:F3},{6:F3},{7:F3},{8:F3},{9:F3}," +
            "{10:F3},{11:F3},{12:F3},{13:F3},{14},{15:F3},{16:F3},{17:F3}," +
            "{18:F2},{19},{20:F1},{21:F1},{22:F1}",
            timestamp, Fps, Captured, Presented,
            CaptureAvgMs, CaptureP95Ms,
            DiffAvgMs, DiffP95Ms,
            UploadAvgMs, UploadP95Ms,
            EffectAvgMs, EffectP95Ms,
            PresentAvgMs, PresentP95Ms,
            GpuValid ? 1 : 0, GpuUploadMs, GpuEffectMs, GpuTailMs,
            CpuPercent,
            NvmlAvailable ? 1 : 0, GpuUtilPercent, VramUsedMiB, VramTotalMiB);
    }

    public const string CsvHeader =
        "timestamp,fps,captured,presented," +
        "capture_avg_ms,capture_p95_ms," +
        "diff_avg_ms,diff_p95_ms," +
        "upload_avg_ms,upload_p95_ms," +
        "effect_avg_ms,effect_p95_ms," +
        "present_avg_ms,present_p95_ms," +
        "gpu_valid,gpu_upload_ms,gpu_effect_ms,gpu_tail_ms," +
        "cpu_percent," +
        "nvml_available,gpu_util_percent,vram_used_mib,vram_total_mib";
}

/// <summary>
/// Frame counter plus rolling timing samples. A report is produced once per
/// second; static scenes still produce a report (with zero frame samples) so
/// the idle GPU/CPU footprint is visible.
/// </summary>
public sealed class FrameStats
{
    private const int WindowSize = 256;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly double[] _capture = new double[WindowSize];
    private readonly double[] _diff = new double[WindowSize];
    private readonly double[] _upload = new double[WindowSize];
    private readonly double[] _effect = new double[WindowSize];
    private readonly double[] _present = new double[WindowSize];
    private int _head;
    private int _count;
    private long _captured;
    private long _presented;
    private long _lastCaptured;
    private long _lastPresented;
    private double _lastReport;
    private TimeSpan _lastCpuTime;
    private double _lastGpuUploadMs;
    private double _lastGpuEffectMs;
    private double _lastGpuTailMs;
    private bool _lastGpuValid;

    public FrameStats()
    {
        _lastCpuTime = _process.TotalProcessorTime;
    }

    public double Fps { get; private set; }
    public long Captured => Interlocked.Read(ref _captured);
    public long Presented => Interlocked.Read(ref _presented);

    public void OnCaptured() => Interlocked.Increment(ref _captured);
    public void OnPresented() => Interlocked.Increment(ref _presented);

    public void OnFrame(in FrameTimings timings)
    {
        _capture[_head] = timings.CaptureMs;
        _diff[_head] = timings.DiffMs;
        _upload[_head] = timings.UploadCpuMs;
        _effect[_head] = timings.EffectCpuMs;
        _present[_head] = timings.PresentCpuMs;
        _lastGpuUploadMs = timings.GpuUploadMs;
        _lastGpuEffectMs = timings.GpuEffectMs;
        _lastGpuTailMs = timings.GpuTailMs;
        _lastGpuValid = timings.GpuValid;
        _head = (_head + 1) % WindowSize;
        if (_count < WindowSize) _count++;
    }

    /// <summary>Returns a report once per second.</summary>
    public FrameReport? Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastReport < 1.0) return null;
        double dt = now - _lastReport;
        _lastReport = now;

        long c = Interlocked.Read(ref _captured);
        long p = Interlocked.Read(ref _presented);
        Fps = (p - _lastPresented) / dt;

        double cpuPercent = 0;
        TimeSpan cpuNow = _process.TotalProcessorTime;
        if (_lastCpuTime != TimeSpan.Zero && dt > 0)
            cpuPercent = (cpuNow - _lastCpuTime).TotalMilliseconds / (dt * 1000.0) * 100.0;
        _lastCpuTime = cpuNow;

        GpuSample gpu = NvmlMetrics.Sample();
        int count = _count;
        double captureAvg = Average(_capture, count);
        double captureP95 = Percentile(_capture, count, 0.95);
        double diffAvg = Average(_diff, count);
        double diffP95 = Percentile(_diff, count, 0.95);
        double uploadAvg = Average(_upload, count);
        double uploadP95 = Percentile(_upload, count, 0.95);
        double effectAvg = Average(_effect, count);
        double effectP95 = Percentile(_effect, count, 0.95);
        double presentAvg = Average(_present, count);
        double presentP95 = Percentile(_present, count, 0.95);
        bool gpuValid = count > 0 && _lastGpuValid;
        double gpuUpload = gpuValid ? _lastGpuUploadMs : 0;
        double gpuEffect = gpuValid ? _lastGpuEffectMs : 0;
        double gpuTail = gpuValid ? _lastGpuTailMs : 0;

        var report = new FrameReport(
            Fps,
            c - _lastCaptured,
            p - _lastPresented,
            captureAvg, captureP95,
            diffAvg, diffP95,
            uploadAvg, uploadP95,
            effectAvg, effectP95,
            presentAvg, presentP95,
            gpuValid, gpuUpload, gpuEffect, gpuTail,
            cpuPercent,
            gpu.Available, gpu.UtilizationPercent, gpu.MemoryUsedMiB, gpu.MemoryTotalMiB);

        _lastCaptured = c;
        _lastPresented = p;
        _head = 0;
        _count = 0;
        return report;
    }

    private static double Average(double[] values, int count)
    {
        if (count <= 0) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++) sum += values[i];
        return sum / count;
    }

    private static double Percentile(double[] values, int count, double percentile)
    {
        if (count <= 0) return 0;
        var copy = new double[count];
        Array.Copy(values, copy, count);
        Array.Sort(copy);
        int index = (int)Math.Ceiling(percentile * count) - 1;
        return copy[Math.Clamp(index, 0, count - 1)];
    }
}