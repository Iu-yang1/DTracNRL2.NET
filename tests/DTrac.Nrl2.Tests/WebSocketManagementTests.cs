using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using DTrac.Nrl2.Rotator;
using DTrac.Nrl2.Transport;
using Xunit;

namespace DTrac.Nrl2.Tests;

/// <summary>WebSocket protocol tests against loopback server, never real hardware.</summary>
public sealed class WebSocketManagementTests
{
    [Fact]
    public async Task ConnectionIsPassiveAndTextPayloadRequiresExplicitSend()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
                var stream = accepted.GetStream();
                string headers = await ReadHttpHeadersAsync(stream, deadline.Token);
                Assert.StartsWith("GET / HTTP/1.1", headers);
                string key = headers.Split("\r\n", StringSplitOptions.None)
                    .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    .Split(':', 2)[1].Trim();
                byte[] digest = SHA1.HashData(Encoding.ASCII.GetBytes(
                    key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + Convert.ToBase64String(digest) + "\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), deadline.Token);
                using var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
                // After handshake, client is passive (no implicit speed/reset command).
                await Task.Delay(30, deadline.Token);
                Assert.False(stream.DataAvailable);
                byte[] welcome = Encoding.UTF8.GetBytes("{\"type\":\"welcome\"}");
                await ws.SendAsync(new ArraySegment<byte>(welcome), WebSocketMessageType.Text,
                    true, deadline.Token);
                var receiveBuffer = new byte[128];
                var incoming = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), deadline.Token);
                Assert.Equal(WebSocketMessageType.Text, incoming.MessageType);
                Assert.Equal("AT+AZ_SPD=00300\r", Encoding.UTF8.GetString(receiveBuffer, 0, incoming.Count));
            });
            await using var client = await Rc3040sWebSocketClient.ConnectAsync(
                "127.0.0.1", port, deadline.Token);
            Assert.Equal("{\"type\":\"welcome\"}",
                await client.ReceiveTextAsync(TimeSpan.FromSeconds(2), deadline.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SendFirmwareCommandAsync(Rc3040sFirmwareCommands.SetAzimuthSpeed(300), false));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SendTextAsync("AT+AZ_SPD=00300\r", cancellationToken: deadline.Token));
            await client.SendFirmwareCommandAsync(
                Rc3040sFirmwareCommands.SetAzimuthSpeed(300), true, deadline.Token);
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task PassiveReadTimeoutDoesNotDiscardLaterReplyOrAbortWebSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(cts.Token);
            var stream = accepted.GetStream();
            string headers = await ReadHttpHeadersAsync(stream, cts.Token);
            string key = headers.Split("\r\n", StringSplitOptions.None)
                .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1].Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n"), cts.Token);
            using var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
            await Task.Delay(180, cts.Token);
            await ws.SendAsync("welcome"u8.ToArray(), WebSocketMessageType.Text, true, cts.Token);
            byte[] buffer = new byte[64];
            var incoming = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            Assert.Equal("C2\r", Encoding.ASCII.GetString(buffer, 0, incoming.Count));
            await ws.SendAsync("RC_3040S_67S_8H_260223_C A1B2C3D4E5F607\r\n"u8.ToArray(),
                WebSocketMessageType.Text, true, cts.Token);
        });
        await using var client = await Rc3040sWebSocketClient.ConnectAsync("127.0.0.1", port, cts.Token);
        Assert.Null(await client.ReceiveTextAsync(TimeSpan.FromMilliseconds(50), cts.Token));
        Assert.Equal(WebSocketState.Open, client.State);
        Assert.Equal("welcome", await client.ReceiveTextAsync(TimeSpan.FromSeconds(2), cts.Token));
        await client.SendTextAsync("C2\r", cancellationToken: cts.Token);
        string? result = await client.ReceiveTextAsync(TimeSpan.FromSeconds(2), cts.Token);
        Assert.StartsWith("RC_3040S_67S_8H_260223_C", result);
        await server;
    }

    [Fact]
    public async Task CapturesCompleteMultiFrameConfigurationWithExactlyOneC2()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = accepted.GetStream();
            string headers = await ReadHttpHeadersAsync(stream, deadline.Token);
            string key = headers.Split("\r\n", StringSplitOptions.None)
                .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1].Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n"), deadline.Token);
            using var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));

            var buffer = new byte[64];
            var inbound = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            Assert.Equal("C2\r", Encoding.ASCII.GetString(buffer, 0, inbound.Count));

            string[] four = [
                "RC_3040S_67S_8H_260223_C A1B2C3D4E5F607\r\n",
                "===========NET SETUP===========\r\nAT+IP=192.168.001.009\r\n",
                "AT+MASK=255.255.255.000\r\nAT+DHCP=ON\r\n",
                "===========UDP Destination IP SETUP===========\r\nAT+D_IP=202.141.176.002\r\n"
            ];
            foreach (string frame in four)
            {
                await ws.SendAsync(Encoding.ASCII.GetBytes(frame), WebSocketMessageType.Text,
                    true, deadline.Token);
                await Task.Delay(30, deadline.Token);
            }
            // Give the client time to observe a quiet inter-frame interval.
            await Task.Delay(300, deadline.Token);
            Assert.False(stream.DataAvailable); // no accidental second C2 in queue
        });
        try
        {
            await using var client = await Rc3040sWebSocketClient.ConnectAsync(
                "127.0.0.1", port, deadline.Token);
            var burst = await Rc3040sWebSocketCaptures.CaptureReadOnlyC2Async(
                client, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(180),
                TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(4, burst.Frames.Count);
            Assert.True(burst.QuietGapObserved);
            Assert.False(burst.LimitsReached);
            var configuration = DTrac.Nrl2.Protocol.Rc3040sConfigurationDump.Parse(burst.Frames);
            Assert.Equal(IPAddress.Parse("192.168.1.9"), configuration.ConfiguredIp);
            Assert.Equal(IPAddress.Parse("202.141.176.2"), configuration.ConfiguredUdpDestination);
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task NormalDisposalSendsWebSocketCloseFrameInsteadOfAbruptAbort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
                var stream = accepted.GetStream();
                string headers = await ReadHttpHeadersAsync(stream, deadline.Token);
                string key = headers.Split("\r\n", StringSplitOptions.None)
                    .First(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    .Split(':', 2)[1].Trim();
                string accept = Convert.ToBase64String(SHA1.HashData(
                    Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n\r\n"), deadline.Token);
                using var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
                byte[] buffer = new byte[32];
                var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                Assert.Equal(WebSocketMessageType.Close, received.MessageType);
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", deadline.Token);
            });
            var client = await Rc3040sWebSocketClient.ConnectAsync("127.0.0.1", port, deadline.Token);
            await client.DisposeAsync();
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ClientValidatesHostAndPortBeforeNetworkAccess()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Rc3040sWebSocketClient.ConnectAsync("127.0.0.1/unsafe"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Rc3040sWebSocketClient.ConnectAsync("127.0.0.1", 0));
    }

    private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < 8192)
        {
            int n = await stream.ReadAsync(buffer, token);
            if (n == 0) throw new EndOfStreamException();
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 &&
                bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Oversize handshake request.");
    }
}
