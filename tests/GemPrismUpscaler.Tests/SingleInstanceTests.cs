using GemPrismUpscaler.Core.Platform.Linux;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void SecondAcquireReportsExistingAndFirstReleaseRemovesSocket()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gp-instance-{Guid.NewGuid():N}.sock");
        SingleInstance? first = null;
        try
        {
            Assert.True(SingleInstance.TryAcquire(InstanceMode.Gui, out first, out var existing, path));
            Assert.NotNull(first);
            Assert.Null(existing);

            var query = SingleInstance.TryQuery(path, 800);
            Assert.NotNull(query);
            Assert.Equal(Environment.ProcessId, query!.Pid);
            Assert.Equal("gui", query.Mode);

            Assert.False(SingleInstance.TryAcquire(InstanceMode.Cli, out var second, out var duplicate, path));
            Assert.Null(second);
            Assert.NotNull(duplicate);
            Assert.Equal(Environment.ProcessId, duplicate!.Pid);
        }
        finally
        {
            first?.Dispose();
            try { File.Delete(path); } catch { }
        }

        Assert.Null(SingleInstance.TryQuery(path, 200));
    }

    [Fact]
    public void StatusProviderIsVisibleToQuery()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gp-status-{Guid.NewGuid():N}.sock");
        SingleInstance? instance = null;
        try
        {
            Assert.True(SingleInstance.TryAcquire(InstanceMode.Cli, out instance, out _, path));
            instance!.StatusProvider = () => new InstanceInfo(
                Environment.ProcessId, "cli", instance.StartedAt, true, "HDMI-A-1");

            var query = SingleInstance.TryQuery(path, 800);
            Assert.NotNull(query);
            Assert.True(query!.SessionActive);
            Assert.Equal("HDMI-A-1", query.Monitor);
        }
        finally
        {
            instance?.Dispose();
            try { File.Delete(path); } catch { }
        }
    }
    [Fact]
    public void StaleSocketFileIsReplaced()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gp-stale-{Guid.NewGuid():N}.sock");
        SingleInstance? instance = null;
        try
        {
            File.WriteAllText(path, "stale");
            Assert.True(SingleInstance.TryAcquire(InstanceMode.Gui, out instance, out var existing, path));
            Assert.NotNull(instance);
            Assert.Null(existing);
            Assert.NotNull(SingleInstance.TryQuery(path, 800));
        }
        finally
        {
            instance?.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

}
