using System.Net;
using System.Net.Sockets;
using System.Text;
using DTrac.Nrl2.Protocol;

namespace DTrac.Nrl2.Relay;

/// <summary>
/// Experimental, local-only NRL2 datagram router for firmware Socket 7 devices.
/// Its peer registration and routing policy is an independently designed LAN profile,
/// NOT a reproduction of unobserved DTrac production relay authentication.
/// </summary>
public sealed class Nrl2LanRelay : IDisposable
{
    private readonly UdpClient udp;
    private readonly Dictionary<string, IPAddress> trustedDevices;
    private readonly Dictionary<string, DeviceSession> devices = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Rotor, string Sid), AppSession> clients = new();
    private readonly TimeSpan expiration;
    private readonly bool enableMotionForwarding;
    private bool disposed;

    private readonly record struct DeviceSession(IPEndPoint Endpoint, DateTimeOffset LastSeen);
    private readonly record struct AppSession(IPEndPoint Endpoint, DateTimeOffset LastSeen);

    /// <param name="bind">Only use an explicitly trusted local interface. Loopback is recommended for tests.</param>
    /// <param name="deviceAddresses">RotorID to exact expected device IP; ID alone is not authentication.</param>
    /// <param name="enableMotionForwarding">Opt-in for W/M/L/R/U/D and A/E; queries and STOP work without it.</param>
    public Nrl2LanRelay(IPEndPoint bind, IReadOnlyDictionary<string, IPAddress> deviceAddresses,
        bool enableMotionForwarding = false, TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(deviceAddresses);
        if (bind.AddressFamily != AddressFamily.InterNetwork || bind.Port is < 0 or > 65535)
            throw new ArgumentException("IPv4 UDP listener required.", nameof(bind));
        if (deviceAddresses.Count == 0) throw new ArgumentException("At least one trusted device required.", nameof(deviceAddresses));
        expiration = idleTimeout ?? TimeSpan.FromSeconds(60);
        if (expiration < TimeSpan.FromSeconds(5) || expiration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        trustedDevices = new Dictionary<string, IPAddress>(StringComparer.Ordinal);
        foreach (var pair in deviceAddresses)
        {
            if (pair.Value is null || pair.Value.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Only explicit trusted IPv4 peers supported.", nameof(deviceAddresses));
            trustedDevices.Add(Nrl2Frame.NormalizeId(pair.Key), pair.Value);
        }
        this.enableMotionForwarding = enableMotionForwarding;
        udp = new UdpClient(bind);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)udp.Client.LocalEndPoint!;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            var now = DateTimeOffset.UtcNow;
            Prune(now);
            await ProcessAsync(received, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(UdpReceiveResult input, DateTimeOffset now, CancellationToken token)
    {
        // Device firmware frames use unsigned 16-bit network byte-order length.
        if (Nrl2Frame.TryParse(input.Buffer, out var device, Nrl2LengthEncoding.FirmwareBigEndian) &&
            trustedDevices.TryGetValue(device.SourceId, out var knownIp) &&
            knownIp.Equals(input.RemoteEndPoint.Address))
        {
            if (device.Type == Nrl2Frame.HeartbeatType &&
                device.TargetId == device.SourceId && device.Payload.Length == 0)
            {
                devices[device.SourceId] = new DeviceSession(input.RemoteEndPoint, now);
                return;
            }
            if (device.Type == Nrl2Frame.Gs232Type &&
                devices.TryGetValue(device.SourceId, out var registration) &&
                registration.Endpoint.Equals(input.RemoteEndPoint) &&
                clients.TryGetValue((device.SourceId, device.TargetId), out var client) &&
                now - client.LastSeen <= expiration &&
                device.Payload.Length > 0 && IsPrintableGs232Reply(device.Payload))
            {
                await udp.SendAsync(input.Buffer.AsMemory(), client.Endpoint, token).ConfigureAwait(false);
            }
            return;
        }

        // Android/SDK outbound frames use ASCII total-length, not binary BE16.
        if (!Nrl2Frame.TryParse(input.Buffer, out var app, Nrl2LengthEncoding.AppAsciiDecimal) ||
            app.Type != Nrl2Frame.Gs232Type || app.SourceId == app.TargetId ||
            !trustedDevices.ContainsKey(app.TargetId) ||
            !devices.TryGetValue(app.TargetId, out var deviceSession) ||
            now - deviceSession.LastSeen > expiration ||
            !IsAllowedCommand(app.Payload, enableMotionForwarding))
            return;

        clients[(app.TargetId, app.SourceId)] = new AppSession(input.RemoteEndPoint, now);
        await udp.SendAsync(input.Buffer.AsMemory(), deviceSession.Endpoint, token).ConfigureAwait(false);
    }

    private static bool IsPrintableGs232Reply(ReadOnlySpan<byte> payload)
    {
        foreach (byte b in payload)
            if (b != '\r' && b != '\n' && (b < 32 || b > 126)) return false;
        return true;
    }

    /// <summary>Unknown raw commands and any AT configuration command are deliberately denied.</summary>
    public static bool IsAllowedCommand(ReadOnlySpan<byte> payload, bool allowMotion)
    {
        if (payload.Length > 51) return false;
        string s = Encoding.ASCII.GetString(payload);
        if (s is "C2\r\n" or "C\r\n" or "B\r\n" or "S\r\n") return true;
        if (!allowMotion) return false;
        if (s is "L\r\n" or "R\r\n" or "U\r\n" or "D\r\n" or "A\r\n" or "E\r\n") return true;
        if (s.Length == 10 && s[0] == 'W' && s[4] == ' ' && s.EndsWith("\r\n", StringComparison.Ordinal) &&
            uint.TryParse(s.AsSpan(1, 3), out uint az) &&
            uint.TryParse(s.AsSpan(5, 3), out uint el) &&
            az <= 450 && el <= 180) return true;
        if (s.Length == 6 && s[0] == 'M' && s.EndsWith("\r\n", StringComparison.Ordinal) &&
            uint.TryParse(s.AsSpan(1, 3), out uint azOnly) && azOnly <= 450) return true;
        return false;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in devices.Where(p => now - p.Value.LastSeen > expiration).Select(p => p.Key).ToArray())
            devices.Remove(key);
        foreach (var key in clients.Where(p => now - p.Value.LastSeen > expiration).Select(p => p.Key).ToArray())
            clients.Remove(key);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        udp.Dispose();
    }
}
