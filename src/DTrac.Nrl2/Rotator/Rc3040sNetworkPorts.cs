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

    /// <summary>
    /// Factory initialization 0x54FE..0x5517 writes EA 92 EA 92 to four
    /// consecutive port configuration bytes 0x30..0x33, so both raw
    /// network-order port words decode to 60050. Verify Socket7 mapping on
    /// real hardware before assuming it is the destination port.
    /// </summary>
    public const int FactoryRelayPortValue = 60050;
}
