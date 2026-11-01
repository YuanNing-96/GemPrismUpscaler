using System.Diagnostics;

namespace GemPrismUpscaler.Core.Engine;

public sealed class FrameStats
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _captured;
    private long _presented;
    private long _lastCaptured;
    private long _lastPresented;
    private double _lastReport;

    public double Fps { get; private set; }
    public long Captured => Interlocked.Read(ref _captured);
    public long Presented => Interlocked.Read(ref _presented);

    public void OnCaptured() => Interlocked.Increment(ref _captured);
    public void OnPresented() => Interlocked.Increment(ref _presented);

    /// <summary>Returns a report once per second.</summary>
    public string? Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastReport < 1.0) return null;
        double dt = now - _lastReport;
        _lastReport = now;
        long c = Interlocked.Read(ref _captured);
        long p = Interlocked.Read(ref _presented);
        Fps = (p - _lastPresented) / dt;
        var report = $"fps={Fps:F1} captured={c - _lastCaptured} presented={p - _lastPresented} totalCapture={c} totalPresent={p}";
        _lastCaptured = c;
        _lastPresented = p;
        return report;
    }
}