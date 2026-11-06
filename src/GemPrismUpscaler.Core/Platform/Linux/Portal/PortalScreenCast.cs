using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Platform.Linux.Portal.DBus;
using Tmds.DBus.Protocol;

namespace GemPrismUpscaler.Core.Platform.Linux.Portal;

public enum PortalSourceType : uint
{
    Monitor = 1,
    Window = 2,
    Virtual = 4
}

public sealed record PortalStreamInfo(
    uint NodeId,
    ulong PipeWireSerial,
    int Width,
    int Height,
    uint SourceType,
    string MappingId)
{
    public override string ToString() =>
        $"node={NodeId} serial={PipeWireSerial} {Width}x{Height} source={SourceType} mapping='{MappingId}'";
}

public sealed record PortalScreenCastSession(
    DBusConnection Connection,
    ObjectPath SessionHandle,
    SafeHandle PipeWireFd,
    IReadOnlyList<PortalStreamInfo> Streams,
    string? RestoreToken) : IDisposable
{
    public void Dispose()
    {
        // The client that created this session owns the DBusConnection.
        try { PipeWireFd.Dispose(); } catch { }
    }
}

/// <summary>
/// Minimal xdg-desktop-portal ScreenCast client. It creates a remote-desktop
/// style session, lets the user pick a window/monitor, and returns the
/// PipeWire fd plus stream metadata. The caller owns the returned session.
/// </summary>
public sealed class PortalScreenCastClient : IDisposable
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private static int _tokenCounter;

    private readonly DBusConnection _connection;
    private readonly DBusService _service;
    private readonly ScreenCast _screenCast;

    public PortalScreenCastClient()
    {
        var address = DBusAddress.Session
                      ?? throw new InvalidOperationException("D-Bus session bus unavailable");
        _connection = new DBusConnection(address);
        _connection.ConnectAsync().GetAwaiter().GetResult();
        _service = new DBusService(_connection, PortalService);
        _screenCast = _service.CreateScreenCast(new ObjectPath(PortalPath));
    }

    public async Task<PortalScreenCastSession> StartAsync(
        PortalSourceType sourceType,
        string? restoreToken = null,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));

        string createToken = NextToken("create");
        var createWatch = WatchResponse(createToken, deadline);
        var createOptions = new Dictionary<string, VariantValue>
        {
            ["handle_token"] = createToken,
            ["session_handle_token"] = NextToken("session")
        };
        ObjectPath createHandle = await _screenCast.CreateSessionAsync(createOptions);
        Log.Debug($"portal CreateSession request={createHandle}");
        var createResponse = await createWatch.Task;
        createWatch.Observer.Dispose();
        if (createResponse.Response != 0)
            throw new InvalidOperationException($"portal CreateSession failed: response={createResponse.Response}");
        string sessionHandleText = GetString(createResponse.Results, "session_handle")
            ?? throw new InvalidOperationException("portal CreateSession returned no session_handle");
        var sessionHandle = new ObjectPath(sessionHandleText);
        Log.Debug($"portal session={sessionHandle}");

        string selectToken = NextToken("select");
        var selectWatch = WatchResponse(selectToken, deadline);
        var selectOptions = new Dictionary<string, VariantValue>
        {
            ["handle_token"] = selectToken,
            ["types"] = (uint)sourceType,
            ["multiple"] = false,
            ["cursor_mode"] = 1u, // hidden
            ["persist_mode"] = 2u // persist until revoked
        };
        if (!string.IsNullOrWhiteSpace(restoreToken))
            selectOptions["restore_token"] = restoreToken;

        ObjectPath selectHandle = await _screenCast.SelectSourcesAsync(sessionHandle, selectOptions);
        Log.Debug($"portal SelectSources request={selectHandle}");
        var selectResponse = await selectWatch.Task;
        selectWatch.Observer.Dispose();
        if (selectResponse.Response != 0)
            throw new InvalidOperationException($"portal SelectSources failed: response={selectResponse.Response}");

        string startToken = NextToken("start");
        var startWatch = WatchResponse(startToken, deadline);
        var startOptions = new Dictionary<string, VariantValue>
        {
            ["handle_token"] = startToken
        };
        Log.Info("portal Start: waiting for the system window picker");
        ObjectPath startHandle = await _screenCast.StartAsync(sessionHandle, string.Empty, startOptions);
        Log.Debug($"portal Start request={startHandle}");
        var startResponse = await startWatch.Task;
        startWatch.Observer.Dispose();
        if (startResponse.Response != 0)
            throw new InvalidOperationException($"portal Start failed: response={startResponse.Response}");

        var streams = ParseStreams(startResponse.Results);
        string? newToken = GetString(startResponse.Results, "restore_token");
        SafeHandle fd = await _screenCast.OpenPipeWireRemoteAsync(sessionHandle,
            new Dictionary<string, VariantValue>());

        Log.Info($"portal screencast started streams={streams.Count} token={(newToken is null ? "-" : "yes")}");
        foreach (var stream in streams) Log.Info($"portal stream {stream}");

        return new PortalScreenCastSession(_connection, sessionHandle, fd, streams, newToken);
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
        var wrapped = WaitWithTimeout(completion.Task, timeout);
        return (wrapped, observer);
    }

    private static async Task<(uint Response, Dictionary<string, VariantValue> Results)> WaitWithTimeout(
        Task<(uint Response, Dictionary<string, VariantValue> Results)> task, Task timeout)
    {
        var completed = await Task.WhenAny(task, timeout);
        if (completed != task)
            throw new TimeoutException("portal response timeout (user did not answer)");
        return await task;
    }

    private string BuildRequestPath(string handleToken)
    {
        string sender = _connection.UniqueName ?? string.Empty;
        if (sender.StartsWith(":", StringComparison.Ordinal)) sender = sender[1..];
        sender = sender.Replace('.', '_');
        return $"/org/freedesktop/portal/desktop/request/{sender}/{handleToken}";
    }

    private static List<PortalStreamInfo> ParseStreams(Dictionary<string, VariantValue> results)
    {
        var list = new List<PortalStreamInfo>();
        if (!results.TryGetValue("streams", out var streams) ||
            streams.Type != VariantValueType.Array)
            return list;

        for (int i = 0; i < streams.Count; i++)
        {
            var tuple = streams.GetItem(i);
            if (tuple.Count < 2) continue;
            uint node = GetUInt32(tuple.GetItem(0));
            var props = tuple.GetItem(1);
            if (props.Type != VariantValueType.Dictionary) continue;
            var dict = props.GetDictionary<string, VariantValue>();

            int width = 0, height = 0;
            if (dict.TryGetValue("size", out var size) && size.Type == VariantValueType.Struct && size.Count >= 2)
            {
                width = (int)GetUInt32(size.GetItem(0));
                height = (int)GetUInt32(size.GetItem(1));
            }
            ulong serial = dict.TryGetValue("pipewire-serial", out var serialValue)
                ? GetUInt64(serialValue)
                : 0;
            uint source = dict.TryGetValue("source_type", out var sourceValue)
                ? GetUInt32(sourceValue)
                : 0;
            string mapping = dict.TryGetValue("mapping_id", out var mappingValue)
                ? (mappingValue.Type == VariantValueType.String ? mappingValue.GetString() : string.Empty)
                : string.Empty;

            list.Add(new PortalStreamInfo(node, serial, width, height, source, mapping));
        }
        return list;
    }

    private static string? GetString(Dictionary<string, VariantValue> results, string key)
        => results.TryGetValue(key, out var value) && value.Type == VariantValueType.String
            ? value.GetString()
            : null;

    private static uint GetUInt32(VariantValue value) => value.Type switch
    {
        VariantValueType.UInt32 => value.GetUInt32(),
        VariantValueType.Int32 => (uint)value.GetInt32(),
        VariantValueType.UInt64 => (uint)value.GetUInt64(),
        VariantValueType.Int64 => (uint)value.GetInt64(),
        _ => 0
    };

    private static ulong GetUInt64(VariantValue value) => value.Type switch
    {
        VariantValueType.UInt64 => value.GetUInt64(),
        VariantValueType.Int64 => (ulong)value.GetInt64(),
        VariantValueType.UInt32 => value.GetUInt32(),
        VariantValueType.Int32 => (ulong)value.GetInt32(),
        _ => 0
    };

    private static string NextToken(string prefix)
        => $"g{prefix}{Environment.ProcessId}{Interlocked.Increment(ref _tokenCounter)}";

    public void Dispose() => _connection.Dispose();
}
