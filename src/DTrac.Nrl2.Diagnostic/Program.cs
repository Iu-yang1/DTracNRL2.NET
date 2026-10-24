using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using DTrac.Nrl2.Discovery;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Relay;
using DTrac.Nrl2.Rotator;
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

        Write("RC-3040S 局域网只读诊断 / LAN diagnostics v5 — 多帧配置读取");
        Write("无需 Git、Python、Visual Studio 或 .NET SDK。");
        Write("不会发送旋转、速度设置、复位或 EEPROM 写入命令。");
        Write("建议先暂停 SkyRoof 自动跟踪，避免竞争 TCP 连接。");
        Write("");

        bool knownHostArguments = args.Length is 2 or 3 && args[0] == "--host" &&
            (args.Length == 2 || args[2] == "--read-only-query");
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

        Write($"目标控制板 IP：{host}");
        Write($"开始时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Write("本次执行全部非破坏性协议测试；不再需要逐项确认。");
        Write("仅发送 GS-232 C2 一次、WebSocket C2 一次、UDP 全网广播/子网广播/单播各一次。");
        Write("");

        var overall = Stopwatch.StartNew();
        await CheckTcpAsync(host, 60020, "GS-232");
        await CheckTcpAsync(host, 60030, "WebSocket 管理");
        await CheckGs232PositionAsync(host);
        await CheckWebSocketFullAsync(host);
        await CheckDiscoveryAsync(host);
        await CheckPassiveSocket7Async();
        overall.Stop();
        Write($"执行耗时：{overall.Elapsed.TotalSeconds:F1} 秒");
        Write("已测范围：TCP、GS-232位置查询、WebSocket会话时序、UDP发现、Socket7被动监听。");
        Write("不可只读验证：EEPROM配置写入、原生DEMO、自动加减速和电机限位。");
        Write("");
        Write("诊断已完成；TCP 成功不等于设备响应或运动控制成功。");
        Write("无需再重复基础端口测试；未知项目在日志中明确标记。");
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

    private static async Task CheckWebSocketFullAsync(IPAddress host)
    {
        Write("[WebSocket/60030] 同会话：握手 → 5 秒无发送观察 → 一次只读 C2 → 整批多帧收集");
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var elapsed = Stopwatch.StartNew();
            await using var ws = await Rc3040sWebSocketClient.ConnectAsync(
                host.ToString(), 60030, total.Token);
            Write($"[WebSocket/60030] RFC6455 HANDSHAKE OK (+{elapsed.ElapsedMilliseconds}ms)");
            string? unsolicited = await ws.ReceiveTextAsync(TimeSpan.FromSeconds(5), total.Token);
            if (unsolicited is not null)
                Write($"[WebSocket/60030] 无查询时收到消息: {RedactConfiguration(unsolicited, 300)}");
            else
                Write("[WebSocket/60030] 5秒内未观察到主动消息");

            // One C2 request only. A firmware dump may arrive as 4+ WS messages.
            var dump = await Rc3040sWebSocketCaptures.CaptureReadOnlyC2Async(
                ws, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(700),
                TimeSpan.FromSeconds(8), total.Token);
            Write($"[WebSocket/60030] C2 只发送一次; 收到 {dump.Frames.Count} 条文本消息; " +
                  $"完成原因={(dump.LimitsReached ? "安全限额" : dump.QuietGapObserved ? "后续700ms无消息" : "超时或关闭")}; " +
                  $"收集耗时={dump.Duration.TotalMilliseconds:F0}ms");

            for (int i = 0; i < dump.Frames.Count; i++)
                Write($"[WebSocket/60030] FRAME {i+1}/{dump.Frames.Count}: " +
                    RedactConfiguration(dump.Frames[i], 650));

            var parsed = Rc3040sConfigurationDump.Parse(dump.Frames);
            Write($"[WebSocket/60030] 识别：固件={parsed.FirmwareVersion ?? "未知"}，" +
                $"字段={parsed.RecognizedFieldCount}，NET区段={parsed.SawNetworkSection}，" +
                $"UDP目标区段={parsed.SawUdpDestinationSection}");
            Write($"[WebSocket/60030] IP={parsed.ConfiguredIp?.ToString() ?? "未知"}，" +
                $"网关={parsed.ConfiguredGateway?.ToString() ?? "未知"}，" +
                $"掩码={parsed.ConfiguredMask?.ToString() ?? "未知"}");
            Write($"[WebSocket/60030] DNS={parsed.ConfiguredDns?.ToString() ?? "未知"}，" +
                $"DHCP={(parsed.DhcpEnabled.HasValue ? (parsed.DhcpEnabled.Value ? "ON" : "OFF") : "未知")}，" +
                $"MAC={parsed.MaskedPhysicalAddress ?? "未知"}");
            Write($"[WebSocket/60030] UDP远端目标={parsed.ConfiguredUdpDestination?.ToString() ?? "未知"} " +
                "(仅配置读回，未证明当前 UDP 会话已建立)");
            Write("[WebSocket/60030] 注意：响应收集以空闲间隔判断，不是固件显式事务结束/ACK");
            Write($"[WebSocket/60030] 会话结束前状态：{ws.State}");
        }
        catch (Exception ex)
        {
            Write("[WebSocket/60030] ERROR: " + Compact(ex));
            if (ex.InnerException is not null)
                Write("[WebSocket/60030] INNER: " + Compact(ex.InnerException));
        }
    }

    private static string RedactConfiguration(string message, int max)
    {
        string redacted = Rc3040sFirmwareBanner.RedactDeviceIds(message);
        redacted = Regex.Replace(redacted, @"(?i)\b(?:[0-9A-F]{2}:){5}[0-9A-F]{2}\b",
            m => "**:**:**:**:" + m.Value[^5..]);
        redacted = Regex.Replace(redacted,
            @"(?im)(AT\+(?:CALL|SSID|PASS|PASSWORD|KEY)\s*=)[^\r\n]*",
            "$1[REDACTED]");
        return EscapeAndLimit(redacted, max);
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

    private static async Task CheckDiscoveryAsync(IPAddress host)
    {
        await DiscoverViaAsync(host, IPAddress.Broadcast, "global-broadcast");
        IPAddress? directed = TryFindDirectedBroadcast(host);
        if (directed is not null && !directed.Equals(IPAddress.Broadcast) && !directed.Equals(host))
            await DiscoverViaAsync(host, directed, "subnet-broadcast");
        await DiscoverViaAsync(host, host, "unicast");
    }

    /// <summary>
    /// Finds the broadcast address only for an active IPv4 interface on the
    /// same subnet as the selected controller, without guessing /24 masks.
    /// Does not change any adapter or system setting.
    /// </summary>
    private static IPAddress? TryFindDirectedBroadcast(IPAddress device)
    {
        try
        {
            byte[] target = device.GetAddressBytes();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask is null) continue;
                    byte[] local = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    bool sameSubnet = true;
                    for (int i = 0; i < 4; i++)
                        if ((local[i] & mask[i]) != (target[i] & mask[i]))
                            sameSubnet = false;
                    if (!sameSubnet) continue;
                    byte[] broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcast[i] = (byte)((local[i] & mask[i]) | (255 ^ mask[i]));
                    var address = new IPAddress(broadcast);
                    Write($"[NET] 匹配本机接口 {unicast.Address}/{unicast.IPv4Mask}; 子网广播 {address}");
                    return address;
                }
            }
        }
        catch (NetworkInformationException ex) { Write("[NET] 接口枚举失败: " + Compact(ex)); }
        catch (SocketException ex) { Write("[NET] 接口枚举失败: " + Compact(ex)); }
        Write("[NET] 没有找到同一子网的 IPv4 接口，跳过子网广播");
        return null;
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

    private static async Task CheckPassiveSocket7Async()
    {
        Write("[NRL2 UDP/60050] 被动监听4秒：不发包、不修改控制板远端地址");
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        int totalFrames = 0;
        try
        {
            using var monitor = new Nrl2PassiveMonitor(new IPEndPoint(IPAddress.Any, 60050));
            monitor.FrameObserved += value =>
            {
                totalFrames++;
                if (totalFrames <= 12)
                    Write($"[NRL2 UDP/60050] {value.Sender} Type={value.MessageType} " +
                        $"编码={value.Encoding} 总长={value.FrameBytes} 载荷={value.PayloadBytes} " +
                        $"源ID={value.MaskedSourceId} 目标ID={value.MaskedTargetId}");
            };
            await monitor.RunAsync(timer.Token);
            Write(totalFrames == 0
                ? "[NRL2 UDP/60050] 未收到报文。若设备未配置指向本机，这是预期结果。"
                : $"[NRL2 UDP/60050] 共观察到 {totalFrames} 个有效帧");
        }
        catch (SocketException ex)
        {
            Write($"[NRL2 UDP/60050] 无法绑定端口：{ex.SocketErrorCode}。可能被其他程序占用。");
        }
        catch (Exception ex)
        {
            Write("[NRL2 UDP/60050] ERROR: " + Compact(ex));
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
        Console.WriteLine("只需输入 IP：自动检查 TCP/GS232、单次 WebSocket C2 完整配置转储、UDP 发现和被动监听。");
        Console.WriteLine("任何模式均不发送电机运动、速度设置或 EEPROM 写入。");
    }
}
