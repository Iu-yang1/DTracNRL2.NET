using DTrac.Nrl2.Discovery;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Rotator;
using DTrac.Nrl2.Transport;

const string sid = "01020304050607";
const string rotor = "123456789ABCDE";

if (args.Length == 0)
{
    Console.WriteLine(Convert.ToHexString(Nrl2Frame.Encode(sid, rotor, Gs232Commands.Query())));
    Console.WriteLine("Offline example. Use --discover for LAN discovery, --lan-query <host> for read-only TCP/60020, --management-status <host> for a passive WebSocket handshake, or --sim-query for 127.0.0.1:19120.");
    return;
}
if (args.SequenceEqual(new[] { "--discover" }))
{
    foreach (var item in await Rc3040sDiscovery.DiscoverAsync(TimeSpan.FromSeconds(2)))
        Console.WriteLine($"{item.Address}:{item.LocalTcpPort} ID={item.RotorId}");
    return;
}
if (args.SequenceEqual(new[] { "--sim-query" }))
{
    using var client = new Nrl2UdpClient("127.0.0.1", 19120, sid, rotor);
    Console.WriteLine(await client.QueryPositionAsync(TimeSpan.FromSeconds(2)));
    return;
}
if (args.Length == 2 && args[0] == "--lan-query")
{
    using var lan = await Rc3040sLanClient.ConnectAsync(args[1], Rc3040sLanClient.DefaultPort);
    Console.WriteLine(await lan.QueryPositionAsync(TimeSpan.FromSeconds(2)));
    return;
}
if (args.Length == 2 && args[0] == "--management-status")
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var management = await Rc3040sWebSocketClient.ConnectAsync(
        args[1], Rc3040sNetworkPorts.ManagementTcpPort, cts.Token);
    Console.WriteLine("WebSocket/TCP 60030 handshake completed; no commands sent.");
    string? message = await management.ReceiveTextAsync(TimeSpan.FromSeconds(2), cts.Token);
    Console.WriteLine(message ?? "(no unsolicited text received)");
    return;
}
throw new ArgumentException("Supported: (none), --discover, --lan-query <host>, --management-status <host>, --sim-query");
