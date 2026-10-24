using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Relay;
using DTrac.Nrl2.Rotator;
using DTrac.Nrl2.Transport;
using Xunit;

namespace DTrac.Nrl2.Tests;

public sealed class GatewayTests
{
    private const string Sid = "01020304050607";
    private const string Rotor = "123456789ABCDE";

    [Fact]
    public async Task UdpNrl2IsTranslatedToLocalTcpGs232WithoutCloudRelay()
    {
        using var tcpServer = new TcpListener(IPAddress.Loopback, 0);
        tcpServer.Start();
        var port = ((IPEndPoint)tcpServer.LocalEndpoint).Port;
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hardware = Task.Run(async () =>
        {
            using var incoming = await tcpServer.AcceptTcpClientAsync(token.Token);
            using var stream = incoming.GetStream();
            var request = new byte[3];
            int offset = 0;
            while (offset < request.Length)
            {
                int count = await stream.ReadAsync(request.AsMemory(offset), token.Token);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
            Assert.Equal("C2\r", Encoding.ASCII.GetString(request));
            await stream.WriteAsync("AZ=181  EL=030\r\n"u8.ToArray(), token.Token);
        });

        using var gateway = new Nrl2TcpGateway(
            new IPEndPoint(IPAddress.Loopback, 0), Rotor, "127.0.0.1", port);
        var serve = gateway.RunAsync(token.Token);
        try
        {
            using var app = new Nrl2UdpClient("127.0.0.1", gateway.LocalEndPoint.Port, Sid, Rotor);
            var position = await app.QueryPositionAsync(TimeSpan.FromSeconds(2), token.Token);
            Assert.Equal(new RotatorPosition(181, 30), position);
            await hardware;
        }
        finally { token.Cancel(); await serve; }
    }

    [Fact]
    public void GatewayRequiresExplicitPhysicalTravelBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Nrl2TcpGateway(
            new IPEndPoint(IPAddress.Loopback, 0), Rotor, "127.0.0.1", maxAzimuth: 451));
    }
}
