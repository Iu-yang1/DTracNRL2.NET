using System.Net;
using DTrac.Nrl2.Protocol;
using Xunit;

namespace DTrac.Nrl2.Tests;

public class Rc3040sConfigurationDumpTests
{
    private static readonly string[] RealShapeSyntheticIdentifier =
    [
        "RC_3040S_67S_8H_260223_C A1B2C3D4E5F607\r\n",
        "===========NET SETUP===========\r\nAT+IP=192.168.001.009\r\nAT+GATEWAY=192.168.001.001\r\n",
        "AT+MASK=255.255.255.000\r\nAT+PHY=00:08:10:08:82:C0\r\nAT+DNS=114.114.114.114\r\nAT+DHCP=ON\r\n",
        "===========UDP Destination IP SETUP===========\r\nAT+D_IP=202.141.176.002\r\n"
    ];

    [Fact]
    public void ParsesRealHardwareMultiframeConfigurationDump()
    {
        var parsed = Rc3040sConfigurationDump.Parse(RealShapeSyntheticIdentifier);
        Assert.Equal("RC_3040S_67S_8H_260223_C", parsed.FirmwareVersion);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), parsed.ConfiguredIp);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), parsed.ConfiguredGateway);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), parsed.ConfiguredMask);
        Assert.Equal(IPAddress.Parse("114.114.114.114"), parsed.ConfiguredDns);
        Assert.True(parsed.DhcpEnabled);
        Assert.Equal(IPAddress.Parse("202.141.176.2"), parsed.ConfiguredUdpDestination);
        Assert.Equal("**:**:**:**:82:C0", parsed.MaskedPhysicalAddress);
        Assert.True(parsed.SawNetworkSection);
        Assert.True(parsed.SawUdpDestinationSection);
        Assert.Equal(7, parsed.RecognizedFieldCount);
        Assert.Equal(4, parsed.WebSocketMessageCount);
    }

    [Fact]
    public void ParsingDoesNotDependOnWebSocketMessageBoundaries()
    {
        string data = string.Concat(RealShapeSyntheticIdentifier);
        var frames = new [] { data[..28], data[28..79], data[79..148], data[148..] };
        var parsed = Rc3040sConfigurationDump.Parse(frames);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), parsed.ConfiguredIp);
        Assert.Equal(IPAddress.Parse("202.141.176.2"), parsed.ConfiguredUdpDestination);
    }

    [Fact]
    public void ParsesActual260223YuntaiSnapshotIncludingRawSpeedValues()
    {
        // Synthetic rotor ID and MAC, otherwise shaped like the 12:29 hardware log.
        string[] frames =
        [
            "RC_3040S_67S_8H_260223_C A1B2C3D4E5F607\r\n",
            "===========NET SETUP===========\r\nAT+IP=192.168.001.009\r\nAT+GATEWAY=192.168.001.001\r\n",
            "AT+MASK=255.255.255.000\r\nAT+PHY=00:11:22:33:44:55\r\nAT+DNS=114.114.114.114\r\nAT+DHCP=ON\r\n",
            "===========UDP Destination IP SETUP===========\r\nAT+D_IP=202.141.176.002\r\n",
            "===========CALL/SSID===========\r\nAT+CALL=SAMPLE\r\nAT+SSID=003\r\nAT+S_ID=A1B2C3D4E5F607\r\n",
            "==========YUNTAI SETUP===========\r\nAT+YT=3040\r\nAT+XW=JX\r\nAT+P450\r\nAT+AZ_SPD=05000\r\nAT+EL_SPD=05000\r\n"
        ];
        var result = Rc3040sConfigurationDump.Parse(frames);
        Assert.Equal((ushort)5000, result.AzimuthSpeedRaw);
        Assert.Equal((ushort)5000, result.ElevationSpeedRaw);
        Assert.Equal("3040", result.ModelToken);
        Assert.Equal("JX", result.XwModeToken);
        Assert.Equal("P450", result.TravelModeToken);
        Assert.True(result.SawYuntaiSection);
        Assert.True(result.SawIdentitySection);
        Assert.Equal(12, result.RecognizedFieldCount);
        Assert.Equal(6, result.WebSocketMessageCount);
        Assert.Equal("**:**:**:**:44:55", result.MaskedPhysicalAddress);
    }

    [Theory]
    [InlineData("AT+AZ_SPD=65536\r\n")]
    [InlineData("AT+EL_SPD=99999\r\n")]
    [InlineData("AT+AZ_SPD=5\r\n")]
    [InlineData("AT+AZ_SPD=00000\r\n")]
    [InlineData("AT+AZ_SPD=05X00\r\n")]
    public void RejectsUntrustedOrUncalibratedSpeedEncoding(string line)
    {
        var result = Rc3040sConfigurationDump.Parse([line]);
        Assert.Null(result.AzimuthSpeedRaw);
        Assert.Null(result.ElevationSpeedRaw);
    }

    [Fact]
    public void FirmwareModeTokenIsNotInterpretedAsMeasuredMechanicalTravel()
    {
        var result = Rc3040sConfigurationDump.Parse(["AT+P450\r\n"]);
        Assert.Equal("P450", result.TravelModeToken);
        Assert.Null(result.AzimuthSpeedRaw);
        Assert.False(result.SawYuntaiSection);
    }

    [Fact]
    public void InvalidIpCannotOverridePreviouslyParsedValue()
    {
        var parsed = Rc3040sConfigurationDump.Parse(
            ["AT+IP=192.168.001.009\r\nAT+IP=192.168.999.009\r\n"]);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), parsed.ConfiguredIp);
    }

    [Fact]
    public void RejectsOversizedUntrustedCapture()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Rc3040sConfigurationDump.Parse([new string('X', 16385)]));
    }

    [Fact]
    public void DoesNotMislabelBannerOrAtConfigurationAsMotorAcknowledgement()
    {
        var parsed = Rc3040sConfigurationDump.Parse(
            ["RC_3040S_67S_8H_260223_C A1B2C3D4E5F607\r\nAT+D_IP=202.141.176.002\r\n"]);
        Assert.Null(parsed.ConfiguredIp);
        Assert.Equal(IPAddress.Parse("202.141.176.2"), parsed.ConfiguredUdpDestination);
        Assert.False(parsed.SawNetworkSection);
    }
}
