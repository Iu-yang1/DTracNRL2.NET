using System.Net;
using System.Text;
using DTrac.Nrl2.Discovery;
using DTrac.Nrl2.Protocol;
using Xunit;

namespace DTrac.Nrl2.Tests;

/// <summary>
/// Regression vectors based on the real October 24, 2026 RC-3040S diagnostics.
/// Synthetic 14-hex IDs are used instead of the user's device identifier.
/// </summary>
public sealed class Rc3040sHardwareEvidenceTests
{
    private const string SyntheticId = "A1B2C3D4E5F607";

    [Theory]
    [InlineData("192.168.001.009", "192.168.1.9")]
    [InlineData("008.008.008.008", "8.8.8.8")]
    [InlineData("192.168.1.9", "192.168.1.9")]
    [InlineData("000.000.000.000", "0.0.0.0")]
    [InlineData("255.255.255.255", "255.255.255.255")]
    public void ParsesDecimalPaddedIPv4WithoutOctalAssumptions(string wire, string expected)
    {
        Assert.True(Rc3040sDiscovery.TryParseDecimalIpv4(wire, out var result));
        Assert.Equal(IPAddress.Parse(expected), result);
    }

    [Theory]
    [InlineData("192.168.001.999")]
    [InlineData("192.168.0009.009")]
    [InlineData("192.168.001")]
    [InlineData("192.168.001.009.1")]
    [InlineData("192.168.001.-09")]
    [InlineData("192.168.001.0x9")]
    [InlineData(" 192.168.001.009")]
    [InlineData("192.168.001.009 ")]
    public void RejectsMalformedDecimalIpv4(string wire) =>
        Assert.False(Rc3040sDiscovery.TryParseDecimalIpv4(wire, out _));

    [Fact]
    public void ParsesTheExactShapeOfRealUnicastPong()
    {
        const string prefix = "cmd=pong&type=RC-3040S&ip=192.168.001.009&port=60020&rotorid=";
        byte[] wire = Encoding.ASCII.GetBytes(prefix + SyntheticId);
        Assert.Equal(75, wire.Length);
        Assert.True(Rc3040sDiscovery.TryParsePong(wire, IPAddress.Parse("192.168.1.9"), out var rotor));
        Assert.NotNull(rotor);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), rotor.Address);
        Assert.Equal(60020, rotor.LocalTcpPort);
        Assert.Equal(SyntheticId, rotor.RotorId);
        Assert.False(Rc3040sDiscovery.TryParsePong(wire,
            IPAddress.Parse("192.168.1.10"), out _));
    }

    [Fact]
    public void FirmwareBannerIsNotPositionOrAtWriteAck()
    {
        string text = "RC_3040S_67S_8H_260223_C " + SyntheticId + "\r\n";
        Assert.True(Rc3040sFirmwareBanner.TryParse(text, out var identity));
        Assert.Equal("RC_3040S_67S_8H_260223_C", identity.VersionLabel);
        Assert.Equal("**********F607", identity.MaskedDeviceId);
        Assert.DoesNotContain(SyntheticId, Rc3040sFirmwareBanner.RedactDeviceIds(text));
        Assert.False(Rc3040sFirmwareBanner.TryParse("AZ=180  EL=090\r\n", out _));
        Assert.False(Rc3040sFirmwareBanner.TryParse("OK\r\n", out _));
    }

    [Fact]
    public async Task TargetedDiscoveryRejectsNonIpv4BeforeSending()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Rc3040sDiscovery.DiscoverAtAsync(IPAddress.IPv6Loopback,
                TimeSpan.FromMilliseconds(100)));
    }
}
