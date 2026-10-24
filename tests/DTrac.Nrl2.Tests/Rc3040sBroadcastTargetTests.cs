using System.Net;
using DTrac.Nrl2.Discovery;
using Xunit;

namespace DTrac.Nrl2.Tests;

public class Rc3040sBroadcastTargetTests
{
    [Fact]
    public void DeviceSubnetBroadcastIsComputedWithActualPrefix()
    {
        var targets = Rc3040sDiscovery.GetSubnetBroadcastTargets([
            (IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.0")),
            (IPAddress.Parse("10.1.5.20"), IPAddress.Parse("255.255.0.0")),
            (IPAddress.Parse("192.168.1.6"), IPAddress.Parse("255.255.255.0"))
        ]);
        Assert.Equal(2, targets.Count);
        Assert.Contains(IPAddress.Parse("192.168.1.255"), targets);
        Assert.Contains(IPAddress.Parse("10.1.255.255"), targets);
    }

    [Fact]
    public void HostRoutesDoNotCreateMisleadingBroadcastDestinations()
    {
        var targets = Rc3040sDiscovery.GetSubnetBroadcastTargets([
            (IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.255")),
            (IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.254"))
        ]);
        Assert.Empty(targets);
    }
}
