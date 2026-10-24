using System.Globalization;
using System.Text;

namespace DTrac.Nrl2.Rotator;

/// <summary>
/// Non-GS232 AT configuration commands seen in firmware handler at 0x3863.
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
        // Raw firmware parameter, NOT degrees/sec or acceleration. Numeric safety range awaits hardware confirmation.
        return Encoding.ASCII.GetBytes(FormattableString.Invariant($"AT+{axis}_SPD={rawValue}\r"));
    }

    /// <summary>Known firmware selection string; changes mechanical travel behavior.</summary>
    public static byte[] SelectAzimuthTravel(bool allow450Degrees) =>
        Encoding.ASCII.GetBytes(allow450Degrees ? "AT+P450\r" : "AT+P360\r");

    /// <summary>Experimental firmware DEMO AT command; parameter semantics are unverified.</summary>
    public static string DemoCommandPrefix => "AT+TEST=";

    /// <summary>Risky device reboot; never send without explicit user initiation.</summary>
    public static byte[] Restart() => "AT+RESET\r"u8.ToArray();
}
