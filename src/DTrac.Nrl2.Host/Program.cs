using System.Net;
using DTrac.Nrl2.Relay;
using DTrac.Nrl2.Protocol;

static void Help()
{
    Console.WriteLine("Local DTrac NRL2 experimental services (not production firmware validated).");
    Console.WriteLine("  --native-relay <listen-ip> <udp-port> <rotorid> <trusted-device-ip> [--enable-motion]");
    Console.WriteLine("  --tcp-gateway <listen-ip> <udp-port> <rotorid> <controller-host> [--enable-motion]");
    Console.WriteLine("Explicitly binding a LAN interface exposes unauthenticated command transport.");
    Console.WriteLine("Use a restrictive firewall/VLAN and hardware emergency stop; never expose it to the Internet.");
}

if (args.Length < 5 ||
    !IPAddress.TryParse(args[1], out var listen) ||
    !int.TryParse(args[2], out int port) || port is < 1 or > 65535)
{
    Help();
    return;
}
string id = Nrl2Frame.NormalizeId(args[3]);
bool allowMotion = args.Length == 6 && args[5] == "--enable-motion";
if (args.Length != 5 && !allowMotion)
{
    Help();
    return;
}
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

if (args[0] == "--native-relay" && IPAddress.TryParse(args[4], out var deviceIp))
{
    using var relay = new Nrl2LanRelay(new IPEndPoint(listen, port),
        new Dictionary<string, IPAddress> { [id] = deviceIp }, allowMotion);
    Console.WriteLine($"Native relay listening {relay.LocalEndPoint}; motion={allowMotion}; trusted device={deviceIp}.");
    await relay.RunAsync(stop.Token);
}
else if (args[0] == "--tcp-gateway")
{
    using var gateway = new Nrl2TcpGateway(new IPEndPoint(listen, port), id, args[4],
        allowMotorCommands: allowMotion);
    gateway.TransportFailure += e => Console.Error.WriteLine($"TCP transport: {e.Message}");
    Console.WriteLine($"UDP/TCP gateway listening {gateway.LocalEndPoint} -> {args[4]}:60020; motion={allowMotion}.");
    await gateway.RunAsync(stop.Token);
}
else Help();
