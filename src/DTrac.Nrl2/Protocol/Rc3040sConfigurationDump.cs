using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;
using DTrac.Nrl2.Discovery;

namespace DTrac.Nrl2.Protocol;

/// <summary>
/// Parses the read-only, multi-message TCP/60030 configuration dump observed
/// on real RC_3040S_67S_8H_260223_C firmware. The dump is a snapshot of
/// firmware-reported settings, NOT evidence of successful AT configuration writes.
/// Individual WebSocket text messages may divide lines at arbitrary boundaries.
/// </summary>
public static partial class Rc3040sConfigurationDump
{
    private const int MaxChars = 16384;
    private const int MaxFrames = 64;

    [GeneratedRegex(@"^[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MacPattern();

    public static Rc3040sConfigurationSnapshot Parse(IEnumerable<string> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        int count = 0;
        int size = 0;
        foreach (var frame in frames)
        {
            if (frame is null) throw new ArgumentException("Null WebSocket frame.", nameof(frames));
            if (++count > MaxFrames || (size += frame.Length) > MaxChars)
                throw new ArgumentOutOfRangeException(nameof(frames), "Configuration text exceeds bounded capture size.");
            // Firmware can divide one CRLF-terminated line between separate WS messages.
            text.Write(frame);
        }

        IPAddress? ip = null, gateway = null, mask = null, dns = null, destination = null;
        bool? dhcp = null;
        string? firmware = null, maskedMac = null;
        bool networkHeader = false, udpHeader = false;
        bool yuntaiHeader = false, identityHeader = false;
        ushort? azSpeed = null, elSpeed = null;
        string? modelToken = null, xwToken = null, travelModeToken = null;
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string all = text.ToString();
        foreach (string untrimmed in all.Split('\n'))
        {
            string line = untrimmed.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.Length > 256) continue;
            if (Rc3040sFirmwareBanner.TryParse(line, out var banner))
            {
                firmware = banner.VersionLabel;
                continue;
            }
            if (line.Contains("NET SETUP", StringComparison.OrdinalIgnoreCase))
            {
                networkHeader = true;
                continue;
            }
            if (line.Contains("UDP Destination IP SETUP", StringComparison.OrdinalIgnoreCase))
            {
                udpHeader = true;
                continue;
            }
            if (line.Contains("YUNTAI SETUP", StringComparison.OrdinalIgnoreCase))
            {
                yuntaiHeader = true;
                continue;
            }
            if (line.Contains("CALL/SSID", StringComparison.OrdinalIgnoreCase))
            {
                identityHeader = true;
                continue;
            }
            if (!line.StartsWith("AT+", StringComparison.OrdinalIgnoreCase)) continue;
            // Mode commands in the observed configuration dump have NO equals:
            // AT+P450. They are tokens, not evidence of a measured axis limit.
            if (line is "AT+P450" or "AT+P360")
            {
                travelModeToken = line[3..];
                fields.Add("TRAVEL_MODE");
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals < 3) continue;
            string key = line[3..equals].ToUpperInvariant();
            string value = line[(equals + 1)..].Trim();
            switch (key)
            {
                case "IP":
                    if (TryIp(value, out var parsedIp)) { ip = parsedIp; fields.Add(key); }
                    break;
                case "GATEWAY":
                    if (TryIp(value, out var parsedGateway)) { gateway = parsedGateway; fields.Add(key); }
                    break;
                case "MASK":
                    if (TryIp(value, out var parsedMask)) { mask = parsedMask; fields.Add(key); }
                    break;
                case "DNS":
                    if (TryIp(value, out var parsedDns)) { dns = parsedDns; fields.Add(key); }
                    break;
                case "D_IP":
                    if (TryIp(value, out var parsedDestination)) { destination = parsedDestination; fields.Add(key); }
                    break;
                case "DHCP":
                    if (value.Equals("ON", StringComparison.OrdinalIgnoreCase)) { dhcp = true; fields.Add(key); }
                    else if (value.Equals("OFF", StringComparison.OrdinalIgnoreCase)) { dhcp = false; fields.Add(key); }
                    break;
                case "AZ_SPD":
                    if (TrySpeed(value, out ushort az))
                    {
                        azSpeed = az; fields.Add(key);
                    }
                    break;
                case "EL_SPD":
                    if (TrySpeed(value, out ushort el))
                    {
                        elSpeed = el; fields.Add(key);
                    }
                    break;
                case "YT":
                    if (value.Length is >= 1 and <= 12 &&
                        value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
                    {
                        modelToken = value; fields.Add(key);
                    }
                    break;
                case "XW":
                    if (value.Length is >= 1 and <= 8 &&
                        value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
                    {
                        xwToken = value; fields.Add(key);
                    }
                    break;
                case "PHY":
                    if (MacPattern().IsMatch(value))
                    {
                        string[] bytes = value.Split(':');
                        maskedMac = "**:**:**:**:" + bytes[4].ToUpperInvariant() + ":" +
                            bytes[5].ToUpperInvariant();
                        fields.Add(key);
                    }
                    break;
            }
        }
        return new Rc3040sConfigurationSnapshot(
            firmware, ip, gateway, mask, dns, dhcp, destination, maskedMac,
            networkHeader, udpHeader, fields.Count, count,
            azSpeed, elSpeed, modelToken, xwToken, travelModeToken,
            yuntaiHeader, identityHeader);
    }

    private static bool TrySpeed(string value, out ushort raw) =>
        value.Length == 5 &&
        value.All(c => c is >= '0' and <= '9') &&
        ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out raw) &&
        raw > 0;

    private static bool TryIp(string text, out IPAddress address) =>
        Rc3040sDiscovery.TryParseDecimalIpv4(text, out address);
}

/// <summary>
/// Values are only the firmware's reported configuration, not independently
/// observed effective routing state. The MAC is always redacted.
/// </summary>
public sealed record Rc3040sConfigurationSnapshot(
    string? FirmwareVersion,
    IPAddress? ConfiguredIp,
    IPAddress? ConfiguredGateway,
    IPAddress? ConfiguredMask,
    IPAddress? ConfiguredDns,
    bool? DhcpEnabled,
    IPAddress? ConfiguredUdpDestination,
    string? MaskedPhysicalAddress,
    bool SawNetworkSection,
    bool SawUdpDestinationSection,
    int RecognizedFieldCount,
    int WebSocketMessageCount,
    ushort? AzimuthSpeedRaw = null,
    ushort? ElevationSpeedRaw = null,
    string? ModelToken = null,
    string? XwModeToken = null,
    string? TravelModeToken = null,
    bool SawYuntaiSection = false,
    bool SawIdentitySection = false);
