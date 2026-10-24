using System.Net;
using System.Text;
using DTrac.Nrl2.Discovery;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Rotator;
using DTrac.Nrl2.Sessions;
using Xunit;

namespace DTrac.Nrl2.Tests;

public sealed class ProtocolTests
{
    private const string Sid = "01020304050607";
    private const string Rotor = "123456789ABCDE";

    [Theory]
    [InlineData("C2\r\n", 52, "43320D0A")]
    [InlineData("W181 030\r\n", 58, "57313831203033300D0A")]
    [InlineData("S\r\n", 51, "530D0A")]
    public void MatchesApkCapturedLayout(string command, int total, string payloadHex)
    {
        byte[] frame = Nrl2Frame.Encode(Sid, Rotor, Encoding.ASCII.GetBytes(command));
        Assert.Equal(total, frame.Length);
        Assert.Equal("NRL2", Encoding.ASCII.GetString(frame, 0, 4));
        Assert.Equal(total.ToString("D2"), Encoding.ASCII.GetString(frame, 4, 2));
        Assert.Equal(5, frame[20]);
        Assert.Equal("DTrac", Encoding.ASCII.GetString(frame, 24, 5));
        Assert.Equal(Sid, Convert.ToHexString(frame.AsSpan(6, 7)));
        Assert.Equal(Rotor, Convert.ToHexString(frame.AsSpan(13, 7)));
        Assert.Equal(payloadHex, Convert.ToHexString(frame.AsSpan(48)));
        Assert.True(Nrl2Frame.TryParse(frame, out var parsed));
        Assert.Equal(command, Encoding.ASCII.GetString(parsed.Payload));
        Assert.False(Nrl2Frame.TryDecodeReply(frame, Sid, Rotor, out _));
    }

    [Fact]
    public void ReverseIdentifiersAreRequiredOnReplies()
    {
        byte[] response = Nrl2Frame.EncodeDevice(Rotor, Sid, "AZ=180  EL=030\r\n"u8);
        Assert.True(Nrl2Frame.TryDecodeReply(response, Sid, Rotor, out var body));
        Assert.True(Gs232Commands.TryParsePosition(body, out var pos));
        Assert.Equal(new RotatorPosition(180, 30), pos);
        response[20] = 6;
        Assert.False(Nrl2Frame.TryDecodeReply(response, Sid, Rotor, out _));
    }

    [Fact]
    public void FirmwareHeartbeatUsesBinaryLengthAndType2()
    {
        byte[] heartbeat = Nrl2Frame.EncodeDevice(Rotor, Rotor, ReadOnlySpan<byte>.Empty,
            Nrl2Frame.HeartbeatType);
        Assert.Equal(48, heartbeat.Length);
        Assert.Equal(0, heartbeat[4]);
        Assert.Equal(48, heartbeat[5]);
        Assert.Equal(1, heartbeat[21]);
        Assert.True(Nrl2Frame.TryParse(heartbeat, out var message, Nrl2LengthEncoding.FirmwareBigEndian));
        Assert.Equal(Nrl2Frame.HeartbeatType, message.Type);
        Assert.False(Nrl2Frame.TryParse(heartbeat, out _, Nrl2LengthEncoding.AppAsciiDecimal));
        heartbeat[5] = 49;
        Assert.False(Nrl2Frame.TryParse(heartbeat, out _, Nrl2LengthEncoding.FirmwareBigEndian));
    }

    [Fact]
    public void FirmwareReplyRequiresCorrectDirectionalLength()
    {
        var packet = Nrl2Frame.EncodeDevice(Rotor, Sid, "AZ=180  EL=030\r\n"u8);
        Assert.Equal(64, packet.Length);
        Assert.Equal(0, packet[4]);
        Assert.Equal(64, packet[5]);
        Assert.True(Nrl2Frame.TryDecodeReply(packet, Sid, Rotor, out var bytes));
        Assert.Equal("AZ=180  EL=030\r\n", Encoding.ASCII.GetString(bytes));
        Assert.False(Nrl2Frame.TryDecodeReply(packet, Sid, Rotor, out _,
            Nrl2LengthEncoding.AppAsciiDecimal));
        Assert.Throws<ArgumentException>(() => Nrl2Frame.EncodeDevice(Rotor, Sid, "x"u8, deviceTag: "invalid"));
    }

    [Fact]
    public void MalformedFramesAndOversizeAreRejected()
    {
        var data = Nrl2Frame.Encode(Sid, Rotor, Gs232Commands.Query());
        data[5] = (byte)'9';
        Assert.False(Nrl2Frame.TryParse(data, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Nrl2Frame.Encode(Sid, Rotor, new byte[52]));
        Assert.Throws<ArgumentException>(() => Nrl2Frame.NormalizeId("INVALID"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gs232Commands.MoveTo(new(500, 30)));
    }

    [Theory]
    [InlineData("cmd=pong&type=RC-3040S&ip=192.0.2.10&port=60020&rotorid=123456789ABCDE\n")]
    [InlineData("cmd=pong&type=1&ip=192.0.2.10&port=60020&id=123456789ABCDE\n")]
    public void DiscoversBothObservedPongDialects(string text)
    {
        Assert.True(Rc3040sDiscovery.TryParsePong(Encoding.ASCII.GetBytes(text),
            IPAddress.Parse("192.0.2.10"), out var rotor));
        Assert.Equal(Rotor, rotor!.RotorId);
        Assert.Equal(60020, rotor.LocalTcpPort);
        Assert.False(Rc3040sDiscovery.TryParsePong(Encoding.ASCII.GetBytes(text),
            IPAddress.Parse("192.0.2.11"), out _));
    }

    [Fact]
    public void DemoRequiresFreshFeedback()
    {
        DateTimeOffset t0 = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        var demo = DemoCruise.Create(0, 20, 30, 10, 1,
            TimeSpan.FromSeconds(2), new RotatorPosition(360, 90), t0);
        Assert.Equal(5, demo.Count);
        Assert.Equal(CruiseState.Waiting, demo.Observe(t0.AddSeconds(1), t0.AddSeconds(1),
            new RotatorPosition(0, 30), true));
        Assert.Equal(CruiseState.NextWaypoint, demo.Observe(t0.AddSeconds(3), t0.AddSeconds(3),
            new RotatorPosition(0, 30), true));
        Assert.Equal(10, demo.Target.Azimuth);
        Assert.Equal(CruiseState.Aborted, demo.Observe(t0.AddSeconds(4), t0.AddSeconds(4),
            new RotatorPosition(10, 30), false));
    }
}
