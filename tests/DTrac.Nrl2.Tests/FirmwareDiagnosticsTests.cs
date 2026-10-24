using System.Text;
using DTrac.Nrl2.Rotator;
using Xunit;

namespace DTrac.Nrl2.Tests;

public sealed class FirmwareDiagnosticsTests
{
    [Theory]
    [InlineData("AZ SPEED: 00300  HZ , 12.50 /s ", Rc3040sAxis.Azimuth, 300, 12.5)]
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
