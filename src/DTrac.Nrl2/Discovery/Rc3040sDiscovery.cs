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
            (!IPAddress.TryParse(ip, out var advertisedAddress) || !advertisedAddress.Equals(sender)))
            return false;

        rotor = new DiscoveredRotor(sender, port, rotorId);
        return true;
    }

    public static async Task<IReadOnlyList<DiscoveredRotor>> DiscoverAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.EnableBroadcast = true;
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        await socket.SendAsync(CreatePing(), new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var found = new Dictionary<string, DiscoveredRotor>(StringComparer.OrdinalIgnoreCase);
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult response;
            try { response = await socket.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            if (TryParsePong(response.Buffer, response.RemoteEndPoint.Address, out var rotor) && rotor != null)
                found[rotor.RotorId] = rotor;
        }
        return found.Values.ToArray();
    }
}
