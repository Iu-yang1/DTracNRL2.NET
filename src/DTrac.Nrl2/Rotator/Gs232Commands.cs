using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DTrac.Nrl2.Rotator;

public readonly record struct RotatorPosition(double Azimuth, double Elevation);
public enum JogDirection { Left, Right, Up, Down }

public static class Gs232Commands
{
    private static readonly Regex PositionRegex = new(
        @"AZ\s*=\s*([+-]?\d+(?:\.\d+)?)\s+EL\s*=\s*([+-]?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static byte[] Query() => "C2\r\n"u8.ToArray();
    public static byte[] Stop() => "S\r\n"u8.ToArray();
    public static byte[] QueryAzimuth() => "C\r\n"u8.ToArray();
    public static byte[] QueryElevation() => "B\r\n"u8.ToArray();
    public static byte[] StopAzimuth() => "A\r\n"u8.ToArray();
    public static byte[] StopElevation() => "E\r\n"u8.ToArray();

    public static byte[] MoveAzimuth(int azimuth)
    {
        if (azimuth is < 0 or > 450) throw new ArgumentOutOfRangeException(nameof(azimuth));
        return Encoding.ASCII.GetBytes(FormattableString.Invariant($"M{azimuth:000}\r\n"));
    }

    public static byte[] MoveTo(RotatorPosition position)
    {
        if (!double.IsFinite(position.Azimuth) || !double.IsFinite(position.Elevation) ||
            position.Azimuth is < 0 or > 450 || position.Elevation is < 0 or > 180)
            throw new ArgumentOutOfRangeException(nameof(position), "Outside GS-232 command bounds.");
        int az = (int)Math.Round(position.Azimuth, MidpointRounding.AwayFromZero);
        int el = (int)Math.Round(position.Elevation, MidpointRounding.AwayFromZero);
        return Encoding.ASCII.GetBytes(FormattableString.Invariant($"W{az:000} {el:000}\r\n"));
    }

    public static byte[] Jog(JogDirection direction) => direction switch
    {
        JogDirection.Left => "L\r\n"u8.ToArray(),
        JogDirection.Right => "R\r\n"u8.ToArray(),
        JogDirection.Up => "U\r\n"u8.ToArray(),
        JogDirection.Down => "D\r\n"u8.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    /// <summary>
    /// Experimental command found in prior integration. Remote acceptance is unverified.
    /// It MUST NOT be invoked by a startup or reconnection routine.
    /// </summary>
    public static byte[] ExperimentalRestart() => "AT+RESET\r\n"u8.ToArray();

    public static bool TryParsePosition(ReadOnlySpan<byte> ascii, out RotatorPosition position)
    {
        position = default;
        if (ascii.Length > 256) return false;
        var match = PositionRegex.Match(Encoding.ASCII.GetString(ascii));
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var az) ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var el) ||
            !double.IsFinite(az) || !double.IsFinite(el) ||
            az is < -360 or > 450 || el is < -90 or > 180)
            return false;
        position = new RotatorPosition(az, el);
        return true;
    }
}
