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
    public async Task AnotherUdpEndpointWithSameSidCannotReuseFreshnessToMoveMotor()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        int accepts = 0;
        var listenerTask = Task.Run(async () =>
        {
            using var first = await listener.AcceptTcpClientAsync(cts.Token);
            Interlocked.Increment(ref accepts);
            var stream = first.GetStream();
            byte[] query = new byte[3];
            await stream.ReadExactlyAsync(query, cts.Token);
            Assert.Equal("C2\r", Encoding.ASCII.GetString(query));
            await stream.WriteAsync("AZ=180  EL=030\r\n"u8.ToArray(), cts.Token);
            // Once the first read-only query is answered, no additional TCP
            // connection should occur for a forged same-SID motion command.
            await Task.Delay(250, cts.Token);
        });
        using var gateway = new Nrl2TcpGateway(
            new IPEndPoint(IPAddress.Loopback, 0), Rotor, "127.0.0.1", port,
            allowMotorCommands: true);
        var runner = gateway.RunAsync(cts.Token);
        try
        {
            using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await first.SendAsync(Nrl2Frame.Encode(Sid, Rotor, Gs232Commands.Query()), gateway.LocalEndPoint);
            var response = await first.ReceiveAsync(cts.Token);
            Assert.True(Nrl2Frame.TryDecodeReply(response.Buffer, Sid, Rotor, out _));
            await second.SendAsync(Nrl2Frame.Encode(Sid, Rotor,
                Gs232Commands.MoveTo(new RotatorPosition(180, 30))), gateway.LocalEndPoint);
            await listenerTask;
            Assert.Equal(1, Volatile.Read(ref accepts));
        }
        finally { cts.Cancel(); await runner; listener.Stop(); }
    }

    [Fact]
    public async Task AStalledControllerTimesOutWithoutKillingUdpGateway()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var simulatedHardware = Task.Run(async () =>
        {
            // Keep the first connection open without answering it, but accept the
            // second request immediately after the gateway reports the first timeout.
            // Waiting longer than operationTimeout here would incorrectly starve
            // the second query and test the harness rather than recovery behavior.
            using var first = await listener.AcceptTcpClientAsync(cancellation.Token);
            byte[] command = new byte[3];
            await first.GetStream().ReadExactlyAsync(command, cancellation.Token);
            Assert.Equal("C2\r", Encoding.ASCII.GetString(command));
            using var second = await listener.AcceptTcpClientAsync(cancellation.Token);
            byte[] otherCommand = new byte[3];
            await second.GetStream().ReadExactlyAsync(otherCommand, cancellation.Token);
            Assert.Equal("C2\r", Encoding.ASCII.GetString(otherCommand));
            await second.GetStream().WriteAsync("AZ=182  EL=040\r\n"u8.ToArray(), cancellation.Token);
        });
        using var gateway = new Nrl2TcpGateway(
            new IPEndPoint(IPAddress.Loopback, 0), Rotor, "127.0.0.1", port,
            operationTimeout: TimeSpan.FromMilliseconds(150));
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.TransportFailure += ex => error.TrySetResult(ex);
        Task running = gateway.RunAsync(cancellation.Token);
        try
        {
            using var app = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await app.SendAsync(Nrl2Frame.Encode(Sid, Rotor, Gs232Commands.Query()),
                gateway.LocalEndPoint);
            Exception failure = await error.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token);
            Assert.IsType<TimeoutException>(failure);
            Assert.False(running.IsCompleted);

            await app.SendAsync(Nrl2Frame.Encode(Sid, Rotor, Gs232Commands.Query()),
                gateway.LocalEndPoint);
            var reply = await app.ReceiveAsync(cancellation.Token);
            Assert.True(Nrl2Frame.TryDecodeReply(reply.Buffer, Sid, Rotor, out var body));
            Assert.True(Gs232Commands.TryParsePosition(body, out var pos));
            Assert.Equal(new RotatorPosition(182, 40), pos);
            await simulatedHardware;
        }
        finally { cancellation.Cancel(); await running; }
    }

    [Fact]
    public void GatewayRequiresExplicitPhysicalTravelBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Nrl2TcpGateway(
            new IPEndPoint(IPAddress.Loopback, 0), Rotor, "127.0.0.1", maxAzimuth: 451));
    }
}
