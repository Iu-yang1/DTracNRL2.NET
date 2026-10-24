using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using DTrac.Nrl2.Transport;
using Xunit;

namespace DTrac.Nrl2.Tests;

/// <summary>All tests are local loopback emulators. No real motor or controller accessed.</summary>
public sealed class Rc3040sAtManagementClientTests
{
    private const string Firmware = "RC_3040S_67S_8H_260223_C";
    private const string FictionalHardwareId = "A1B2C3D4E5F607";

    [Fact]
    public void SpeedAndRestartNeedTwoOperatorConfirmations()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Rc3040sAtWritePermit.ForSpeed(true, 300, Firmware, false, true));
        Assert.Throws<InvalidOperationException>(() =>
            Rc3040sAtWritePermit.ForSpeed(false, 300, Firmware, true, false));
        Assert.Throws<InvalidOperationException>(() =>
            Rc3040sAtWritePermit.ForRestart(Firmware, true, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Rc3040sAtWritePermit.ForSpeed(true, 0, Firmware, true, true));
        Assert.Throws<ArgumentException>(() =>
            Rc3040sAtWritePermit.ForSpeed(true, 300, "unknown firmware", true, true));
    }

    [Theory]
    [InlineData(true, "AT+AZ_SPD=00300\r")]
    [InlineData(false, "AT+EL_SPD=00300\r")]
    public async Task ApprovesExactlyOneSpeedWriteAfterFirmwarePinning(bool azimuth, string expectedAt)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
            using var webSocket = await AcceptAsync(tcp, deadline.Token);
            string first = await ReceiveTextAsync(webSocket, deadline.Token);
            Assert.Equal("C2\r", first);
            foreach (string frame in ConfigFrames(Firmware))
                await webSocket.SendAsync(Encoding.ASCII.GetBytes(frame), WebSocketMessageType.Text,
                    true, deadline.Token);
            string second = await ReceiveTextAsync(webSocket, deadline.Token);
            Assert.Equal(expectedAt, second);
            await webSocket.SendAsync("SPEED command accepted? UNKNOWN\r\n"u8.ToArray(),
                WebSocketMessageType.Text, true, deadline.Token);
        });

        await using var client = await Rc3040sAtManagementClient.ConnectAsync(
            "127.0.0.1", port, deadline.Token);
        var permit = Rc3040sAtWritePermit.ForSpeed(
            azimuth, 300, Firmware, confirmPersistentSettingChange: true,
            confirmUnknownMechanicalEffect: true);
        var result = await client.SendApprovedAsync(permit, deadline.Token);
        Assert.Equal(Firmware, result.FirmwareVersion);
        Assert.True(result.WebSocketSendCompleted);
        Assert.False(result.DeviceAcknowledgementVerified);
        Assert.False(result.PersistentSettingVerified);
        Assert.False(result.ReadbackAvailable);
        Assert.Contains(result.ObservedMessages, x => x.Contains("UNKNOWN", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendApprovedAsync(permit, deadline.Token));
        await server;
    }

    [Fact]
    public async Task Real260223StyleTestEchoIsClassifiedSeparatelyFromConfigurationDump()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
            using var websocket = await AcceptAsync(tcp, deadline.Token);
            Assert.Equal("C2\r", await ReceiveTextAsync(websocket, deadline.Token));
            foreach (var frame in ConfigFrames(Firmware))
                await websocket.SendAsync(Encoding.ASCII.GetBytes(frame),
                    WebSocketMessageType.Text, true, deadline.Token);
            Assert.Equal("AT+TEST=TEST_01\r", await ReceiveTextAsync(websocket, deadline.Token));
            await websocket.SendAsync("TEST_01\r"u8.ToArray(),
                WebSocketMessageType.Text, true, deadline.Token);
            // The banner and YUNTAI messages are not additional echo ACKs.
            await websocket.SendAsync(Encoding.ASCII.GetBytes(
                Firmware + " " + FictionalHardwareId + "\r\n"),
                WebSocketMessageType.Text, true, deadline.Token);
            await websocket.SendAsync(
                "==========YUNTAI SETUP===========\r\nAT+AZ_SPD=05000\r\nAT+EL_SPD=05000\r\n"u8.ToArray(),
                WebSocketMessageType.Text, true, deadline.Token);
        });
        await using var client = await Rc3040sAtManagementClient.ConnectAsync(
            "127.0.0.1", port, deadline.Token);
        var permit = Rc3040sAtWritePermit.ForDiagnosticEcho(
            "TEST_01", Firmware, true, true);
        var result = await client.SendApprovedAsync(permit, deadline.Token);
        Assert.True(result.DiagnosticEchoMatched);
        Assert.False(result.DeviceAcknowledgementVerified);
        Assert.False(result.PersistentSettingVerified);
        Assert.Contains(result.ObservedMessages, x => x == "TEST_01\r");
        Assert.Contains(result.ObservedMessages, x => x.Contains("YUNTAI SETUP", StringComparison.Ordinal));
        await server;
    }

    [Fact]
    public async Task NonMatchingEchoMustNotBeClassifiedAsAcknowledged()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
            using var websocket = await AcceptAsync(tcp, deadline.Token);
            Assert.Equal("C2\r", await ReceiveTextAsync(websocket, deadline.Token));
            foreach (string frame in ConfigFrames(Firmware))
                await websocket.SendAsync(Encoding.ASCII.GetBytes(frame),
                    WebSocketMessageType.Text, true, deadline.Token);
            Assert.Equal("AT+TEST=TEST_01\r", await ReceiveTextAsync(websocket, deadline.Token));
            await websocket.SendAsync("TEST_02\r"u8.ToArray(),
                WebSocketMessageType.Text, true, deadline.Token);
        });
        await using var client = await Rc3040sAtManagementClient.ConnectAsync(
            "127.0.0.1", port, deadline.Token);
        var permit = Rc3040sAtWritePermit.ForDiagnosticEcho("TEST_01", Firmware, true, true);
        var result = await client.SendApprovedAsync(permit, deadline.Token);
        Assert.False(result.DiagnosticEchoMatched);
        Assert.False(result.DeviceAcknowledgementVerified);
        await server;
    }

    [Fact]
    public async Task MismatchedFirmwareNeverTransmitsAt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
            using var webSocket = await AcceptAsync(tcp, deadline.Token);
            Assert.Equal("C2\r", await ReceiveTextAsync(webSocket, deadline.Token));
            foreach (var frame in ConfigFrames("RC_3040S_67S_8H_251217_C"))
                await webSocket.SendAsync(Encoding.ASCII.GetBytes(frame), WebSocketMessageType.Text,
                    true, deadline.Token);
            await Task.Delay(900, deadline.Token);
            Assert.Equal(WebSocketState.Open, webSocket.State);
            // No speed write is necessary to prove rejection: client throws
            // before sending an AT. The mock server does not send an ACK.
        });
        await using var client = await Rc3040sAtManagementClient.ConnectAsync(
            "127.0.0.1", port, deadline.Token);
        var permit = Rc3040sAtWritePermit.ForSpeed(true, 300, Firmware, true, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendApprovedAsync(permit, deadline.Token));
        await server;
    }

    [Fact]
    public async Task ConfigReadOnlyModeSendsJustC2()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
            using var webSocket = await AcceptAsync(tcp, deadline.Token);
            Assert.Equal("C2\r", await ReceiveTextAsync(webSocket, deadline.Token));
            foreach (string frame in ConfigFrames(Firmware))
                await webSocket.SendAsync(Encoding.ASCII.GetBytes(frame), WebSocketMessageType.Text,
                    true, deadline.Token);
            await Task.Delay(900, deadline.Token);
            Assert.Equal(WebSocketState.Open, webSocket.State);
        });
        await using var client = await Rc3040sAtManagementClient.ConnectAsync(
            "127.0.0.1", port, deadline.Token);
        var config = await client.ReadConfigurationAsync(deadline.Token);
        Assert.Equal(Firmware, config.FirmwareVersion);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), config.ConfiguredIp);
        await server;
    }

    [Theory]
    [InlineData(true, false, Rc3040sSpeedReadbackOutcome.Updated)]
    [InlineData(false, true, Rc3040sSpeedReadbackOutcome.Unchanged)]
    public async Task SpeedWriteReadbackUsesSingleWebSocketAndNeverRetries(
        bool azimuth, bool unchanged, Rc3040sSpeedReadbackOutcome expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int accepted = 0, atWrites = 0, c2Reads = 0, attempted = 0;
        ushort requested = 5735;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
            Interlocked.Increment(ref accepted);
            using var ws = await AcceptAsync(tcp, deadline.Token);
            for (int step = 0; step < (unchanged ? 1 : 2); step++)
            {
                Assert.Equal("C2\r", await ReceiveTextAsync(ws, deadline.Token));
                Interlocked.Increment(ref c2Reads);
                foreach (var frame in ConfigFrames(Firmware))
                    await ws.SendAsync(Encoding.ASCII.GetBytes(frame),
                        WebSocketMessageType.Text, true, deadline.Token);
                ushort value = step == 0 ? (ushort)5000 : requested;
                var speedFrame = $"==========YUNTAI SETUP===========\r\n" +
                    $"AT+AZ_SPD={value:D5}\r\nAT+EL_SPD={value:D5}\r\n";
                await ws.SendAsync(Encoding.ASCII.GetBytes(speedFrame),
                    WebSocketMessageType.Text, true, deadline.Token);
                if (unchanged || step == 1) break; // readback must NOT trigger a second AT
                string at = await ReceiveTextAsync(ws, deadline.Token);
                Assert.Equal(azimuth ? "AT+AZ_SPD=05735\r" : "AT+EL_SPD=05735\r", at);
                Interlocked.Increment(ref atWrites);
            }
            // Permit a 700-ms idle capture window before normal shutdown.
            await Task.Delay(1000, deadline.Token);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        });
        await using var client = await Rc3040sAtManagementClient.ConnectAsync(
            "127.0.0.1", port, deadline.Token);
        var permit = Rc3040sAtWritePermit.ForSpeed(
            azimuth, unchanged ? (ushort)5000 : requested, Firmware, true, true);
        var result = await client.SendSpeedAndReadbackAsync(
            permit, "**********F607", () => Interlocked.Increment(ref attempted),
            deadline.Token);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(unchanged ? 0 : 1, attempted);
        Assert.Equal(unchanged ? 0 : 1, atWrites);
        Assert.Equal(unchanged ? 1 : 2, c2Reads);
        Assert.Equal(1, accepted);
        Assert.False(result.PersistentSettingVerified);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendSpeedAndReadbackAsync(permit));
        await server;
    }

    [Fact]
    public async Task SpeedPreflightMismatchBlocksWrite()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
            using var ws = await AcceptAsync(tcp, deadline.Token);
            Assert.Equal("C2\r", await ReceiveTextAsync(ws, deadline.Token));
            foreach (var frame in ConfigFrames(Firmware))
                await ws.SendAsync(Encoding.ASCII.GetBytes(frame),
                    WebSocketMessageType.Text, true, deadline.Token);
            await ws.SendAsync("==========YUNTAI SETUP===========\r\nAT+AZ_SPD=05000\r\n"u8.ToArray(),
                WebSocketMessageType.Text, true, deadline.Token);
            await Task.Delay(950, deadline.Token);
            Assert.Equal(WebSocketState.Open, ws.State);
        });
        await using var client = await Rc3040sAtManagementClient.ConnectAsync(
            "127.0.0.1", port, deadline.Token);
        var permit = Rc3040sAtWritePermit.ForSpeed(true, 5735, Firmware, true, true);
        int attempted = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendSpeedAndReadbackAsync(permit, "**********FFFF",
                () => attempted++, deadline.Token));
        Assert.Equal(0, attempted);
        await server;
    }

    private static IEnumerable<string> ConfigFrames(string version)
    {
        yield return version + " " + FictionalHardwareId + "\r\n";
        yield return "===========NET SETUP===========\r\nAT+IP=192.168.001.009\r\nAT+GATEWAY=192.168.001.001\r\n";
        yield return "AT+MASK=255.255.255.000\r\nAT+DNS=114.114.114.114\r\nAT+DHCP=ON\r\n";
        yield return "===========UDP Destination IP SETUP===========\r\nAT+D_IP=202.141.176.002\r\n";
    }

    private static async Task<WebSocket> AcceptAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        var stream = tcp.GetStream();
        using var raw = new MemoryStream();
        byte[] one = new byte[1];
        while (raw.Length < 8192)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
                throw new EndOfStreamException();
            raw.Write(one);
            byte[] bytes = raw.ToArray();
            if (bytes.Length >= 4 && bytes.AsSpan(^4).SequenceEqual("\r\n\r\n"u8))
                break;
        }
        string headers = Encoding.ASCII.GetString(raw.ToArray());
        string key = headers.Split("\r\n", StringSplitOptions.None)
            .First(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        string accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n" +
            "Connection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"),
            cancellationToken);
        return WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
    }

    private static async Task<string> ReceiveTextAsync(WebSocket webSocket, CancellationToken token)
    {
        byte[] bytes = new byte[512];
        var message = await webSocket.ReceiveAsync(new ArraySegment<byte>(bytes), token);
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        return Encoding.ASCII.GetString(bytes, 0, message.Count);
    }
}
