using System.Text.RegularExpressions;

namespace DTrac.Nrl2.Protocol;

/// <summary>
/// Recognizes a firmware/device banner seen from real RC-3040S_67S_8H_260223_C
/// hardware over WebSocket/60030 after a C2 probe. A banner is NOT an angle
/// response nor proof that a given AT command was accepted.
/// </summary>
public static partial class Rc3040sFirmwareBanner
{
    [GeneratedRegex(@"^\s*(?<version>RC_3040S_67S_8H_[0-9]{6}_[A-Za-z0-9]+)\s+(?<id>[A-Fa-f0-9]{14})\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?i)\b[A-F0-9]{14}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool TryParse(string? message, out Rc3040sFirmwareIdentity identity)
    {
        identity = default;
        if (message is null || message.Length > 256) return false;
        Match match = VersionPattern().Match(message);
        if (!match.Success) return false;
        string id = match.Groups["id"].Value;
        identity = new Rc3040sFirmwareIdentity(match.Groups["version"].Value,
            "**********" + id[^4..].ToUpperInvariant());
        return true;
    }

    /// <summary>Redacts 14-digit hex hardware IDs from arbitrary device messages.</summary>
    public static string RedactDeviceIds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IdPattern().Replace(text, m =>
            "**********" + m.Value[^4..].ToUpperInvariant());
    }
}

/// <summary>Deliberately carries only a masked ID, never the full hardware identifier.</summary>
public readonly record struct Rc3040sFirmwareIdentity(string VersionLabel, string MaskedDeviceId);
