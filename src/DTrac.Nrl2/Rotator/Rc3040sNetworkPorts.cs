namespace DTrac.Nrl2.Rotator;

/// <summary>
/// Firmware-observed local ports on RC-3040S (251217 image).
/// Management TCP/60030 has NOT yet been validated for naked ASCII AT commands.
/// </summary>
public static class Rc3040sNetworkPorts
{
    public const int Gs232TcpPort = 60020;
    public const int ManagementTcpPort = 60030;
    public const int DiscoveryUdpPort = 60059;
}
