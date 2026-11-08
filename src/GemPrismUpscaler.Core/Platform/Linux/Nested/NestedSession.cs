using System.Diagnostics;
using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Owns one private headless sway instance and the command launched into it.
/// The session uses a private XDG_RUNTIME_DIR for sway itself, but exposes an
/// absolute WAYLAND_DISPLAY path to the child so the child can keep the host
/// XDG_RUNTIME_DIR (audio, PipeWire, D-Bus and portals keep working).
/// </summary>
public sealed class NestedSession : IDisposable
{
    private const string TokenEnv = "GEMPRISM_NESTED_TOKEN";

    private readonly string _swayPath;
    private readonly string? _swayMsgPath;
    private readonly string[] _command;
    private readonly int _width;
    private readonly int _height;
    private readonly string _token;
    private Process? _sway;
    private Process? _commandProcess;
    private Task<string>? _swayStderr;
    private string? _runtimeDir;
    private string? _configPath;
    private bool _disposed;
    private bool _stopped;
    private readonly object _stopGate = new();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    public int Width => _width;
    public int Height => _height;
    public string? RuntimeDirectory => _runtimeDir;
    public string? WaylandDisplay { get; private set; }
    public string? SwaySocket { get; private set; }
    public string? Display { get; private set; }
    public string Token => _token;
    public int SwayPid => _sway?.Id ?? 0;
    public int CommandPid => _commandProcess?.Id ?? 0;
    public bool IsRunning => _sway is { HasExited: false };

    public NestedSession(string swayPath, string[] command, int width = 1280, int height = 720,
        string? swayMsgPath = null, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(swayPath))
            throw new ArgumentException("sway path is required", nameof(swayPath));
        if (command.Length == 0)
            throw new ArgumentException("nested command is required", nameof(command));
        _swayPath = swayPath;
        _swayMsgPath = swayMsgPath;
        _command = command;
        _width = width;
        _height = height;
        _token = string.IsNullOrWhiteSpace(token)
            ? $"gp-{Environment.ProcessId}-{Guid.NewGuid():N}"
            : token;
    }

    /// <summary>
    /// Resolve the pinned sway binary: GEMPRISM_SWAY, then the bundled runtime
    /// directory, then the system sway as a development fallback.
    /// </summary>
    public static string ResolveSwayPath()
    {
        string? env = Environment.GetEnvironmentVariable("GEMPRISM_SWAY");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        string bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "sway", "sway");
        if (File.Exists(bundled))
            return bundled;

        return "sway";
    }

    public static string? ResolveSwayMsgPath()
    {
        string? env = Environment.GetEnvironmentVariable("GEMPRISM_SWAYMSG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        string bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "sway", "swaymsg");
        if (File.Exists(bundled))
            return bundled;

        string sibling = Path.Combine(AppContext.BaseDirectory, "runtime", "swaymsg", "swaymsg");
        if (File.Exists(sibling))
            return sibling;

        return File.Exists("/usr/bin/swaymsg") ? "/usr/bin/swaymsg" : null;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NestedSession));
        if (_sway is not null) throw new InvalidOperationException("nested session already started");

        CleanupStaleRuntimeDirectories();
        _runtimeDir = Path.Combine(Path.GetTempPath(),
            $"gemprism-nested-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runtimeDir);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_runtimeDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _configPath = Path.Combine(_runtimeDir, "sway.conf");
        SwayConfig.Write(_configPath, _width, _height);

        var psi = new ProcessStartInfo
        {
            FileName = _swayPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _runtimeDir
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(_configPath);
        psi.Environment["XDG_RUNTIME_DIR"] = _runtimeDir;
        psi.Environment["WLR_BACKENDS"] = "headless";
        psi.Environment["WLR_HEADLESS_OUTPUTS"] = "1";
        psi.Environment["WLR_LIBINPUT_NO_DEVICES"] = "1";
        psi.Environment[TokenEnv] = _token;

        Log.Info($"nested: starting sway path='{_swayPath}' output={_width}x{_height} runtime='{_runtimeDir}'");
        _sway = Process.Start(psi) ?? throw new InvalidOperationException("failed to start sway");
        _swayStderr = _sway.StandardError.ReadToEndAsync();
        _ = _sway.StandardOutput.ReadToEndAsync();

        WaitForSockets(TimeSpan.FromSeconds(8));
        DiscoverDisplay(TimeSpan.FromSeconds(8));
        Log.Info($"nested: sway pid={_sway.Id} wayland={WaylandDisplay} display={Display} " +
                 $"swaysock={SwaySocket} token={_token}");
    }

    /// <summary>
    /// Remove runtime directories left by a SIGKILL/crash. A directory is
    /// stale when its owning PID is gone; live sessions are never touched.
    /// </summary>
    private static void CleanupStaleRuntimeDirectories()
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(Path.GetTempPath(), "gemprism-nested-*"))
            {
                string name = Path.GetFileName(dir);
                string[] parts = name.Split('-');
                if (parts.Length < 4 || !int.TryParse(parts[2], out int pid)) continue;
                bool alive;
                try { using var process = Process.GetProcessById(pid); alive = !process.HasExited; }
                catch { alive = false; }
                if (alive) continue;
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { Log.Warn($"nested: stale runtime cleanup failed '{dir}': {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"nested: stale runtime scan failed: {ex.Message}");
        }
    }

    private void WaitForSockets(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (_sway!.HasExited)
                throw new InvalidOperationException(
                    $"sway exited during startup: {SwayFailureDetail()}");

            WaylandDisplay ??= Directory.GetFiles(_runtimeDir!, "wayland-*")
                .FirstOrDefault(path => !path.EndsWith(".lock", StringComparison.Ordinal));
            SwaySocket ??= Directory.GetFiles(_runtimeDir!, "sway-ipc.*.sock").FirstOrDefault();

            if (WaylandDisplay is not null && SwaySocket is not null)
                return;

            Thread.Sleep(40);
        }

        throw new TimeoutException($"sway socket timeout: {SwayFailureDetail()}");
    }

    private void DiscoverDisplay(TimeSpan timeout)
    {
        string probe = Path.Combine(_runtimeDir!, "display.txt");
        File.Delete(probe);
        string quoted = probe.Replace("'", "'\\''", StringComparison.Ordinal);
        string command = $"exec printf '%s' \"$DISPLAY\" > '{quoted}'";
        if (!TrySwayMsg(command))
            throw new InvalidOperationException("swaymsg exec failed while discovering DISPLAY");

        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (File.Exists(probe))
            {
                string value = File.ReadAllText(probe).Trim();
                if (value.Length > 0)
                {
                    Display = value;
                    return;
                }
            }
            Thread.Sleep(40);
        }

        throw new TimeoutException("nested XWayland DISPLAY was not reported by sway");
    }

    private bool TrySwayMsg(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _swayMsgPath ?? ResolveSwayMsgPath() ?? "swaymsg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(command);
            psi.Environment["SWAYSOCK"] = SwaySocket!;
            using var process = Process.Start(psi);
            if (process is null) return false;
            string stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            if (process.ExitCode != 0)
            {
                Log.Warn($"nested: swaymsg failed exit={process.ExitCode} stderr='{stderr.Trim()}'");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"nested: swaymsg unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Launch the command inside the nested session. Returns the child process
    /// exit code. The caller owns Start/Stop so it can print the environment
    /// before the child is spawned.
    /// </summary>
    public void StartCommand()
    {
        if (_sway is null || WaylandDisplay is null || Display is null || SwaySocket is null)
            throw new InvalidOperationException("nested session is not started");
        if (_commandProcess is not null)
            throw new InvalidOperationException("nested command already started");

        var psi = new ProcessStartInfo
        {
            FileName = _command[0],
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (string arg in _command.Skip(1))
            psi.ArgumentList.Add(arg);
        psi.Environment["WAYLAND_DISPLAY"] = WaylandDisplay;
        psi.Environment["DISPLAY"] = Display;
        psi.Environment["SWAYSOCK"] = SwaySocket;
        psi.Environment[TokenEnv] = _token;
        string? hostRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(hostRuntime))
            psi.Environment["XDG_RUNTIME_DIR"] = hostRuntime;

        Log.Info($"nested: launching command '{string.Join(" ", _command)}'");
        _commandProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to launch nested command");
    }

    public int WaitForCommand()
    {
        if (_commandProcess is null) throw new InvalidOperationException("nested command not started");
        _commandProcess.WaitForExit();
        Log.Info($"nested: command exited code={_commandProcess.ExitCode} pid={_commandProcess.Id}");
        return _commandProcess.ExitCode;
    }

    public int RunCommand()
    {
        StartCommand();
        return WaitForCommand();
    }

    public bool IsCommandRunning => _commandProcess is { HasExited: false };
    public int CommandExitCode => _commandProcess?.HasExited == true ? _commandProcess.ExitCode : 0;

    public void Stop()
    {
        lock (_stopGate)
        {
            if (_disposed || _stopped) return;
            try { StopCore(); }
            finally { _stopped = true; }
        }
    }

    private void StopCore()
    {
        try
        {
            if (_commandProcess is { HasExited: false })
            {
                Log.Info($"nested: stopping command pid={_commandProcess.Id}");
                _commandProcess.Kill(entireProcessTree: true);
                _commandProcess.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"nested: stop command failed: {ex.Message}");
        }

        if (_sway is { HasExited: false })
        {
            try
            {
                using var ipc = SwayIpc.Connect(SwaySocket!);
                ipc.RunCommand("exit");
                _sway.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                Log.Warn($"nested: sway exit command failed: {ex.Message}");
            }
            try
            {
                if (!_sway.HasExited)
                {
                    Log.Info($"nested: stopping sway pid={_sway.Id}");
                    _sway.Kill(entireProcessTree: true);
                    if (!_sway.WaitForExit(2000))
                    {
                        Log.Warn("nested: sway did not exit; sending SIGKILL");
                        kill(_sway.Id, 9);
                        _sway.WaitForExit(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"nested: stop sway failed: {ex.Message}");
            }
        }

        try { _swayStderr?.Wait(TimeSpan.FromMilliseconds(300)); } catch { }
        try
        {
            if (_runtimeDir is not null && Directory.Exists(_runtimeDir))
                Directory.Delete(_runtimeDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"nested: remove runtime dir failed: {ex.Message}");
        }
    }

    private string SwayFailureDetail()
    {
        try
        {
            if (_swayStderr is { IsCompleted: true })
                return _swayStderr.Result.Trim();
        }
        catch { }
        return "no stderr captured";
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _sway?.Dispose();
        _commandProcess?.Dispose();
        _sway = null;
        _commandProcess = null;
        _disposed = true;
    }
}