using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux;

public enum InstanceMode
{
    Gui,
    Cli
}

/// <summary>
/// Snapshot of the running instance, exchanged by the single-instance socket.
/// </summary>
public sealed record InstanceInfo(
    int Pid,
    string Mode,
    DateTimeOffset StartedAt,
    bool SessionActive,
    string? Monitor)
{
    public string ModeLabel => Mode.Equals("cli", StringComparison.OrdinalIgnoreCase) ? "CLI" : "GUI";

    public string DisplayText
    {
        get
        {
            var text = $"PID {Pid}（{ModeLabel}）";
            if (SessionActive) text += "，正在超分";
            if (!string.IsNullOrWhiteSpace(Monitor)) text += $"，显示器 {Monitor}";
            return text;
        }
    }
}

/// <summary>
/// Cross-process single-instance guard and status endpoint.
///
/// A Unix domain socket in $XDG_RUNTIME_DIR is bound exclusively: bind is
/// atomic, the socket file disappears when the process exits, and a stale file
/// can be detected by a failed connect. M1a only answers STATUS; activation of
/// an existing UI is a later setting and deliberately not implemented here.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string DefaultName = "gemprism-upscaler";
    private const int MaxPacket = 1024;

    private readonly Socket? _listener;
    private readonly string _path;
    private readonly InstanceInfo _baseInfo;
    private readonly Thread? _acceptThread;
    private volatile Func<InstanceInfo>? _statusProvider;
    private volatile bool _disposed;

    private SingleInstance(Socket? listener, string path, InstanceInfo baseInfo)
    {
        _listener = listener;
        _path = path;
        _baseInfo = baseInfo;

        if (listener is not null)
        {
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "GemPrism-Instance"
            };
            _acceptThread.Start();
        }
    }

    public InstanceInfo BaseInfo => _baseInfo;
    public DateTimeOffset StartedAt => _baseInfo.StartedAt;
    public string SocketPath => _path;

    /// <summary>Dynamic status for --status and a second instance.</summary>
    public Func<InstanceInfo>? StatusProvider
    {
        get => _statusProvider;
        set => _statusProvider = value;
    }

    public InstanceInfo CurrentInfo
    {
        get
        {
            try { return _statusProvider?.Invoke() ?? _baseInfo; }
            catch { return _baseInfo; }
        }
    }

    public static string GetSocketPath(string name = DefaultName)
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var dir = !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime)
            ? runtime
            : Path.GetTempPath();
        return Path.Combine(dir, $"{name}-{GetUid()}.sock");
    }

    /// <summary>
    /// Tries to become the single instance. Returns false and the existing
    /// snapshot when another live process owns the socket. An unexpected socket
    /// setup failure is logged and treated as a degraded no-op guard so the
    /// upscaler still starts; it never masquerades as a duplicate instance.
    /// </summary>
    public static bool TryAcquire(
        InstanceMode mode,
        out SingleInstance? instance,
        out InstanceInfo? existing,
        string? path = null)
    {
        instance = null;
        existing = null;
        path ??= GetSocketPath();

        var modeText = mode == InstanceMode.Cli ? "cli" : "gui";
        var baseInfo = new InstanceInfo(Environment.ProcessId, modeText, DateTimeOffset.Now, false, null);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                socket.Bind(new UnixDomainSocketEndPoint(path));
                socket.Listen(4);
                TryRestrictPermissions(path);
                instance = new SingleInstance(socket, path, baseInfo);
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                socket.Dispose();
                existing = TryQueryPath(path, 350);
                if (existing is not null) return false;

                // No one answered: stale socket left by a crash. Remove and retry.
                try { File.Delete(path); } catch { }
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                Log.Write($"single-instance guard unavailable ({ex.GetType().Name}: {ex.Message}); continuing without it");
                instance = new SingleInstance(null, path, baseInfo);
                return true;
            }
        }

        existing = TryQueryPath(path, 350);
        if (existing is not null) return false;

        Log.Write("single-instance guard could not bind after retries; continuing without it");
        instance = new SingleInstance(null, path, baseInfo);
        return true;
    }

    public static InstanceInfo? TryQuery(string? path = null, int timeoutMs = 500)
    {
        return TryQueryPath(path ?? GetSocketPath(), timeoutMs);
    }

    private static InstanceInfo? TryQueryPath(string path, int timeoutMs)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            var connect = socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
            if (!connect.Wait(timeoutMs)) return null;
            socket.SendTimeout = timeoutMs;
            socket.ReceiveTimeout = timeoutMs;
            socket.Send(Encoding.UTF8.GetBytes("STATUS\n"));

            var buffer = new byte[MaxPacket];
            var text = new StringBuilder();
            while (text.Length < MaxPacket)
            {
                int read = socket.Receive(buffer);
                if (read <= 0) break;
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (text.ToString().Contains('\n')) break;
            }
            return ParseStatus(text.ToString());
        }
        catch
        {
            return null;
        }
    }

    private void AcceptLoop()
    {
        while (!_disposed && _listener is not null)
        {
            Socket client;
            try
            {
                client = _listener.Accept();
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
        }
    }

    private void HandleClient(Socket client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 1000;
                client.SendTimeout = 1000;

                var buffer = new byte[MaxPacket];
                var text = new StringBuilder();
                while (text.Length < MaxPacket)
                {
                    int read = client.Receive(buffer);
                    if (read <= 0) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    if (text.ToString().Contains('\n')) break;
                }

                string command = text.ToString().Trim();
                string response = command.Equals("STATUS", StringComparison.OrdinalIgnoreCase)
                    ? FormatStatus(CurrentInfo)
                    : "error unknown-command\n";
                client.Send(Encoding.UTF8.GetBytes(response));
            }
            catch
            {
                // Status is best-effort; never let a client take down the app.
            }
        }
    }

    private static string FormatStatus(InstanceInfo info) =>
        $"ok pid={info.Pid} mode={info.Mode} started={info.StartedAt.ToUnixTimeSeconds()} " +
        $"session={(info.SessionActive ? 1 : 0)} monitor={Sanitize(info.Monitor)}\n";

    private static string Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Replace(' ', '_');

    private static InstanceInfo? ParseStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (!line.StartsWith("ok ", StringComparison.Ordinal)) return null;

        int pid = 0;
        string mode = "?";
        long started = 0;
        bool session = false;
        string? monitor = null;

        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq];
            string value = part[(eq + 1)..];
            switch (key)
            {
                case "pid": int.TryParse(value, out pid); break;
                case "mode": mode = value; break;
                case "started": long.TryParse(value, out started); break;
                case "session": session = value is "1" or "true"; break;
                case "monitor": monitor = value is "-" ? null : value; break;
            }
        }

        if (pid <= 0) return null;
        return new InstanceInfo(pid, mode, DateTimeOffset.FromUnixTimeSeconds(started), session, monitor);
    }

    private static void TryRestrictPermissions(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort; the runtime directory is already user-private.
        }
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _listener?.Close(); } catch { }
        try
        {
            if (_acceptThread is not null && _acceptThread != Thread.CurrentThread)
                _acceptThread.Join(TimeSpan.FromSeconds(1));
        }
        catch { }

        try { if (_listener is not null) File.Delete(_path); } catch { }
    }
}
