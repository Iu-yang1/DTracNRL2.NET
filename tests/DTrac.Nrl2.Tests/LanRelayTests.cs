using System.Net;
using System.Net.Sockets;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Relay;
using DTrac.Nrl2.Rotator;
using Xunit;

namespace DTrac.Nrl2.Tests;

public sealed class LanRelayTests
{
    private const string Sid = "01020304050607";
    private const string Rotor = "123456789ABCDE";

    [Fact]
    public void RelayRejectsNonQueryMovementUntilExplicitOptIn()
    {
        Assert.True(Nrl2LanRelay.IsAllowedCommand(Gs232Commands.Query(), false));
        Assert.True(Nrl2LanRelay.IsAllowedCommand(Gs232Commands.Stop(), false));
        Assert.False(Nrl2LanRelay.IsAllowedCommand(Gs232Commands.MoveTo(new(180, 30)), false));
        Assert.True(Nrl2LanRelay.IsAllowedCommand(Gs232Commands.MoveTo(new(180, 30)), true));
        Assert.False(Nrl2LanRelay.IsAllowedCommand("AT+AZ_SPD=300\r\n"u8, true));
        Assert.False(Nrl2LanRelay.IsAllowedCommand("W999 190\r\n"u8, true));
        Assert.Equal("AT+AZ_SPD=00300\r", System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetAzimuthSpeed(300)));
        Assert.Equal("AT+EL_SPD=00300\r", System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetElevationSpeed(300)));
    }

    [Fact]
    public void SpeedEncodingIsFiveDigitFixedWidthAndNeverAutoTransmitted()
    {
        Assert.Equal("AT+AZ_SPD=00001\r",
            System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetAzimuthSpeed(1)));
        Assert.Equal("AT+EL_SPD=65535\r",
            System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetElevationSpeed(ushort.MaxValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rc3040sFirmwareCommands.SetAzimuthSpeed(0));
        Assert.Equal(60030, Rc3040sNetworkPorts.ManagementTcpPort);
        Assert.Equal(60020, Rc3040sNetworkPorts.Gs232TcpPort);
        Assert.Equal(60059, Rc3040sNetworkPorts.DiscoveryUdpPort);
    }

    [Fact]
    public void FirmwareConfigurationBuildersAreOptInAndExact()
    {
        Assert.Equal("AT+DHCP=ON\r",
            System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetDhcp(true)));
        Assert.Equal("AT+DHCP=OFF\r",
            System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetDhcp(false)));
        Assert.Equal("AT+YT=YS3051\r",
            System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SelectModel(Rc3040sFirmwareModel.Ys3051)));
        Assert.Equal("AT+P450\r",
            System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SelectAzimuthTravel(true)));
        Assert.Equal("AT+INIT\r",
            System.Text.Encoding.ASCII.GetString(Rc3040sFirmwareCommands.InitializeDefaults()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Rc3040sFirmwareCommands.SelectModel((Rc3040sFirmwareModel)99));
    }

    [Fact]
    public async Task OfflineType2RegistersFirmwareAndRoutesBidirectionally()
    {
        using var relay = new Nrl2LanRelay(new IPEndPoint(IPAddress.Loopback, 0),
            new Dictionary<string, IPAddress> { [Rotor] = IPAddress.Loopback });
        using var app = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var controller = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = relay.RunAsync(cts.Token);
        try
        {
            await controller.SendAsync(
                Nrl2Frame.EncodeDevice(Rotor, Rotor, ReadOnlySpan<byte>.Empty, Nrl2Frame.HeartbeatType),
                relay.LocalEndPoint);
            await app.SendAsync(Nrl2Frame.Encode(Sid, Rotor, Gs232Commands.Query()), relay.LocalEndPoint);
            var inbound = await controller.ReceiveAsync(cts.Token);
            Assert.True(Nrl2Frame.TryParse(inbound.Buffer, out var parsed));
            Assert.Equal("C2\r\n", System.Text.Encoding.ASCII.GetString(parsed.Payload));

            await controller.SendAsync(Nrl2Frame.EncodeDevice(Rotor, Sid, "AZ=180  EL=030\r\n"u8),
                relay.LocalEndPoint);
            var outbound = await app.ReceiveAsync(cts.Token);
            Assert.True(Nrl2Frame.TryDecodeReply(outbound.Buffer, Sid, Rotor, out var reply));
            Assert.True(Gs232Commands.TryParsePosition(reply, out var position));
            Assert.Equal(new RotatorPosition(180, 30), position);
        }
        finally { cts.Cancel(); await running; }
    }
}
