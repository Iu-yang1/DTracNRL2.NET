using System.IO;
using System.Net.WebSockets;
using System.Text;
using DTrac.Nrl2.Rotator;

namespace DTrac.Nrl2.Transport;

/// <summary>
/// Experimental, protocol-grounded RC-3040S management WebSocket client on TCP/60030.
/// The firmware contains RFC 6455 Upgrade and frame-processing routines.
/// Application-level command acknowledgement is NOT verified on real hardware.
/// </summary>
public sealed class Rc3040sWebSocketClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 4096;
    private readonly ClientWebSocket socket;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly SemaphoreSlim receiveGate = new(1, 1);
    private bool disposed;

    private Rc3040sWebSocketClient(ClientWebSocket socket) => this.socket = socket;

    /// <summary>
    /// Performs only an HTTP WebSocket upgrade; never transmits any AT or motor command.
    /// Uses the '/' path observed in firmware ("GET / HTTP").
    /// </summary>
    public static async Task<Rc3040sWebSocketClient> ConnectAsync(
        string host, int port = Rc3040sNetworkPorts.ManagementTcpPort,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains('\\') ||
            host.Any(char.IsWhiteSpace))
            throw new ArgumentException("Supply a hostname or IP, not a URL/path.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var ws = new ClientWebSocket();
        try
        {
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await ws.ConnectAsync(new UriBuilder(Uri.UriSchemeWs, host, port, "/").Uri,
                cancellationToken).ConfigureAwait(false);
            return new Rc3040sWebSocketClient(ws);
        }
        catch { ws.Dispose(); throw; }
    }

    public WebSocketState State => socket.State;

    /// <summary>
    /// Returns one received text message, or null when the peer closes.
    /// This is a raw application message (welcome/status/errors), not a position/motor ACK.
    /// </summary>
    public async Task<string?> ReceiveTextAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ObjectDisposedException.ThrowIf(disposed, this);
        await receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            byte[] buffer = new byte[512];
            using var assembled = new MemoryStream();
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new InvalidDataException("Unexpected non-text management message.");
                    if (assembled.Length + result.Count > MaximumMessageBytes)
                        throw new InvalidDataException("WebSocket management reply exceeds safe bound.");
                    assembled.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                        return new UTF8Encoding(false, true).GetString(assembled.ToArray());
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
        finally { receiveGate.Release(); }
    }

    /// <summary>
    /// Send an explicitly selected plain-text WebSocket payload. Firmware application
    /// semantics are NOT fully verified. This method never infers success from SendAsync.
    /// </summary>
    public async Task SendTextAsync(string payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] data = new UTF8Encoding(false, true).GetBytes(payload);
        if (data.Length is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(payload), "Unverified firmware frame size; cap payload at 256 bytes.");
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text,
                true, cancellationToken).ConfigureAwait(false);
        }
        finally { sendGate.Release(); }
    }

    /// <summary>
    /// Explicit write with two independent opt-ins: the caller selected the AT command,
    /// and explicitly confirms the possibility of nonvolatile device changes.
    /// No startup, reconnect, or receive code calls this method.
    /// </summary>
    public Task SendFirmwareCommandAsync(byte[] command, bool confirmPossiblePersistentWrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!confirmPossiblePersistentWrite)
            throw new InvalidOperationException("Explicit operator confirmation is required for firmware AT writes.");
        if (command.Length is < 3 or > 128 || command[0] != (byte)'A' ||
            command[1] != (byte)'T' || command[^1] != (byte)'\r' ||
            command.AsSpan().IndexOf((byte)'\n') >= 0 ||
            command.Any(b => b != '\r' && (b < 32 || b > 126)))
            throw new ArgumentException("Only bounded printable CR-terminated AT commands are accepted.", nameof(command));
        return SendTextAsync(Encoding.ASCII.GetString(command), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        socket.Abort();
        socket.Dispose();
        sendGate.Dispose();
        receiveGate.Dispose();
        await ValueTask.CompletedTask;
    }
}
