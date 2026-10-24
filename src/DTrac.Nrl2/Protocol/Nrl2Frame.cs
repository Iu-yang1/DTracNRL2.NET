using System.Globalization;
using System.Text;

namespace DTrac.Nrl2.Protocol;

/// <summary>
/// DTrac APK's observed 48-byte NRL2 envelope. Not the generic nrllink wire dialect.
/// All byte offsets are relative to the UDP application payload (not the W5500 RX header).
/// </summary>
public static class Nrl2Frame
{
    public const int HeaderSize = 48;
    public const int MaxFrameSize = 99;
    public const byte Gs232Type = 0x05;

    public static string NormalizeId(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 14)
            throw new ArgumentException("ID must contain exactly 14 hexadecimal characters.", nameof(hex));
        byte[] data;
        try { data = Convert.FromHexString(hex); }
        catch (FormatException e) { throw new ArgumentException("ID is not hexadecimal.", nameof(hex), e); }
        return Convert.ToHexString(data);
    }

    public static byte[] Encode(string sourceSid, string targetRotorId, ReadOnlySpan<byte> payload, byte type = Gs232Type)
    {
        var source = Convert.FromHexString(NormalizeId(sourceSid));
        var target = Convert.FromHexString(NormalizeId(targetRotorId));
        int size = checked(HeaderSize + payload.Length);
        if (size > MaxFrameSize)
            throw new ArgumentOutOfRangeException(nameof(payload), "DTrac ASCII length holds at most 99 bytes.");

        byte[] frame = new byte[size];
        "NRL2"u8.CopyTo(frame);
        var digits = size.ToString("D2", CultureInfo.InvariantCulture);
        frame[4] = (byte)digits[0];
        frame[5] = (byte)digits[1];
        source.CopyTo(frame, 6);
        target.CopyTo(frame, 13);
        frame[20] = type;
        "DTrac"u8.CopyTo(frame.AsSpan(24, 5));
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, out Nrl2Message message)
    {
        message = default;
        if (bytes.Length < HeaderSize || bytes.Length > MaxFrameSize ||
            !bytes.Slice(0, 4).SequenceEqual("NRL2"u8) ||
            bytes[4] < '0' || bytes[4] > '9' ||
            bytes[5] < '0' || bytes[5] > '9' ||
            (bytes[4] - '0') * 10 + bytes[5] - '0' != bytes.Length)
            return false;

        message = new Nrl2Message(
            Convert.ToHexString(bytes.Slice(6, 7)),
            Convert.ToHexString(bytes.Slice(13, 7)),
            bytes[20],
            Encoding.ASCII.GetString(bytes.Slice(24, 5)),
            bytes.Slice(HeaderSize).ToArray());
        return true;
    }

    /// <summary>
    /// Checks reverse source/destination identifiers for a reply.
    /// The wire format has no confirmed sequence number or authenticated acknowledgement.
    /// </summary>
    public static bool TryDecodeReply(ReadOnlySpan<byte> frame, string appSid, string rotorId, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (!TryParse(frame, out var msg) || msg.Type != Gs232Type)
            return false;
        string app, rotor;
        try { app = NormalizeId(appSid); rotor = NormalizeId(rotorId); }
        catch (ArgumentException) { return false; }
        if (!string.Equals(msg.SourceId, rotor, StringComparison.Ordinal) ||
            !string.Equals(msg.TargetId, app, StringComparison.Ordinal))
            return false;
        foreach (var c in msg.Payload)
        {
            if (c != 10 && c != 13 && (c < 32 || c > 126))
                return false;
        }
        payload = msg.Payload;
        return true;
    }
}

public readonly record struct Nrl2Message(
    string SourceId, string TargetId, byte Type, string Tag, byte[] Payload);
