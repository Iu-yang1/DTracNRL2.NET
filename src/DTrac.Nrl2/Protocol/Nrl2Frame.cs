using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DTrac.Nrl2.Protocol;

/// <summary>Two observed directional wire encodings: APK ASCII decimal length and MCU binary big-endian length.</summary>
public enum Nrl2LengthEncoding { AppAsciiDecimal, FirmwareBigEndian }

/// <summary>
/// Observed DTrac-specific NRL2 48-byte envelope; not generic nrllink.
/// The peer's 8-byte W5500 UDP receive metadata is not included in these offsets.
/// </summary>
public static class Nrl2Frame
{
    public const int HeaderSize = 48;
    public const int MaxFrameSize = 1500;
    public const byte Gs232Type = 0x05;
    public const byte HeartbeatType = 0x02;

    public static string NormalizeId(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 14)
            throw new ArgumentException("ID must be exactly fourteen hexadecimal digits.", nameof(hex));
        try { return Convert.ToHexString(Convert.FromHexString(hex)); }
        catch (FormatException e) { throw new ArgumentException("Malformed hexadecimal ID.", nameof(hex), e); }
    }

    /// <summary>APK-compatible frame: ASCII decimal 2-byte length, +21=0, fixed DTrac label.</summary>
    public static byte[] Encode(string sourceSid, string targetRotorId, ReadOnlySpan<byte> payload, byte type = Gs232Type)
    {
        int total = checked(HeaderSize + payload.Length);
        if (total > 99) throw new ArgumentOutOfRangeException(nameof(payload), "APK length supports at most 99 bytes.");
        byte[] result = MakeHeader(sourceSid, targetRotorId, total, type);
        var length = total.ToString("D2", CultureInfo.InvariantCulture);
        result[4] = (byte)length[0];
        result[5] = (byte)length[1];
        "DTrac"u8.CopyTo(result.AsSpan(24, 5));
        payload.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    /// <summary>
    /// Emulator/reference generator matching reconstructed MCU header, not a claim about public relay transport.
    /// Firmware tag is eight raw ASCII bytes and is NOT invariably 'DTrac'.
    /// </summary>
    public static byte[] EncodeDevice(string rotorId, string targetId, ReadOnlySpan<byte> payload,
        byte type = Gs232Type, string deviceTag = "TESTDEV ")
    {
        int total = checked(HeaderSize + payload.Length);
        if (total > MaxFrameSize) throw new ArgumentOutOfRangeException(nameof(payload));
        if (deviceTag.Length != 8 || deviceTag.Any(c => c < 32 || c > 126))
            throw new ArgumentException("Device tag must be exactly eight printable ASCII characters.", nameof(deviceTag));
        byte[] frame = MakeHeader(rotorId, targetId, total, type);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), (ushort)total);
        frame[21] = 1;
        Encoding.ASCII.GetBytes(deviceTag).CopyTo(frame, 24);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    private static byte[] MakeHeader(string sourceId, string targetId, int total, byte type)
    {
        var source = Convert.FromHexString(NormalizeId(sourceId));
        var target = Convert.FromHexString(NormalizeId(targetId));
        byte[] frame = new byte[total];
        "NRL2"u8.CopyTo(frame);
        source.CopyTo(frame, 6);
        target.CopyTo(frame, 13);
        frame[20] = type;
        return frame;
    }

    /// <summary>
    /// Strict parser requiring an explicit origin dialect. This intentionally does not auto-guess ambiguous bytes.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out Nrl2Message message,
        Nrl2LengthEncoding encoding = Nrl2LengthEncoding.AppAsciiDecimal)
    {
        message = default;
        if (frame.Length < HeaderSize || frame.Length > MaxFrameSize ||
            !frame[..4].SequenceEqual("NRL2"u8))
            return false;
        int advertised;
        if (encoding == Nrl2LengthEncoding.AppAsciiDecimal)
        {
            if (frame.Length > 99 || frame[4] is < (byte)'0' or > (byte)'9' ||
                frame[5] is < (byte)'0' or > (byte)'9') return false;
            advertised = (frame[4] - '0') * 10 + frame[5] - '0';
        }
        else if (encoding == Nrl2LengthEncoding.FirmwareBigEndian)
            advertised = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(4, 2));
        else return false;

        if (advertised != frame.Length) return false;
        message = new Nrl2Message(
            Convert.ToHexString(frame.Slice(6, 7)),
            Convert.ToHexString(frame.Slice(13, 7)),
            frame[20],
            Encoding.ASCII.GetString(frame.Slice(24, 8)),
            frame[HeaderSize..].ToArray());
        return true;
    }

    /// <summary>
    /// Validate source/target and textual payload. Identifier matching is NOT authentication,
    /// and there is no confirmed wire sequence number to prove freshness.
    /// </summary>
    public static bool TryDecodeReply(ReadOnlySpan<byte> frame, string appSid, string rotorId, out byte[] payload,
        Nrl2LengthEncoding encoding = Nrl2LengthEncoding.FirmwareBigEndian)
    {
        payload = Array.Empty<byte>();
        if (!TryParse(frame, out var msg, encoding) || msg.Type != Gs232Type)
            return false;
        string sid, rid;
        try { sid = NormalizeId(appSid); rid = NormalizeId(rotorId); }
        catch (ArgumentException) { return false; }
        if (msg.SourceId != rid || msg.TargetId != sid) return false;
        foreach (byte b in msg.Payload)
            if (b != 10 && b != 13 && (b < 32 || b > 126)) return false;
        payload = msg.Payload;
        return true;
    }
}

public readonly record struct Nrl2Message(
    string SourceId, string TargetId, byte Type, string Tag, byte[] Payload);
