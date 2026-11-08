using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Small i3-ipc client for the private sway instance. Only the requests used
/// by M2b are implemented: GET_TREE, GET_OUTPUTS and RUN_COMMAND.
/// </summary>
public sealed class SwayIpc : IDisposable
{
    private const string Magic = "i3-ipc";
    private const int HeaderSize = 14;
    private const uint GetTreeType = 4;
    private const uint GetOutputsType = 3;
    private const uint RunCommandType = 0;

    private readonly Socket _socket;
    private readonly object _gate = new();
    private bool _disposed;

    private SwayIpc(Socket socket) => _socket = socket;

    public static SwayIpc Connect(string socketPath, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
            throw new ArgumentException("sway socket path is required", nameof(socketPath));

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            ReceiveTimeout = (int)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds,
            SendTimeout = (int)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds
        };
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return new SwayIpc(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public JsonDocument GetTree() => JsonDocument.Parse(Request(GetTreeType));

    public JsonDocument GetOutputs() => JsonDocument.Parse(Request(GetOutputsType));

    public bool RunCommand(string command)
    {
        string json = Request(RunCommandType, command);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("success", out var success) && !success.GetBoolean())
                return false;
        }
        return true;
    }

    private string Request(uint type, string payload = "")
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        byte[] header = new byte[HeaderSize];
        Encoding.ASCII.GetBytes(Magic, header.AsSpan(0, Magic.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6, 4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10, 4), type);

        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SwayIpc));
            SendAll(header);
            if (body.Length > 0) SendAll(body);

            byte[] responseHeader = new byte[HeaderSize];
            ReceiveAll(responseHeader);
            if (Encoding.ASCII.GetString(responseHeader, 0, Magic.Length) != Magic)
                throw new InvalidDataException("invalid sway IPC magic");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(responseHeader.AsSpan(6, 4));
            if (length > 64 * 1024 * 1024)
                throw new InvalidDataException($"sway IPC response too large: {length}");
            byte[] response = new byte[length];
            if (length > 0) ReceiveAll(response);
            return Encoding.UTF8.GetString(response);
        }
    }

    private void SendAll(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int sent = _socket.Send(data);
            if (sent <= 0) throw new IOException("sway IPC send failed");
            data = data[sent..];
        }
    }

    private void ReceiveAll(Span<byte> data)
    {
        while (data.Length > 0)
        {
            int read = _socket.Receive(data);
            if (read <= 0) throw new IOException("sway IPC connection closed");
            data = data[read..];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }
}