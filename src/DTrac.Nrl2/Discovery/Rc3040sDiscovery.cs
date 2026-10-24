using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using DTrac.Nrl2.Protocol;

namespace DTrac.Nrl2.Discovery;

public sealed record DiscoveredRotor(IPAddress Address, int LocalTcpPort, string RotorId);

/// <summary>LAN discovery uses UDP/60059 and is NOT the NRL2 remote control transport.</summary>
public static class Rc3040sDiscovery
{
    public const int DiscoveryPort = 60059;
    public static byte[] CreatePing() => "cmd=ping\n"u8.ToArray();

    public static bool TryParsePong(ReadOnlySpan<byte> response, IPAddress sender, out DiscoveredRotor? rotor)
    {
        rotor = null;
        if (sender.AddressFamily != AddressFamily.InterNetwork || response.Length is < 10 or > 512)
            return false;

        var fields = Encoding.ASCII.GetString(response).Trim('\0', '\r', '\n', ' ').Split('&');
        if (!fields[0].Equals("cmd=pong", StringComparison.OrdinalIgnoreCase))
            return false;
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Skip(1))
        {
            int separator = field.IndexOf('=');
            if (separator > 0) attributes[field[..separator]] = field[(separator + 1)..];
        }
        if (!attributes.TryGetValue("type", out var type) ||
            !(type.Equals("RC-3040S", StringComparison.OrdinalIgnoreCase) || type == "1"))
            return false;
        if (!attributes.TryGetValue("rotorid", out var id) && !attributes.TryGetValue("id", out id))
            return false;
        string rotorId;
        try { rotorId = Nrl2Frame.NormalizeId(id); }
        catch (ArgumentException) { return false; }

        int port = 60020;
        if (attributes.TryGetValue("port", out var advertisedPort) &&
            (!int.TryParse(advertisedPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
             port is < 1 or > 65535))
            return false;
        if (attributes.TryGetValue("ip", out var ip) &&
            (!TryParseDecimalIpv4(ip, out var advertisedAddress) || !advertisedAddress.Equals(sender)))
            return false;

        rotor = new DiscoveredRotor(sender, port, rotorId);
        return true;
    }

    /// <summary>
    /// RC-3040S firmware emits IPv4 octets as decimal three-digit groups
    /// (e.g. 192.168.001.009); don't use IPAddress.TryParse, whose historical
    /// IPv4 shorthand/octal rules can reject or misinterpret such text.
    /// </summary>
    public static bool TryParseDecimalIpv4(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (value is null || value.Length is < 7 or > 15) return false;
        string[] octets = value.Split('.');
        if (octets.Length != 4) return false;
        var bytes = new byte[4];
        for (int i = 0; i < octets.Length; i++)
        {
            string part = octets[i];
            if (part.Length is < 1 or > 3) return false;
            if (part.Any(c => c is < '0' or > '9') ||
                !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }
        address = new IPAddress(bytes);
        return true;
    }

    /// <summary>
    /// Real RC-3040S firmware 260223_C responded to a subnet-directed broadcast
    /// but not the limited broadcast 255.255.255.255. Probe local IPv4 subnets,
    /// preserving the historical limited-broadcast fallback.
    /// </summary>
    public static Task<IReadOnlyList<DiscoveredRotor>> DiscoverAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var adapters = new List<(IPAddress Address, IPAddress Mask)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        unicast.IPv4Mask is not null)
                        adapters.Add((unicast.Address, unicast.IPv4Mask));
                }
            }
        }
        catch (NetworkInformationException) { /* limited broadcast fallback */ }
        var destinations = GetSubnetBroadcastTargets(adapters).ToList();
        destinations.Add(IPAddress.Broadcast);
        return DiscoverCoreAsync(destinations, null, timeout, cancellationToken);
    }

    /// <summary>Computes IPv4 subnet broadcasts from explicit local interfaces, without assuming /24.</summary>
    public static IReadOnlyList<IPAddress> GetSubnetBroadcastTargets(
        IEnumerable<(IPAddress Address, IPAddress Mask)> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var results = new HashSet<IPAddress>();
        foreach (var (address, maskAddress) in adapters)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork ||
                maskAddress.AddressFamily != AddressFamily.InterNetwork) continue;
            var ip = address.GetAddressBytes();
            var mask = maskAddress.GetAddressBytes();
            int fixedBits = 0;
            foreach (byte m in mask) fixedBits += System.Numerics.BitOperations.PopCount((uint)m);
            // /31 and /32 have no conventional directed broadcast.
            if (fixedBits is < 1 or > 30) continue;
            byte[] broadcast = new byte[4];
            for (int i = 0; i < 4; i++)
                broadcast[i] = (byte)((ip[i] & mask[i]) | ~mask[i]);
            results.Add(new IPAddress(broadcast));
        }
        return results.OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Sends only the official read-only UDP/60059 cmd=ping to a known IPv4 peer.
    /// Does not send NRL2 motion packets or modify controller configuration.
    /// </summary>
    public static Task<IReadOnlyList<DiscoveredRotor>> DiscoverAtAsync(
        IPAddress device, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The RC-3040S discovery protocol uses IPv4.", nameof(device));
        return DiscoverCoreAsync([device], device, timeout, cancellationToken);
    }

    private static async Task<IReadOnlyList<DiscoveredRotor>> DiscoverCoreAsync(
        IReadOnlyCollection<IPAddress> destinations, IPAddress? expectedSource, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.EnableBroadcast = true;
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        int sent = 0;
        SocketException? error = null;
        foreach (IPAddress destination in destinations)
        {
            try
            {
                await socket.SendAsync(CreatePing(),
                    new IPEndPoint(destination, DiscoveryPort), cancellationToken);
                sent++;
            }
            catch (SocketException ex) { error = ex; }
        }
        if (sent == 0)
            throw new IOException("No UDP/60059 discovery broadcast was sent successfully.", error);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var found = new Dictionary<string, DiscoveredRotor>(StringComparer.OrdinalIgnoreCase);
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult response;
            try { response = await socket.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            if ((expectedSource is null || response.RemoteEndPoint.Address.Equals(expectedSource)) &&
                TryParsePong(response.Buffer, response.RemoteEndPoint.Address, out var rotor) && rotor != null)
                found[rotor.RotorId] = rotor;
        }
        return found.Values.ToArray();
    }
}
