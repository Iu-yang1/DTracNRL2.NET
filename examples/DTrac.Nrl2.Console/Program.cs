using DTrac.Nrl2.Discovery;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Rotator;
using DTrac.Nrl2.Transport;

const string sid = "01020304050607";
const string rotor = "123456789ABCDE";

if (args.Length == 0)
{
    Console.WriteLine(Convert.ToHexString(Nrl2Frame.Encode(sid, rotor, Gs232Commands.Query())));
    Console.WriteLine("Offline example. Use --discover for LAN discovery (read-only), or --sim-query to query 127.0.0.1:19120.");
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
throw new ArgumentException("Supported: (none), --discover, --sim-query");
