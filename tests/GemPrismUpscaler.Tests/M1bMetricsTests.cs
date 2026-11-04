using GemPrismUpscaler.Core.Engine;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class M1bMetricsTests
{
    private static FrameReport Report(bool gpuValid = true) => new(
        Fps: 59.5,
        Captured: 60,
        Presented: 60,
        CaptureAvgMs: 0.5,
        CaptureP95Ms: 0.9,
        DiffAvgMs: 0.7,
        DiffP95Ms: 1.1,
        UploadAvgMs: 0.3,
        UploadP95Ms: 0.4,
        EffectAvgMs: 3.2,
        EffectP95Ms: 4.1,
        PresentAvgMs: 0.2,
        PresentP95Ms: 0.3,
        GpuValid: gpuValid,
        GpuUploadMs: 0.3,
        GpuEffectMs: 2.9,
        GpuTailMs: 0.2,
        CpuPercent: 12.5,
        NvmlAvailable: true,
        GpuUtilPercent: 37,
        VramUsedMiB: 515,
        VramTotalMiB: 8188);

    [Fact]
    public void CsvHeaderAndLineHaveTheSameFieldCount()
    {
        string[] header = FrameReport.CsvHeader.Split(',');
        string[] line = Report().ToCsvLine().Split(',');
        Assert.Equal(23, header.Length);
        Assert.Equal(header.Length, line.Length);
    }

    [Fact]
    public void LogLineExposesStagesAndGpuTiming()
    {
        string line = Report().ToLogString();
        Assert.Contains("capture=0.50/0.90ms", line);
        Assert.Contains("diff=0.70/1.10ms", line);
        Assert.Contains("upload=0.30/0.40ms", line);
        Assert.Contains("effect=3.20/4.10ms", line);
        Assert.Contains("present=0.20/0.30ms", line);
        Assert.Contains("gpuUpload=0.30ms", line);
        Assert.Contains("gpuEffect=2.90ms", line);
        Assert.Contains("gpuUtil=37%", line);
        Assert.Contains("vram=515/8188MiB", line);
    }

    [Fact]
    public void IdleReportUsesGpuNotAvailable()
    {
        string line = Report(gpuValid: false).ToLogString();
        Assert.Contains("gpu=n/a", line);
        Assert.DoesNotContain("gpuUpload=", line);
    }

    [Fact]
    public void CsvUsesInvariantDecimalSeparator()
    {
        string line = Report().ToCsvLine();
        Assert.Contains(",0.500,0.900,0.700,1.100,", line);
        Assert.Contains(",3.200,", line);
        Assert.Contains(",12.50,", line);
    }
}