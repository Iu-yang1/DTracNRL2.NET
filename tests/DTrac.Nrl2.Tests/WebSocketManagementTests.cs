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
