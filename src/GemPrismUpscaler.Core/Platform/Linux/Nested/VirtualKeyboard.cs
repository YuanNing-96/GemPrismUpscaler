using System.Diagnostics;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Persistent helper around the small C vkeyboard tool. NWayland 0.11 has no
/// zwp_virtual_keyboard binding, so the helper owns the Wayland keyboard object
/// and accepts one command per line on stdin:
///   K &lt;evdev&gt; &lt;state&gt;
///   M &lt;depressed&gt; &lt;latched&gt; &lt;locked&gt; &lt;group&gt;
///   Q
/// </summary>
public sealed class VirtualKeyboard : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly Task<string> _stderr;
    private bool _disposed;

    private VirtualKeyboard(Process process, StreamWriter input)
    {
        _process = process;
        _input = input;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static VirtualKeyboard? TryStart(string waylandDisplay)
    {
        string? path = ResolvePath();
        if (path is null)
        {
            Log.Warn("nested keyboard: gemprism-vkeyboard not found");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["WAYLAND_DISPLAY"] = waylandDisplay;
            var process = Process.Start(psi);
            if (process is null) return null;

            string? ready = null;
            var readyTask = Task.Run(() => process.StandardOutput.ReadLine());
            if (!readyTask.Wait(TimeSpan.FromSeconds(3)))
            {
                Log.Warn("nested keyboard: helper READY timeout");
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            ready = readyTask.Result;
            if (!string.Equals(ready, "READY", StringComparison.Ordinal))
            {
                Log.Warn($"nested keyboard: helper failed to start: '{ready}'");
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return new VirtualKeyboard(process, process.StandardInput);
        }
        catch (Exception ex)
        {
            Log.Warn($"nested keyboard: helper start failed: {ex.Message}");
            return null;
        }
    }

    public void Key(int evdevKeyCode, bool pressed)
    {
        if (_disposed) return;
        try
        {
            _input.WriteLine($"K {Math.Max(0, evdevKeyCode)} {(pressed ? 1 : 0)}");
            _input.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"nested keyboard: key write failed: {ex.Message}");
        }
    }

    public void Modifiers(OverlayModifiers modifiers)
    {
        if (_disposed) return;
        try
        {
            _input.WriteLine($"M {modifiers.Depressed} {modifiers.Latched} {modifiers.Locked} {modifiers.Group}");
            _input.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"nested keyboard: modifier write failed: {ex.Message}");
        }
    }

    public static string? ResolvePath()
    {
        string? env = Environment.GetEnvironmentVariable("GEMPRISM_VKEYBOARD");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        string bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "gemprism-vkeyboard");
        if (File.Exists(bundled)) return bundled;

        string local = Path.Combine(Environment.CurrentDirectory, "tools", "gemprism-vkeyboard");
        if (File.Exists(local)) return local;

        return File.Exists("/usr/local/bin/gemprism-vkeyboard")
            ? "/usr/local/bin/gemprism-vkeyboard" : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _input.WriteLine("Q"); _input.Flush(); } catch { }
        try { _input.Dispose(); } catch { }
        try
        {
            if (!_process.WaitForExit(1000))
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        try { _stderr.Wait(TimeSpan.FromMilliseconds(200)); } catch { }
        _process.Dispose();
    }
}
