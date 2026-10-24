using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DTrac.Nrl2.Rotator;

/// <summary>
/// Non-GS232 AT configuration commands seen in firmware handler at 0x3863.
/// A management ingress is statically traced from W5500 Socket 0 TCP/60030 to 0x3863,
/// but its framing and accept/reply semantics have not yet been established.
/// Unlike the GS232 dispatcher at 0x3433, availability on TCP/60020 and NRL2 Type5
/// has NOT been demonstrated. These functions only construct bytes; never transmit.
/// </summary>
public static class Rc3040sFirmwareCommands
{
    public static byte[] SetAzimuthSpeed(ushort rawValue) => Speed("AZ", rawValue);
    public static byte[] SetElevationSpeed(ushort rawValue) => Speed("EL", rawValue);

    private static byte[] Speed(string axis, ushort rawValue)
    {
        if (rawValue == 0) throw new ArgumentOutOfRangeException(nameof(rawValue));
        // Firmware parser at 0x720E reads exactly five ASCII digit positions (+10..+14).
        // ushort prevents 16-bit wraparound, but this is NOT a calibrated mechanical speed range.
        // This command may persist EEPROM. Do not transmit without an independently verified management frame.
        return Encoding.ASCII.GetBytes(FormattableString.Invariant($"AT+{axis}_SPD={rawValue:00000}\r"));
    }

    /// <summary>
    /// Firmware's 0x8B2C / 0x8E76 parser reads 4 exactly-3-decimal-digit octets at
    /// offsets prefix.Length + {0,4,8,12}. This only constructs bytes. Sending the
    /// command may persist the IP settings and disconnect the controller.
    /// </summary>
    private static byte[] Ipv4(string prefix, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("This firmware interface accepts IPv4 only.", nameof(address));
        string encoded = string.Join(".", address.GetAddressBytes().Select(
            part => part.ToString("D3", CultureInfo.InvariantCulture)));
        return Encoding.ASCII.GetBytes(prefix + encoded + "\r");
    }

    public static byte[] SetIpAddress(IPAddress address) => Ipv4("AT+IP=", address);
    public static byte[] SetGateway(IPAddress address) => Ipv4("AT+GATEWAY=", address);
    public static byte[] SetSubnetMask(IPAddress address) => Ipv4("AT+MASK=", address);
    public static byte[] SetDns(IPAddress address) => Ipv4("AT+DNS=", address);

    /// <summary>
    /// Writes the firmware's remote NRL2 server destination string (D_IP).
    /// Factory image stores a 15-character zero-padded IPv4 string at config slot 0x50.
    /// This command may immediately disconnect Socket7 or require reboot.
    /// It must NEVER be sent automatically during connection/retry.
    /// </summary>
    public static byte[] SetRemoteRelayIp(IPAddress address) => Ipv4("AT+D_IP=", address);

    /// <summary>
    /// A six-character local/relay call-label. The firmware copies a maximum of
    /// six nonterminator characters into XDATA 0x09E2 onward. This is NOT a
    /// cryptographic callsign credential or a rotator RotorID.
    /// </summary>
    public static byte[] SetCallsign(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        // This safe SDK subset avoids CR/LF injection and firmware buffer overflow;
        // it does not assert the firmware rejects every other printable character.
        if (callsign.Length is < 1 or > 6 ||
            callsign.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9')))
            throw new ArgumentException("Call-label must be 1..6 uppercase ASCII letters/digits.", nameof(callsign));
        return Encoding.ASCII.GetBytes($"AT+CALL={callsign}\r");
    }

    /// <summary>
    /// Raw one-byte SSID setting: three fixed decimal positions parsed via
    /// 0x8E76 and stored at XDATA 0x09E1. Not a Wi-Fi SSID string.
    /// </summary>
    public static byte[] SetDeviceSsid(byte rawSsid) =>
        Encoding.ASCII.GetBytes(FormattableString.Invariant($"AT+SSID={rawSsid:000}\r"));

    /// <summary>Firmware-recognized DHCP toggle; may persist configuration or disconnect the device.</summary>
    public static byte[] SetDhcp(bool enabled) =>
        Encoding.ASCII.GetBytes(enabled ? "AT+DHCP=ON\r" : "AT+DHCP=OFF\r");

    /// <summary>Firmware-recognized model selection, with unknown hardware side effects.</summary>
    public static byte[] SelectModel(Rc3040sFirmwareModel model) =>
        Encoding.ASCII.GetBytes(model switch
        {
            Rc3040sFirmwareModel.Rc3040 => "AT+YT=3040\r",
            Rc3040sFirmwareModel.Rc3051 => "AT+YT=3051\r",
            Rc3040sFirmwareModel.Rc3060 => "AT+YT=3060\r",
            Rc3040sFirmwareModel.Ys3051 => "AT+YT=YS3051\r",
            Rc3040sFirmwareModel.Sp5050 => "AT+YT=SP5050\r",
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        });

    /// <summary>Nonvolatile initialization, deliberately never sent by the library itself.</summary>
    public static byte[] InitializeDefaults() => "AT+INIT\r"u8.ToArray();

    /// <summary>Known firmware selection string; changes mechanical travel behavior.</summary>
    public static byte[] SelectAzimuthTravel(bool allow450Degrees) =>
        Encoding.ASCII.GetBytes(allow450Degrees ? "AT+P450\r" : "AT+P360\r");

    /// <summary>
    /// Diagnostic echo command: handler 0x387D forwards the suffix at XDATA 0x0051
    /// to the WebSocket text writer via 0x8BEE -> 0x8A1F -> 0x58C9.
    /// This is NOT firmware cruise/DEMO.
    /// </summary>
    public const string DiagnosticEchoPrefix = "AT+TEST=";

    /// <summary>Constructs a diagnostic echo payload only; never sends it.</summary>
    public static byte[] DiagnosticEcho(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is < 1 or > 48 || text.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("Echo text must be 1..48 printable ASCII characters.", nameof(text));
        return Encoding.ASCII.GetBytes(DiagnosticEchoPrefix + text + "\r");
    }

    [Obsolete("AT+TEST= is an echo/diagnostic operation, NOT a native DEMO cruise command. Use DiagnosticEchoPrefix.")]
    public static string DemoCommandPrefix => DiagnosticEchoPrefix;

    /// <summary>Risky device reboot; never send without explicit user initiation.</summary>
    public static byte[] Restart() => "AT+RESET\r"u8.ToArray();
}

public enum Rc3040sFirmwareModel { Rc3040, Rc3051, Rc3060, Ys3051, Sp5050 }
