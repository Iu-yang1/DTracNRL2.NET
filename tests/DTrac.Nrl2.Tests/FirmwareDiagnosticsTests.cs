using System.Net;
using System.Text;
using DTrac.Nrl2.Rotator;
using Xunit;

namespace DTrac.Nrl2.Tests;

public sealed class FirmwareDiagnosticsTests
{
    [Theory]
    [InlineData("AZ SPEED: 00300  HZ , 12.50 /s ", Rc3040sAxis.Azimuth, 300, 12.5)]
    [InlineData("AZ SPEED: 05000  HZ , 4.98 °/s ", Rc3040sAxis.Azimuth, 5000, 4.98)]
    [InlineData("EL SPEED: 05000  HZ , 5.51 °/s ", Rc3040sAxis.Elevation, 5000, 5.51)]
    [InlineData("EL SPEED: 65535  HZ , 0.25 /s", Rc3040sAxis.Elevation, 65535, 0.25)]
    [InlineData("az speed: 00001 HZ , .50 /s", Rc3040sAxis.Azimuth, 1, .5)]
    public void ParsesFirmwareSpeedDiagnosticButNeverAssumesDegreesPerSecond(
        string line, Rc3040sAxis expectedAxis, ushort hz, double rate)
    {
        Assert.True(Rc3040sDiagnosticParser.TryParseSpeedLine(line, out var diagnostic));
        Assert.Equal(expectedAxis, diagnostic.Axis);
        Assert.Equal(hz, diagnostic.RawFrequencyHz);
        Assert.Equal(rate, diagnostic.ReportedRatePerSecond, precision: 8);
    }

    [Theory]
    [InlineData("AZ SPEED: 99999 HZ , 1.0 /s")]
    [InlineData("AZ SPEED: 00300 HZ , NaN /s")]
    [InlineData("AZ SPEED: 300 HZ , 12.50 /s")]
    [InlineData("AZ SPEED: 00300 HZ , 12.5 rpm")]
    [InlineData("AT+AZ_SPD=00300")]
    public void RejectsMalformedOrNonDiagnosticLines(string line) =>
        Assert.False(Rc3040sDiagnosticParser.TryParseSpeedLine(line, out _));

    [Fact]
    public void DeviceCallAndSsidConfigurationHaveBoundedFormats()
    {
        Assert.Equal("AT+CALL=BG5JSU\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetCallsign("BG5JSU")));
        Assert.Equal("AT+CALL=TEST1\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetCallsign("TEST1")));
        Assert.Equal("AT+SSID=000\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetDeviceSsid(0)));
        Assert.Equal("AT+SSID=255\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetDeviceSsid(byte.MaxValue)));
        Assert.Throws<ArgumentException>(() => Rc3040sFirmwareCommands.SetCallsign("ABCDEFG"));
        Assert.Throws<ArgumentException>(() => Rc3040sFirmwareCommands.SetCallsign("ab12"));
        Assert.Throws<ArgumentException>(() => Rc3040sFirmwareCommands.SetCallsign("A\rB"));
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)14)]
    public void RejectsNonrepresentableLowRawSpeed(ushort raw)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Rc3040sFirmwareCommands.SetAzimuthSpeed(raw));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Rc3040sFirmwareCommands.SetElevationSpeed(raw));
    }

    [Fact]
    public void AcceptsMinimumNumericallyRepresentableRawSpeedButDoesNotClaimMechanicalSafety()
    {
        Assert.Equal("AT+AZ_SPD=00015\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetAzimuthSpeed(15)));
    }

    [Fact]
    public void IPv4FirmwareConfigUsesFixedThreeDigitOctets()
    {
        var address = IPAddress.Parse("192.168.1.50");
        Assert.Equal("AT+IP=192.168.001.050\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetIpAddress(address)));
        Assert.Equal("AT+GATEWAY=192.168.001.050\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetGateway(address)));
        Assert.Equal("AT+MASK=255.255.255.000\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetSubnetMask(IPAddress.Parse("255.255.255.0"))));
        Assert.Equal("AT+DNS=008.008.008.008\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetDns(IPAddress.Parse("8.8.8.8"))));
        Assert.Equal("AT+D_IP=192.168.001.050\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetRemoteRelayIp(address)));
        Assert.Throws<ArgumentException>(() =>
            Rc3040sFirmwareCommands.SetIpAddress(IPAddress.IPv6Loopback));
        Assert.Throws<ArgumentException>(() =>
            Rc3040sFirmwareCommands.SetRemoteRelayIp(IPAddress.IPv6Loopback));
        Assert.Equal(60050, Rc3040sNetworkPorts.FactoryRelayPortValue);
    }

    [Fact]
    public void DiagnosticEchoHasNoCruiseSemantics()
    {
        Assert.Equal("AT+TEST=hello\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.DiagnosticEcho("hello")));
        Assert.Throws<ArgumentException>(() => Rc3040sFirmwareCommands.DiagnosticEcho("\r"));
        Assert.Throws<ArgumentException>(() => Rc3040sFirmwareCommands.DiagnosticEcho(""));
        Assert.Equal("AT+AZ_SPD=00042\r",
            Encoding.ASCII.GetString(Rc3040sFirmwareCommands.SetAzimuthSpeed(42)));
    }
}
