using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Rotator;
using DTrac.Nrl2.Transport;

namespace DTrac.Nrl2.Relay;

/// <summary>
/// A local NRL2-UDP to GS-232-TCP/60020 protocol gateway. This does not require
/// any NRL2 firmware Socket7 setup or any DTrac relay server.
/// The gateway itself is a software protocol translator, NOT native firmware UDP/60059.
/// </summary>
public sealed class Nrl2TcpGateway : IDisposable
{
    private readonly UdpClient udp;
    private readonly string rotorId;
    private readonly string controllerHost;
    private readonly int controllerPort;
    private readonly bool allowMotorCommands;
    private readonly int maxAzimuth;
    private readonly int maxElevation;
    private readonly Dictionary<(string Sid, string Endpoint), DateTimeOffset> lastFreshPosition = new();
    private bool disposed;

    public event Action<Exception>? TransportFailure;

    /// <param name="listen">Use loopback for a local app; nonloopback requires an explicitly trusted firewall/VLAN.</param>
    /// <param name="controllerHost">RC-3040S LAN IPv4 address or resolvable hostname.</param>
    /// <param name="allowMotorCommands">False by default. The operator must enable movement deliberately.</param>
    public Nrl2TcpGateway(IPEndPoint listen, string rotorId, string controllerHost,
        int controllerPort = Rc3040sLanClient.DefaultPort,
        bool allowMotorCommands = false, int maxAzimuth = 360, int maxElevation = 90)
    {
        ArgumentNullException.ThrowIfNull(listen);
        if (listen.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4 interface required.", nameof(listen));
        if (string.IsNullOrWhiteSpace(controllerHost))
            throw new ArgumentException("Controller address required.", nameof(controllerHost));
        if (controllerPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(controllerPort));
        if (maxAzimuth is < 1 or > 450 || maxElevation is < 1 or > 180)
            throw new ArgumentOutOfRangeException(nameof(maxAzimuth));
        this.rotorId = Nrl2Frame.NormalizeId(rotorId);
        this.controllerHost = controllerHost;
        this.controllerPort = controllerPort;
        this.allowMotorCommands = allowMotorCommands;
        this.maxAzimuth = maxAzimuth;
        this.maxElevation = maxElevation;
        udp = new UdpClient(listen);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)udp.Client.LocalEndPoint!;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            try { await HandleAsync(packet, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
            {
                TransportFailure?.Invoke(ex);
            }
        }
    }

    private async Task HandleAsync(UdpReceiveResult packet, CancellationToken token)
    {
        if (!Nrl2Frame.TryParse(packet.Buffer, out var request) ||
            request.Type != Nrl2Frame.Gs232Type ||
            request.TargetId != rotorId ||
            request.SourceId == request.TargetId ||
            !Nrl2LanRelay.IsAllowedCommand(request.Payload, allowMotorCommands))
            return;

        string command = Encoding.ASCII.GetString(request.Payload);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var controller = await Rc3040sLanClient.ConnectAsync(
            controllerHost, controllerPort, deadline.Token).ConfigureAwait(false);

        if (command is "C2\r\n" or "C\r\n" or "B\r\n")
        {
            var position = await controller.QueryPositionAsync(TimeSpan.FromSeconds(2), deadline.Token)
                .ConfigureAwait(false);
            if (position is null) return;

            // Firmware response is not a protocol ACK; only record a recent validated TCP read.
            lastFreshPosition[(request.SourceId, packet.RemoteEndPoint.ToString())] = DateTimeOffset.UtcNow;
            int az = (int)Math.Round(position.Value.Azimuth, MidpointRounding.AwayFromZero);
            int el = (int)Math.Round(position.Value.Elevation, MidpointRounding.AwayFromZero);
            string line = command switch
            {
                "C\r\n" => FormattableString.Invariant($"AZ={az:000}\r\n"),
                "B\r\n" => FormattableString.Invariant($"EL={el:000}\r\n"),
                _ => FormattableString.Invariant($"AZ={az:000}  EL={el:000}\r\n")
            };
            byte[] response = Nrl2Frame.EncodeDevice(rotorId, request.SourceId,
                Encoding.ASCII.GetBytes(line), deviceTag: "LANBRDG ");
            await udp.SendAsync(response.AsMemory(), packet.RemoteEndPoint, token).ConfigureAwait(false);
            return;
        }

        if (command == "S\r\n")
        {
            await controller.StopAsync(deadline.Token).ConfigureAwait(false);
            lastFreshPosition.Remove((request.SourceId, packet.RemoteEndPoint.ToString()));
            return;
        }

        // A successful C2 on this gateway from the SAME UDP endpoint within 3 seconds
        // is a prerequisite to new motion; matching SID alone does not confer authority.
        // The application must still enforce real mechanical limits and provide physical STOP.
        if (!allowMotorCommands ||
            !lastFreshPosition.TryGetValue((request.SourceId, packet.RemoteEndPoint.ToString()), out var seenAt) ||
            DateTimeOffset.UtcNow - seenAt > TimeSpan.FromSeconds(3))
            return;

        if (command.Length == 10 && command[0] == 'W')
        {
            int az = int.Parse(command.AsSpan(1, 3), CultureInfo.InvariantCulture);
            int el = int.Parse(command.AsSpan(5, 3), CultureInfo.InvariantCulture);
            if (az > maxAzimuth || el > maxElevation) return;
            await controller.MoveToAsync(new RotatorPosition(az, el), deadline.Token).ConfigureAwait(false);
            lastFreshPosition.Remove((request.SourceId, packet.RemoteEndPoint.ToString()));
            return;
        }
        if (command.Length == 6 && command[0] == 'M')
        {
            int az = int.Parse(command.AsSpan(1, 3), CultureInfo.InvariantCulture);
            if (az > maxAzimuth) return;
            await controller.MoveAzimuthAsync(az, deadline.Token).ConfigureAwait(false);
            lastFreshPosition.Remove((request.SourceId, packet.RemoteEndPoint.ToString()));
            return;
        }
        // The gateway does not forward open-ended manual jogs without a validated
        // hardware failsafe. Direct TCP clients must explicitly coordinate jog + STOP.
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        udp.Dispose();
    }
}
