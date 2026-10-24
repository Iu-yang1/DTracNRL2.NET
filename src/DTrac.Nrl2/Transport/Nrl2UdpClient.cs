using System.Net.Sockets;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Rotator;

namespace DTrac.Nrl2.Transport;

/// <summary>
/// Sends NRL2 frames to an explicitly configured DTrac-compatible UDP relay.
/// UDP send completion is not a device acknowledgement, and matching IDs are not cryptographic auth.
/// </summary>
public sealed class Nrl2UdpClient : IDisposable
{
    private readonly UdpClient socket;
    private readonly SemaphoreSlim queryGate = new(1, 1);
    private readonly string appSid;
    private readonly string rotorId;
    private bool disposed;

    public Nrl2UdpClient(string relayHost, int relayPort, string appSid, string rotorId)
    {
        if (string.IsNullOrWhiteSpace(relayHost)) throw new ArgumentException("Relay host is required.", nameof(relayHost));
        if (relayPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(relayPort));
        this.appSid = Nrl2Frame.NormalizeId(appSid);
        this.rotorId = Nrl2Frame.NormalizeId(rotorId);
        socket = new UdpClient(AddressFamily.InterNetwork);
        try { socket.Connect(relayHost, relayPort); }
        catch { socket.Dispose(); throw; }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> gs232Command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var frame = Nrl2Frame.Encode(appSid, rotorId, gs232Command.Span);
        await socket.SendAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public Task MoveToAsync(RotatorPosition position, CancellationToken cancellationToken = default) =>
        SendAsync(Gs232Commands.MoveTo(position), cancellationToken);

    /// <summary>
    /// Repeated best-effort STOP as a transport mitigation only. A physical emergency stop is still required.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(Gs232Commands.Stop(), cancellationToken).ConfigureAwait(false);
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        await SendAsync(Gs232Commands.Stop(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sequential read-only query. No wire-level nonce or sequence number has been verified;
    /// a same-ID delayed reply can still be stale. The caller MUST apply its own freshness gate.
    /// </summary>
    public async Task<RotatorPosition?> QueryPositionAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ObjectDisposedException.ThrowIf(disposed, this);
        await queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await SendAsync(Gs232Commands.Query(), cts.Token).ConfigureAwait(false);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult answer;
                try { answer = await socket.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
                if (Nrl2Frame.TryDecodeReply(answer.Buffer, appSid, rotorId, out var body) &&
                    Gs232Commands.TryParsePosition(body, out var position))
                    return position;
            }
            return null;
        }
        finally { queryGate.Release(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        socket.Dispose();
        queryGate.Dispose();
    }
}
