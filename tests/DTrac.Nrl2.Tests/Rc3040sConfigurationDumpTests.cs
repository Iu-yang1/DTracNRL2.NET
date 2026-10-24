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
