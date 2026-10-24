using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DTrac.Nrl2.Rotator;
using DTrac.Nrl2.Transport;
using Xunit;

namespace DTrac.Nrl2.Tests;

/// <summary>Loopback TCP tests: never contact or command real rotator hardware.</summary>
public sealed class LanTcpTests
{
    [Fact]
    public async Task ReadOnlyPositionQueryHandlesFragmentedTcpReply()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(cts.Token);
                var stream = accepted.GetStream();
                Assert.Equal("C2", await ReadCommandAsync(stream, cts.Token));
                await stream.WriteAsync("AZ=181  EL=0"u8.ToArray(), cts.Token);
                await Task.Delay(15, cts.Token);
                await stream.WriteAsync("30\r\n"u8.ToArray(), cts.Token);
            });
            using var client = await Rc3040sLanClient.ConnectAsync("127.0.0.1", port, cts.Token);
            RotatorPosition? result = await client.QueryPositionAsync(TimeSpan.FromSeconds(2), cts.Token);
            Assert.Equal(new RotatorPosition(181, 30), result);
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ConnectionDoesNotMoveAndExplicitStopUsesCrTerminator()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(cts.Token);
                var stream = accepted.GetStream();
                await Task.Delay(50, cts.Token);
                Assert.False(stream.DataAvailable); // No implicit movement, query or START command.
                Assert.Equal("S", await ReadCommandAsync(stream, cts.Token));
            });
            using var client = await Rc3040sLanClient.ConnectAsync("127.0.0.1", port, cts.Token);
            await Task.Delay(75, cts.Token);
            await client.StopAsync(cts.Token);
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task LocalTcpRejectsUnsafeConfigurationWithoutConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Rc3040sLanClient.ConnectAsync("", Rc3040sLanClient.DefaultPort));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Rc3040sLanClient.ConnectAsync("127.0.0.1", 0));
    }

    private static async Task<string> ReadCommandAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] b = new byte[1];
        using var bytes = new MemoryStream();
        while (bytes.Length < 64)
        {
            int n = await stream.ReadAsync(b, token);
            if (n == 0) throw new EndOfStreamException();
            if (b[0] == '\r') return Encoding.ASCII.GetString(bytes.ToArray());
            bytes.WriteByte(b[0]);
        }
        throw new InvalidDataException("Unexpectedly long GS-232 command.");
    }
}
