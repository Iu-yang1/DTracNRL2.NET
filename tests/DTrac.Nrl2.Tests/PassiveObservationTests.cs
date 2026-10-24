using System.Net;
using System.Net.Sockets;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Relay;
using Xunit;

namespace DTrac.Nrl2.Tests;

public sealed class PassiveObservationTests
{
    private const string Sid = "01020304050607";
    private const string Rotor = "123456789ABCDE";

    [Fact]
    public void IdentifiesBothDirectionalFrameEncodingsWithoutExposingFullId()
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 49501);
        byte[] firmware = Nrl2Frame.EncodeDevice(Rotor, Rotor,
            ReadOnlySpan<byte>.Empty, Nrl2Frame.HeartbeatType);
        Assert.True(Nrl2PassiveMonitor.TryDescribe(firmware, remote, out var result));
        Assert.Equal(Nrl2LengthEncoding.FirmwareBigEndian, result.Encoding);
        Assert.Equal(2, result.MessageType);
        Assert.Equal(48, result.FrameBytes);
        Assert.Equal("**********BCDE", result.MaskedSourceId);
        Assert.Equal(0, result.PayloadBytes);

        byte[] app = Nrl2Frame.Encode(Sid, Rotor, "C2\r\n"u8);
        Assert.True(Nrl2PassiveMonitor.TryDescribe(app, remote, out result));
        Assert.Equal(Nrl2LengthEncoding.AppAsciiDecimal, result.Encoding);
        Assert.Equal(5, result.MessageType);
        Assert.Equal(4, result.PayloadBytes);
        Assert.Equal("**********0607", result.MaskedSourceId);
        app[5] = (byte)'9';
        Assert.False(Nrl2PassiveMonitor.TryDescribe(app, remote, out _));
    }

    [Fact]
    public async Task PassiveListenerReceivesHeartbeatAndSendsNothing()
    {
        using var monitor = new Nrl2PassiveMonitor(new IPEndPoint(IPAddress.Loopback, 0));
        using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var observed = new TaskCompletionSource<Nrl2PassiveObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.FrameObserved += item => observed.TrySetResult(item);
        var running = monitor.RunAsync(cancel.Token);
        try
        {
            var heartbeat = Nrl2Frame.EncodeDevice(Rotor, Rotor,
                ReadOnlySpan<byte>.Empty, Nrl2Frame.HeartbeatType);
            await device.SendAsync(heartbeat, monitor.LocalEndPoint);
            var item = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancel.Token);
            Assert.Equal(Nrl2Frame.HeartbeatType, item.MessageType);
            using var shortTime = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await device.ReceiveAsync(shortTime.Token));
        }
        finally { cancel.Cancel(); await running; }
    }
}
