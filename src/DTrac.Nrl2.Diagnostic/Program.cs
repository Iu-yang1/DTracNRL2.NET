using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DTrac.Nrl2.Discovery;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Transport;
using System.Text.RegularExpressions;

// No developer tools needed on the radio PC. Publish as a self-contained win-x64 EXE.
// This diagnostic never sends GS-232 motion, AT configuration or firmware writes.
// It tests TCP reachability, performs a WebSocket HTTP Upgrade/read-only receive,
// and sends one fixed UDP discovery ping to broadcast, followed by a directed ping.
// Only on explicit operator opt-in, probes known read-only C2 position reports.
internal static class Program
{
    private const int ConnectTimeoutMs = 3500;
    private static readonly List<string> ReportLines = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Title = "RC-3040S LAN Diagnostics";

        if (args is ["--self-test"])
        {
            if (!IPAddress.TryParse("192.168.1.9", out var parsed) ||
                parsed.AddressFamily != AddressFamily.InterNetwork ||
                Rc3040sDiscovery.CreatePing().Length != 9)
                return 1;
            Console.WriteLine("SELF-TEST PASS: no network activity performed.");
            return 0;
        }

        if (args is ["--help"] or ["-h"])
        {
            PrintHelp();
            return 0;
        }

        Write("RC-3040S 局域网只读诊断 / LAN diagnostics v3");
        Write("无需 Git、Python、Visual Studio 或 .NET SDK。");
        Write("不会发送旋转、速度设置、复位或 EEPROM 写入命令。");
        Write("建议先暂停 SkyRoof 自动跟踪，避免竞争 TCP 连接。");
        Write("");

        bool explicitReadOnlyQuery = args.Length == 3 && args[2] == "--read-only-query";
        bool knownHostArguments = args.Length is 2 or 3 && args[0] == "--host" &&
            (args.Length == 2 || explicitReadOnlyQuery);
        IPAddress host;
        if (knownHostArguments &&
            IPAddress.TryParse(args[1], out var fromArgs) &&
            fromArgs.AddressFamily == AddressFamily.InterNetwork)
        {
            host = fromArgs;
        }
        else if (args.Length == 0)
        {
            Console.Write("请输入云台 IPv4 地址（例如 192.168.1.9）：");
            string? input = Console.ReadLine()?.Trim();
            if (!IPAddress.TryParse(input, out var entered) ||
                entered.AddressFamily != AddressFamily.InterNetwork)
            {
                Write("IPv4 地址无效。");
                EndInteractive();
                return 2;
            }
            host = entered;
        }
        else
        {
            PrintHelp();
            return 2;
        }

        bool probeReadOnlyPosition = explicitReadOnlyQuery;
        if (args.Length == 0)
        {
            Console.Write("是否附加只读 C2 角度查询？这会发送 GS-232 C2 但不会启动运动。 [y/N]：");
            probeReadOnlyPosition = string.Equals(
                Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        }

        Write($"目标控制板 IP：{host}");
        Write($"开始时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Write($"C2 只读主动查询：{(probeReadOnlyPosition ? "用户明确启用" : "未启用")}");
        Write("");

        await CheckTcpAsync(host, 60020, "GS-232");
        await CheckTcpAsync(host, 60030, "WebSocket 管理");
        await CheckWebSocketAsync(host);
        if (probeReadOnlyPosition)
        {
            await CheckGs232PositionAsync(host);
            await CheckWebSocketPositionAsync(host);
        }
        await CheckDiscoveryAsync(host);

        Write("");
        Write("诊断已完成；TCP 成功不等于设备响应或运动控制成功。");
        Write("60020 短暂失败可能与 SkyRoof 已有连接有关。");
        string? path = SaveReport();
        Write($"日志路径：{path ?? "保存失败，请复制窗口内容"}");
        Write("请将日志回传，以继续分析 RC-3040S 协议。");
        EndInteractive();
        return 0;
    }

    private static async Task CheckTcpAsync(IPAddress host, int port, string label)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(ConnectTimeoutMs));
        try
        {
            await tcp.ConnectAsync(host, port, cancel.Token);
            Write($"[TCP/{port} {label}] CONNECTED");
        }
        catch (OperationCanceledException)
        {
            Write($"[TCP/{port} {label}] TIMEOUT");
        }
        catch (SocketException ex)
        {
            Write($"[TCP/{port} {label}] FAILED: {ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            Write($"[TCP/{port} {label}] ERROR: {Compact(ex)}");
        }
    }

    private static async Task CheckWebSocketAsync(IPAddress host)
    {
        Write("[WebSocket/60030] 仅进行 HTTP Upgrade + 被动读取");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var ws = await Rc3040sWebSocketClient.ConnectAsync(
                host.ToString(), 60030, timeout.Token);
            Write("[WebSocket/60030] HANDSHAKE OK (RFC 6455)");
            string? unsolicited = await ws.ReceiveTextAsync(TimeSpan.FromSeconds(2));
            Write(unsolicited is null
                ? "[WebSocket/60030] 2 秒内无主动文本消息"
                : "[WebSocket/60030] 主动消息: " + EscapeAndLimit(Rc3040sFirmwareBanner.RedactDeviceIds(unsolicited), 512));
        }
        catch (OperationCanceledException)
        {
            Write("[WebSocket/60030] TIMEOUT");
        }
        catch (Exception ex)
        {
            Write("[WebSocket/60030] FAILED: " + Compact(ex));
            if (ex.InnerException is not null)
                Write("[WebSocket/60030] INNER: " + Compact(ex.InnerException));
        }
    }


    /// <summary>Explicitly opted-in C2 only; never sends W/M/S/jog/AT or EEPROM configuration.</summary>
    private static async Task CheckGs232PositionAsync(IPAddress host)
    {
        Write("[GS-232/60020] 开始 C2 只读角度回读（已获用户确认）");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var client = await Rc3040sLanClient.ConnectAsync(host.ToString(), 60020, cancel.Token);
            var position = await client.QueryPositionAsync(TimeSpan.FromSeconds(2), cancel.Token);
            Write(position is null
                ? "[GS-232/60020] C2 无角度回复/超时"
                : FormattableString.Invariant(
                    $"[GS-232/60020] C2 AZ={position.Value.Azimuth:F1}, EL={position.Value.Elevation:F1}"));
        }
        catch (Exception ex)
        {
            Write("[GS-232/60020] C2 ERROR: " + Compact(ex));
        }
    }

    /// <summary>Only sends a known read-only C2 text query on an isolated new WebSocket session.</summary>
    private static async Task CheckWebSocketPositionAsync(IPAddress host)
    {
        Write("[WebSocket/60030] 开始 C2 只读文本查询（已获用户确认）");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var ws = await Rc3040sWebSocketClient.ConnectAsync(
                host.ToString(), 60030, cancel.Token);
            // The device may route text to AT only, to GS-232, or to neither.
            // A lack of response does NOT disprove the management WebSocket protocol.
            await ws.SendTextAsync("C2\r", cancellationToken: cancel.Token);
            string? reply = await ws.ReceiveTextAsync(TimeSpan.FromSeconds(2), cancel.Token);
            if (reply is null)
                Write("[WebSocket/60030] C2 已发送，未收到文本答复");
            else if (Rc3040sFirmwareBanner.TryParse(reply, out var banner))
                Write($"[WebSocket/60030] 收到设备版本标识（不是角度）: {banner.VersionLabel} {banner.MaskedDeviceId}");
            else
                Write("[WebSocket/60030] C2 后收到文本（尚不能确认应答语义）: " +
                    EscapeAndLimit(Rc3040sFirmwareBanner.RedactDeviceIds(reply), 512));
        }
        catch (Exception ex)
        {
            Write("[WebSocket/60030] C2 ERROR: " + Compact(ex));
        }
    }

    private static async Task CheckDiscoveryAsync(IPAddress host)
    {
        await DiscoverViaAsync(host, IPAddress.Broadcast, "broadcast");
        await DiscoverViaAsync(host, host, "unicast");
    }

    private static async Task DiscoverViaAsync(IPAddress host, IPAddress destination, string mode)
    {
        Write($"[UDP/60059 {mode}] 仅发送 cmd=ping\\n 至 {destination}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            await udp.SendAsync(Rc3040sDiscovery.CreatePing(),
                new IPEndPoint(destination, Rc3040sDiscovery.DiscoveryPort));
            int replies = 0;
            while (!timeout.IsCancellationRequested && replies < 8)
            {
                UdpReceiveResult response;
                try { response = await udp.ReceiveAsync(timeout.Token); }
                catch (OperationCanceledException) { break; }
                if (!response.RemoteEndPoint.Address.Equals(host)) continue;
                replies++;
                bool parsed = Rc3040sDiscovery.TryParsePong(
                    response.Buffer, response.RemoteEndPoint.Address, out var rotor);
                string raw = Rc3040sFirmwareBanner.RedactDeviceIds(Encoding.ASCII.GetString(response.Buffer));
                // Don't inadvertently copy the full seven-byte device ID into logs.
                raw = Regex.Replace(raw, @"(?i)(rotorid|id)\s*=\s*([0-9a-f]{14})",
                    match => match.Groups[1].Value + "=**********" + match.Groups[2].Value[^4..]);
                Write(parsed && rotor is not null
                    ? $"[UDP/60059 {mode}] PONG {response.RemoteEndPoint}; TCP={rotor.LocalTcpPort}; RotorID=**********{rotor.RotorId[^4..]}"
                    : $"[UDP/60059 {mode}] RAW {response.RemoteEndPoint}, {response.Buffer.Length}B: {EscapeAndLimit(raw, 200)}");
            }
            if (replies == 0)
                Write($"[UDP/60059 {mode}] 2 秒内无所选 IP 回复（不能说明 Socket7 NRL2 不可用）");
        }
        catch (Exception ex)
        {
            Write($"[UDP/60059 {mode}] ERROR: {Compact(ex)}");
        }
    }

    private static string EscapeAndLimit(string value, int max)
    {
        string safe = value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal);
        return safe.Length <= max ? safe : safe[..max] + "...(truncated)";
    }

    private static string Compact(Exception ex) =>
        EscapeAndLimit(ex.GetType().Name + ": " + ex.Message, 300);

    private static string? SaveReport()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string filename = $"RC3040S_Diagnostic_{timestamp}.txt";
        string[] destinations = [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath()
        ];
        foreach (string folder in destinations)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                string path = Path.Combine(folder, filename);
                File.WriteAllLines(path, ReportLines, new UTF8Encoding(false));
                return path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static void Write(string value)
    {
        Console.WriteLine(value);
        ReportLines.Add(value);
    }

    private static void EndInteractive()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine("按回车退出...");
        Console.ReadLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("RC3040S-Diagnostic.exe [--host <IPv4> [--read-only-query]]");
        Console.WriteLine("双击并输入 IP；可选只读 C2 查询。默认仅检查 TCP/WebSocket 握手与 UDP 广播+单播发现。");
        Console.WriteLine("任何模式均不发送电机运动、速度设置或 EEPROM 写入。");
    }
}
