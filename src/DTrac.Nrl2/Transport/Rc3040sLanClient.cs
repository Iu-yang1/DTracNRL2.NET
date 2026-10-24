using System.IO;
using System.Net.Sockets;
using DTrac.Nrl2.Rotator;

namespace DTrac.Nrl2.Transport;

/// <summary>
/// Direct LAN GS-232 TCP/60020 access to RC-3040S firmware.
/// No NRL2 encapsulation, APP SID, RotorID or relay service is involved.
/// </summary>
/// <remarks>
/// Network writes and received position lines are not authenticated motor acknowledgements.
/// No motion is performed on construction or connection. A physical emergency stop remains essential.
/// </remarks>
public sealed class Rc3040sLanClient : IDisposable
{
    public const int DefaultPort = 60020;
    private const int MaxReplyBytes = 256;
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SemaphoreSlim queryGate = new(1, 1);
    private bool disposed;

    private Rc3040sLanClient(TcpClient tcp)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
    }

    /// <summary>Connects without sending any rotator command.</summary>
    public static async Task<Rc3040sLanClient> ConnectAsync(
        string host, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A LAN host or IP address is required.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new Rc3040sLanClient(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads a single GS-232 C2 position report. Timeout returns null;
    /// a TCP disconnect throws EndOfStreamException. A valid report is not a motor ACK.
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
            using var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutToken.CancelAfter(timeout);
            try
            {
                await SendAsync(Gs232Commands.Query(), timeoutToken.Token).ConfigureAwait(false);
                byte[] line = new byte[MaxReplyBytes];
                byte[] chunk = new byte[64];
                int length = 0;
                while (true)
                {
                    int n = await stream.ReadAsync(chunk.AsMemory(), timeoutToken.Token).ConfigureAwait(false);
                    if (n == 0) throw new EndOfStreamException("RC-3040S LAN TCP connection closed.");
                    for (int i = 0; i < n; ++i)
                    {
                        byte b = chunk[i];
                        if (b == '\r' || b == '\n')
                        {
                            if (length > 0 && Gs232Commands.TryParsePosition(line.AsSpan(0, length), out var position))
                                return position;
                            length = 0;
                            continue;
                        }
                        if (length >= line.Length)
                            throw new InvalidDataException("RC-3040S position response exceeded 256 bytes.");
                        line[length++] = b;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            queryGate.Release();
        }
    }

    /// <summary>Explicit target move; caller must first enforce position freshness and mechanical limits.</summary>
    public Task MoveToAsync(RotatorPosition position, CancellationToken cancellationToken = default) =>
        SendAsync(Gs232Commands.MoveTo(position), cancellationToken);

    /// <summary>Explicit manual jog. Requires a corresponding STOP on release.</summary>
    public Task JogAsync(JogDirection direction, CancellationToken cancellationToken = default) =>
        SendAsync(Gs232Commands.Jog(direction), cancellationToken);

    /// <summary>
    /// Sends a single GS-232 STOP. Successful TCP write does not prove motor stoppage;
    /// use a physical emergency stop for safety.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        SendAsync(Gs232Commands.Stop(), cancellationToken);

    private async Task SendAsync(byte[] gs232Command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // RC-3040S local TCP uses CR-terminated GS-232 text; NRL2 APK uses CRLF.
        if (gs232Command.Length < 2 || gs232Command[^2] != '\r' || gs232Command[^1] != '\n')
            throw new InvalidDataException("Expected CRLF-terminated GS-232 command.");
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(gs232Command.AsMemory(0, gs232Command.Length - 1),
                cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tcp.Dispose();
        writeGate.Dispose();
        queryGate.Dispose();
    }
}
