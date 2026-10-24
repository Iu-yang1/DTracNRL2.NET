using System.Globalization;
using System.Net;
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
    /// Broadcast discovery is not reliable on every firmware/network combination.
    /// Prefer DiscoverAtAsync when the actual device IP is already known.
    /// </summary>
    public static Task<IReadOnlyList<DiscoveredRotor>> DiscoverAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        DiscoverCoreAsync(IPAddress.Broadcast, null, timeout, cancellationToken);

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
        return DiscoverCoreAsync(device, device, timeout, cancellationToken);
    }

    private static async Task<IReadOnlyList<DiscoveredRotor>> DiscoverCoreAsync(
        IPAddress destination, IPAddress? expectedSource, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.EnableBroadcast = true;
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        await socket.SendAsync(CreatePing(), new IPEndPoint(destination, DiscoveryPort), cancellationToken);
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
