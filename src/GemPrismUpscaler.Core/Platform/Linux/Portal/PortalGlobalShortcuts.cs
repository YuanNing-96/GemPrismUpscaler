using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Platform.Linux.Portal.DBus;
using Tmds.DBus.Protocol;

namespace GemPrismUpscaler.Core.Platform.Linux.Portal;

/// <summary>
/// Minimal xdg-desktop-portal GlobalShortcuts client. Unlike X11 XGrabKey this
/// keeps working while a native Wayland window owns the keyboard focus. KDE
/// presents an approval dialog on the first BindShortcuts call.
/// </summary>
public sealed class PortalGlobalShortcuts : IDisposable
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private static int _tokenCounter;

    private readonly DBusConnection _connection;
    private readonly DBusService _service;
    private readonly GlobalShortcuts _shortcuts;
    private readonly Registry _registry;
    private ObjectPath? _session;
    private IDisposable? _activatedWatch;
    private bool _disposed;

    public event Action<string>? Activated;

    public PortalGlobalShortcuts()
    {
        var address = DBusAddress.Session
                      ?? throw new InvalidOperationException("D-Bus session bus unavailable");
        _connection = new DBusConnection(address);
        _connection.ConnectAsync().GetAwaiter().GetResult();
        _service = new DBusService(_connection, PortalService);
        _shortcuts = _service.CreateGlobalShortcuts(new ObjectPath(PortalPath));
        _registry = _service.CreateRegistry(new ObjectPath(PortalPath));
    }

    /// <summary>
    /// Registers this D-Bus connection with an application id. Must run before
    /// the first portal method call on the connection.
    /// </summary>
    private async Task RegisterAsync()
    {
        PortalAppId.EnsureDesktopFile();
        try
        {
            await _registry.RegisterAsync(PortalAppId.Value,
                new Dictionary<string, VariantValue>()).WaitAsync(TimeSpan.FromSeconds(5));
            Log.Debug($"portal app id registered: {PortalAppId.Value}");
        }
        catch (TimeoutException)
        {
            // Do not issue more calls on a connection whose registration is
            // still pending; abort only the shortcut binding (capture uses a
            // separate connection and is unaffected).
            throw new TimeoutException("portal app id registration timed out");
        }
    }

    public async Task<bool> BindAsync(string shortcutId, string description, string trigger,
        TimeSpan? timeout = null)
    {
        Log.Debug($"portal global-shortcuts: begin id={shortcutId} trigger='{trigger}'");
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        try
        {
            await RegisterAsync();
            Log.Debug("portal global-shortcuts: app id step done, creating session");
            string createToken = NextToken("shortcutcreate");
            var createWatch = WatchResponse(createToken, deadline);
            ObjectPath createHandle = await _shortcuts.CreateSessionAsync(
                new Dictionary<string, VariantValue>
                {
                    ["handle_token"] = createToken,
                    ["session_handle_token"] = NextToken("shortcutsession")
                });
            Log.Debug($"portal global-shortcuts CreateSession request={createHandle}");
            var createResponse = await createWatch.Task;
            createWatch.Observer.Dispose();
            if (createResponse.Response != 0)
            {
                Log.Warn($"portal global-shortcuts CreateSession failed: response={createResponse.Response}");
                return false;
            }
            string? sessionText = GetString(createResponse.Results, "session_handle");
            if (string.IsNullOrWhiteSpace(sessionText))
            {
                Log.Warn("portal global-shortcuts returned no session_handle");
                return false;
            }
            _session = new ObjectPath(sessionText);

            // Subscribe before BindShortcuts: the signal can arrive as soon as
            // the user presses the key after approving the dialog.
            Log.Debug($"portal global-shortcuts: session={_session}");
            _activatedWatch = await _shortcuts.WatchActivatedAsync(
                n =>
                {
                    if (_session is { } expected && n.SessionHandle != expected) return;
                    Log.Info($"portal global-shortcuts activated id={n.ShortcutId}");
                    Activated?.Invoke(n.ShortcutId);
                });

            string bindToken = NextToken("shortcutbind");
            var bindWatch = WatchResponse(bindToken, deadline);
            ObjectPath bindHandle = await _shortcuts.BindShortcutsAsync(
                _session.Value,
                [(shortcutId, new Dictionary<string, VariantValue>
                {
                    ["description"] = description,
                    ["preferred_trigger"] = trigger
                })],
                string.Empty,
                new Dictionary<string, VariantValue> { ["handle_token"] = bindToken });
            Log.Debug($"portal global-shortcuts BindShortcuts request={bindHandle}");
            var bindResponse = await bindWatch.Task;
            bindWatch.Observer.Dispose();
            if (bindResponse.Response != 0)
            {
                Log.Warn($"portal global-shortcuts BindShortcuts failed: response={bindResponse.Response}");
                return false;
            }
            var bound = ParseBoundShortcuts(bindResponse.Results);
            if (bound.Count == 0)
            {
                Log.Warn($"portal global-shortcuts: no shortcut was granted for id={shortcutId}");
                return false;
            }
            foreach (var shortcut in bound)
            {
                Log.Info($"portal global-shortcuts bound id={shortcut.Id} trigger='{shortcut.Trigger}'");
                if (string.IsNullOrWhiteSpace(shortcut.Trigger) ||
                    string.Equals(shortcut.Trigger, "none", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("portal global-shortcuts: shortcut has no active key; " +
                             "assign it in System Settings > Shortcuts > GemPrismUpscaler");
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            if (_disposed)
                Log.Debug($"portal global-shortcuts stopped: {ex.GetType().Name}: {ex.Message}");
            else
                Log.Warn($"portal global-shortcuts unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private (Task<(uint Response, Dictionary<string, VariantValue> Results)> Task, IDisposable Observer)
        WatchResponse(string handleToken, DateTime deadline)
    {
        string path = BuildRequestPath(handleToken);
        var request = _service.CreateRequest(new ObjectPath(path));
        var completion = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = request.WatchResponseAsync(
            result => completion.TrySetResult((result.Response, result.Results)))
            .AsTask().GetAwaiter().GetResult();

        var remaining = deadline - DateTime.UtcNow;
        var timeout = Task.Delay(remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining);
        return (WaitWithTimeout(completion.Task, timeout), observer);
    }

    private static async Task<(uint Response, Dictionary<string, VariantValue> Results)> WaitWithTimeout(
        Task<(uint Response, Dictionary<string, VariantValue> Results)> task, Task timeout)
    {
        var completed = await Task.WhenAny(task, timeout);
        if (completed != task)
            throw new TimeoutException("portal global-shortcuts response timeout");
        return await task;
    }

    private string BuildRequestPath(string handleToken)
    {
        string sender = _connection.UniqueName ?? string.Empty;
        if (sender.StartsWith(":", StringComparison.Ordinal)) sender = sender[1..];
        sender = sender.Replace('.', '_');
        return $"/org/freedesktop/portal/desktop/request/{sender}/{handleToken}";
    }

    private static List<(string Id, string Trigger)> ParseBoundShortcuts(
        Dictionary<string, VariantValue> results)
    {
        var list = new List<(string, string)>();
        if (!results.TryGetValue("shortcuts", out var shortcuts) ||
            shortcuts.Type != VariantValueType.Array)
            return list;
        for (int i = 0; i < shortcuts.Count; i++)
        {
            var tuple = shortcuts.GetItem(i);
            if (tuple.Count < 2 || tuple.GetItem(0).Type != VariantValueType.String) continue;
            string id = tuple.GetItem(0).GetString();
            var props = tuple.GetItem(1);
            string trigger = string.Empty;
            if (props.Type == VariantValueType.Dictionary)
            {
                var dict = props.GetDictionary<string, VariantValue>();
                if (dict.TryGetValue("trigger_description", out var value) &&
                    value.Type == VariantValueType.String)
                    trigger = value.GetString();
            }
            list.Add((id, trigger));
        }
        return list;
    }

    private static string? GetString(Dictionary<string, VariantValue> results, string key)
        => results.TryGetValue(key, out var value) && value.Type == VariantValueType.String
            ? value.GetString()
            : null;

    private static string NextToken(string prefix)
        => $"g{prefix}{Environment.ProcessId}{Interlocked.Increment(ref _tokenCounter)}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _activatedWatch?.Dispose(); } catch { }
        if (_session is { } session)
        {
            try { _service.CreateSession(session).CloseAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Debug($"portal global-shortcuts close: {ex.Message}"); }
        }
        _connection.Dispose();
    }
}