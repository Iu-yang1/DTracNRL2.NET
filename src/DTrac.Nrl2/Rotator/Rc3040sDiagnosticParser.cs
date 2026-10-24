using System.Globalization;
using System.Text.RegularExpressions;

namespace DTrac.Nrl2.Rotator;

/// <summary>
/// Parses firmware diagnostic text observed as ROM formatting literals.
/// Parsing a diagnostic output is NOT authenticated readback nor proof of motor motion.
/// </summary>
public static class Rc3040sDiagnosticParser
{
    // Firmware ROM: 0xA0DF, 0xA101: AZ/EL SPEED: %05u  HZ , %02f .../s
    // 251217 ROM string uses GBK A1E3 = '°' before /s, and diagnostics
    // compute 360*Hz/361600 (AZ) or 360*Hz/326400 (EL).
    // Parsing this display remains separate from true measured shaft speed.
    private static readonly Regex SpeedLine = new(
        @"^\s*(AZ|EL)\s+SPEED:\s*(\d{5})\s+HZ\s*,\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*°?\s*/s\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Recognizes firmware speed diagnostic lines; no command is sent.</summary>
    public static bool TryParseSpeedLine(string? input, out Rc3040sAxisSpeedDiagnostic diagnostic)
    {
        diagnostic = default;
        if (input is null || input.Length > 256) return false;
        var match = SpeedLine.Match(input);
        if (!match.Success ||
            !ushort.TryParse(match.Groups[2].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out ushort hz) ||
            !double.TryParse(match.Groups[3].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double rate) ||
            !double.IsFinite(rate))
            return false;
        var axis = match.Groups[1].Value.Equals("AZ", StringComparison.OrdinalIgnoreCase)
            ? Rc3040sAxis.Azimuth : Rc3040sAxis.Elevation;
        diagnostic = new Rc3040sAxisSpeedDiagnostic(axis, hz, rate);
        return true;
    }
}

public enum Rc3040sAxis { Azimuth, Elevation }

/// <param name="RawFrequencyHz">Firmware's diagnostic speed frequency field, not degrees per second.</param>
/// <param name="ReportedRatePerSecond">Firmware-reported nominal degrees/second (251217 conversion); not a mechanical measurement.</param>
public readonly record struct Rc3040sAxisSpeedDiagnostic(
    Rc3040sAxis Axis, ushort RawFrequencyHz, double ReportedRatePerSecond);
