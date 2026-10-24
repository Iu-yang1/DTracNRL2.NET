using System.Net;
using System.Net.Sockets;
using DTrac.Nrl2.Protocol;

namespace DTrac.Nrl2.Relay;

/// <summary>
/// An OBSERVE-ONLY UDP listener for controlled RC-3040S NRL2 interop tests.
/// It never transmits a UDP packet. IDs are masked in its public observations.
/// Only datagrams addressed to this listener are visible; it is not a LAN packet sniffer.
/// </summary>
public sealed class Nrl2PassiveMonitor : IDisposable
{
    private readonly UdpClient listener;
    private bool disposed;

    public Nrl2PassiveMonitor(IPEndPoint bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        if (bind.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only local IPv4 bindings supported.", nameof(bind));
        listener = new UdpClient(bind);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)listener.Client.LocalEndPoint!;

    public event Action<Nrl2PassiveObservation>? FrameObserved;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult inbound;
            try { inbound = await listener.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            if (TryDescribe(inbound.Buffer, inbound.RemoteEndPoint, out var observation))
                FrameObserved?.Invoke(observation);
        }
    }

    public static bool TryDescribe(ReadOnlySpan<byte> bytes, IPEndPoint remote,
        out Nrl2PassiveObservation observation)
    {
        ArgumentNullException.ThrowIfNull(remote);
        observation = default;
        Nrl2LengthEncoding dialect;
        if (Nrl2Frame.TryParse(bytes, out var frame, Nrl2LengthEncoding.FirmwareBigEndian))
            dialect = Nrl2LengthEncoding.FirmwareBigEndian;
        else if (Nrl2Frame.TryParse(bytes, out frame, Nrl2LengthEncoding.AppAsciiDecimal))
            dialect = Nrl2LengthEncoding.AppAsciiDecimal;
        else return false;

        observation = new Nrl2PassiveObservation(
            remote.ToString(),
            dialect,
            frame.Type,
            bytes.Length,
            MaskIdentifier(frame.SourceId),
            MaskIdentifier(frame.TargetId),
            frame.Payload.Length);
        return true;
    }

    private static string MaskIdentifier(string id) =>
        id.Length >= 14 ? "**********" + id[^4..] : "(invalid)";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        listener.Dispose();
    }
}

public readonly record struct Nrl2PassiveObservation(
    string Sender,
    Nrl2LengthEncoding Encoding,
    byte MessageType,
    int FrameBytes,
    string MaskedSourceId,
    string MaskedTargetId,
    int PayloadBytes);
