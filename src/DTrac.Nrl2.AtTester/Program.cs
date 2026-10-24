using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DTrac.Nrl2.Discovery;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Transport;

// Standalone radio-PC test package, no developer tools or runtime required.
// Only a C2 config read is automatic. Every AT operation is opt-in and one-shot.
internal static class Program
{
    private static readonly List<string> Report = new();
    private static readonly Regex SafeEcho = new(@"^[A-Za-z0-9_-]{1,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string PackageVersion = "1.0.1-at-connect-diagnostics";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Title = "RC-3040S WebSocket AT 功能测试";

        if (args is ["--self-test"])
        {
            if (!SafeEcho.IsMatch("TEST_01") || SafeEcho.IsMatch("unsafe\r\nAT+RESET") ||
                ParseRawSpeed("00300") != 300 || ParseRawSpeed("00000") is not null ||
                !IPAddress.TryParse("192.168.1.9", out _))
                return 1;
            Console.WriteLine("SELF-TEST PASS; no network connection or device commands.");
            return 0;
        }
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("RC3040S-AT-Tester.exe [--host <IPv4>] | --self-test");
            return 0;
        }

        Write("RC-3040S TCP/60030 WebSocket 原生 AT 测试 / " + PackageVersion);
        Write("默认只读取配置 C2；AT+TEST、速度设置、重启均需独立手动确认。");
        Write("不会使用 TCP/60020 发运动命令，也不经公网 UDP 中继。");
        Write("请暂停 SkyRoof 自动跟踪，保持机械限位及紧急停止手段可用。");
        Write("注意：251217 固件静态分析并不证明 260223_C 的 AT 写入行为。");
        Write("");

        IPAddress? host;
        if (args.Length == 0)
        {
            Console.Write("输入控制板 IPv4（例如 192.168.1.9）：");
            if (!TryParseLocalIpv4(Console.ReadLine()?.Trim(), out host))
            {
                Write("IP 地址无效，未连接设备。");
                SaveAndExit();
                return 2;
            }
        }
        else if (args.Length == 2 && args[0] == "--host" &&
                 TryParseLocalIpv4(args[1], out host))
        {
            // Accepted only an explicit literal IPv4: never connect to hostname or URI.
        }
        else
        {
            Write("参数无效，仅支持 --host <IPv4> / --self-test。");
            SaveAndExit();
            return 2;
        }

        Write($"目标：{host}:60030 (WebSocket) | 本地时间 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Write("[1] 只读配置检查（自动运行，不发送 AT）");
        Rc3040sConfigurationSnapshot? config = null;
        bool preflightHadConnectionFailure = false;
        // Only the READ-ONLY preflight may try again. Never replay an AT write.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                Write($"[WS/60030] 尝试 {attempt}/2：建立 WebSocket 握手...");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
                var clock = Stopwatch.StartNew();
                await using var client = await Rc3040sAtManagementClient.ConnectAsync(
                    host!.ToString(), cancellationToken: cts.Token);
                Write($"[WS/60030] 握手成功 (+{clock.ElapsedMilliseconds}ms)");
                Write("[WS/60030] 开始发送一次只读 C2，收集多帧配置...");
                config = await client.ReadConfigurationAsync(cts.Token);
                Write($"[WS/60030] 完成配置读取 (+{clock.ElapsedMilliseconds}ms)");
                Write($"固件版本：{config.FirmwareVersion ?? "未识别"}");
                Write($"管理快照：IP={config.ConfiguredIp?.ToString() ?? "未知"}, " +
                      $"Gateway={config.ConfiguredGateway?.ToString() ?? "未知"}, " +
                      $"Mask={config.ConfiguredMask?.ToString() ?? "未知"}");
                Write($"DNS={config.ConfiguredDns?.ToString() ?? "未知"}, " +
                      $"DHCP={(config.DhcpEnabled is true ? "ON" : config.DhcpEnabled is false ? "OFF" : "未知")}, " +
                      $"UDP目的={config.ConfiguredUdpDestination?.ToString() ?? "未知"}");
                Write($"物理地址（已遮蔽）={config.MaskedPhysicalAddress ?? "未知"}，解析字段={config.RecognizedFieldCount}");
                break;
            }
            catch (Exception error)
            {
                preflightHadConnectionFailure = true;
                Write($"[WS/60030] 第 {attempt} 次只读检查失败，完整异常链：");
                WriteExceptionChain(error);
                if (attempt == 1)
                {
                    await InspectLanConnectivityAsync(host!);
                    Write("[WS/60030] 仅只读路径延迟 2.5 秒后再次尝试一次；AT 写操作本轮始终禁用。");
                    await Task.Delay(TimeSpan.FromMilliseconds(2500));
                }
            }
        }
        if (config is null)
        {
            Write("两次只读预检查都未成功；无任何 AT 命令发送。");
            SaveAndExit();
            return 3;
        }
        if (preflightHadConnectionFailure)
        {
            Write("只读检查虽已恢复，但此前发生过连接故障。本轮所有 AT 写操作锁定。");
            Write("请把日志发回，用于定位设备连接资源、代理或网络握手问题。");
            SaveAndExit();
            return 0;
        }

        if (config?.FirmwareVersion is null ||
            !config.SawNetworkSection || config.RecognizedFieldCount < 3)
        {
            Write("固件标识或配置字段不完整；AT 写入入口已锁定。");
            SaveAndExit();
            return 4;
        }

        // Never default to any write. An operator must manually choose a specific test.
        Console.WriteLine("");
        Console.WriteLine("后续测试（仅选一项，执行后退出）：");
        Console.WriteLine("  [0] 完成只读测试并保存报告（推荐）");
        Console.WriteLine("  [1] AT+TEST= 诊断回显（实验，仍需授权）");
        Console.WriteLine("  [2] AZ 原始速度设置（可能写入 EEPROM / 改变电机）");
        Console.WriteLine("  [3] EL 原始速度设置（可能写入 EEPROM / 改变电机）");
        Console.WriteLine("  [4] AT+RESET 设备重启（可能断连）");
        Console.Write("选择 [0]：");
        string selection = Console.ReadLine()?.Trim() ?? "";
        if (selection.Length == 0 || selection == "0")
        {
            Write("操作员选择只读测试。没有发出任何 AT 设置指令。");
            SaveAndExit();
            return 0;
        }
        if (selection is not ("1" or "2" or "3" or "4"))
        {
            Write("未知选择。未发送 AT 指令。");
            SaveAndExit();
            return 2;
        }

        Rc3040sAtWritePermit permit;
        string operation;
        string firmware = config.FirmwareVersion;
        switch (selection)
        {
            case "1":
                Write("AT+TEST= 的效果仅依据旧固件静态分析推测为回显；新固件未确认。");
                Console.Write("测试文本（仅英文数字、下划线、短横线，1~32字符；默认 TEST_01）：");
                string payload = Console.ReadLine()?.Trim() ?? "";
                if (payload.Length == 0) payload = "TEST_01";
                if (!SafeEcho.IsMatch(payload))
                {
                    Write("测试文本格式不合法。未发送 AT。");
                    SaveAndExit();
                    return 2;
                }
                operation = "AT+TEST=" + payload;
                if (!Authorize("ECHO", firmware, false))
                {
                    Write("未完成双重授权，未发送 AT。");
                    SaveAndExit();
                    return 0;
                }
                permit = Rc3040sAtWritePermit.ForDiagnosticEcho(payload, firmware, true, true);
                break;
            case "2":
            case "3":
                bool az = selection == "2";
                string axis = az ? "AZ" : "EL";
                Write($"警告：{axis} 原始速度未知，可能立即改变步进频率或持久写入 EEPROM。");
                Write("请先通过原厂工具核实此轴当前原始数值及可恢复方式。");
                Console.Write($"输入经原厂界面核对的当前 {axis} 原始速度（1~65535）：");
                ushort? before = ParseRawSpeed(Console.ReadLine());
                Console.Write($"输入准备写入的 {axis} 原始速度（1~65535）：");
                ushort? after = ParseRawSpeed(Console.ReadLine());
                if (before is null || after is null || before == after)
                {
                    Write("原值或新值无效/相同，未发送 AT。");
                    SaveAndExit();
                    return 2;
                }
                Write($"当前值（操作者提供）={before}; 目标值={after}。程序无法独立验证原值。");
                Write("建议断开电机功率、保留控制板供电，并准备现场恢复手段。");
                operation = $"AT+{axis}_SPD={after.Value:00000}";
                if (!Authorize($"SET-{axis}-SPEED", firmware, true))
                {
                    Write("未完成双重授权，未发送 AT。");
                    SaveAndExit();
                    return 0;
                }
                permit = Rc3040sAtWritePermit.ForSpeed(az, after.Value, firmware, true, true);
                break;
            default:
                operation = "AT+RESET";
                Write("警告：设备可能立刻断线、重新初始化；本测试无法确认安全停机。");
                if (!Authorize("RESTART", firmware, true))
                {
                    Write("未完成双重授权，未发送 AT。");
                    SaveAndExit();
                    return 0;
                }
                permit = Rc3040sAtWritePermit.ForRestart(firmware, true, true);
                break;
        }

        Write($"已授权一次操作：{operation}；实际发送前将再次读取固件版本。");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var writer = await Rc3040sAtManagementClient.ConnectAsync(
                host!.ToString(), cancellationToken: cts.Token);
            var result = await writer.SendApprovedAsync(permit, cts.Token);
            Write($"WebSocketSendCompleted={result.WebSocketSendCompleted}");
            Write($"固件版本={result.FirmwareVersion}");
            Write($"DeviceAcknowledgementVerified={result.DeviceAcknowledgementVerified}");
            Write($"PersistentSettingVerified={result.PersistentSettingVerified}");
            Write($"ReadbackAvailable={result.ReadbackAvailable}");
            if (result.ObservedMessages.Count == 0)
                Write("命令后未观察到文本帧；此结果并不意味着执行成功或失败。");
            for (int i = 0; i < result.ObservedMessages.Count; i++)
                Write($"[响应 {i+1}] {Redact(result.ObservedMessages[i])}");
            Write("注意：WebSocket 发送完成不等于设备执行成功，不能自动重试。");
        }
        catch (Exception e)
        {
            Write("AT 操作未能完成或结果不明确：" + SafeException(e));
            Write("无法确认命令是否已被设备接收。为避免重复写入，本程序不会重试。");
        }

        SaveAndExit();
        return 0;
    }

    private static async Task InspectLanConnectivityAsync(IPAddress device)
    {
        Write("[诊断] 仅在 WebSocket 出错后检查 TCP 会话状态与端口。");
        try
        {
            var existing = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .Where(x => x.RemoteEndPoint.Address.Equals(device) &&
                    x.RemoteEndPoint.Port is 60020 or 60030)
                .Take(16).ToArray();
            if (existing.Length == 0)
                Write("[诊断] 本机未枚举到指向该设备 60020/60030 的活动 TCP 会话。");
            foreach (var item in existing)
                Write($"[诊断] TCP {item.LocalEndPoint.Port} -> {item.RemoteEndPoint.Port} " +
                      $"状态={item.State}");
        }
        catch (Exception ex) when (ex is NetworkInformationException
            or SocketException or UnauthorizedAccessException)
        {
            Write("[诊断] 枚举本机 TCP 会话失败：" + SafeException(ex));
        }

        foreach (int port in new[] { 60030, 60020 })
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
            var watch = Stopwatch.StartNew();
            try
            {
                await tcp.ConnectAsync(device, port, limit.Token);
                Write($"[TCP/{port}] CONNECTED; +{watch.ElapsedMilliseconds}ms (仅建立并关闭连接，不发命令)");
            }
            catch (OperationCanceledException)
            {
                Write($"[TCP/{port}] TIMEOUT; +{watch.ElapsedMilliseconds}ms");
            }
            catch (SocketException e)
            {
                Write($"[TCP/{port}] SOCKET_FAIL={e.SocketErrorCode}, native={e.NativeErrorCode}; +{watch.ElapsedMilliseconds}ms");
            }
            catch (Exception e)
            {
                Write($"[TCP/{port}] ERROR: {SafeException(e)}");
            }
        }
        Write("[诊断] 注：TCP 可连不代表 WebSocket HTTP Upgrade 成功。");
    }

    private static void WriteExceptionChain(Exception error)
    {
        Exception? current = error;
        for (int depth = 0; depth < 6 && current is not null; depth++)
        {
            Write($"  cause[{depth}] {SafeException(current)}");
            if (current is SocketException sock)
                Write($"  SocketError={sock.SocketErrorCode}; NativeError={sock.NativeErrorCode}");
            if (current is WebSocketException websocket)
                Write($"  WebSocketError={websocket.WebSocketErrorCode}; NativeError={websocket.NativeErrorCode}");
            if (current is HttpRequestException http)
                Write($"  HttpRequestError={http.HttpRequestError}; HttpStatus={http.StatusCode}");
            current = current.InnerException;
        }
    }

    private static bool Authorize(string phrase, string firmware, bool highRisk)
    {
        if (highRisk)
            Console.WriteLine("硬件风险：此操作可能立即生效、持久保存，或导致控制板失联。");
        Console.Write($"第一项确认：手工完整输入固件版本 {firmware}：");
        if (Console.ReadLine()?.Trim() != firmware) return false;
        Console.Write($"第二项确认：手工输入 {phrase} 表示承担实机操作风险：");
        return string.Equals(Console.ReadLine()?.Trim(), phrase, StringComparison.Ordinal);
    }

    private static ushort? ParseRawSpeed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(c => c is < '0' or > '9') ||
            !ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ushort raw) ||
            raw == 0)
            return null;
        return raw;
    }

    private static bool TryParseLocalIpv4(string? value, out IPAddress? address)
    {
        address = null;
        if (!Rc3040sDiscovery.TryParseDecimalIpv4(value, out IPAddress candidate))
            return false;
        if (candidate.Equals(IPAddress.Any) || candidate.Equals(IPAddress.Broadcast) ||
            IPAddress.IsLoopback(candidate))
            return false;
        address = candidate;
        return true;
    }

    private static string Redact(string text)
    {
        text = Rc3040sFirmwareBanner.RedactDeviceIds(text);
        text = Regex.Replace(text, @"(?i)\b(?:[0-9a-f]{2}:){5}[0-9a-f]{2}\b",
            m => "**:**:**:**:" + m.Value[^5..]);
        text = Regex.Replace(text, @"(?im)(AT\+(?:CALL|SSID|PASS|PASSWORD|KEY)\s*=)[^\r\n]*",
            "$1[REDACTED]");
        text = text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return text.Length > 1000 ? text[..1000] + "...(truncated)" : text;
    }

    private static string SafeException(Exception ex)
    {
        string message = Redact(ex.GetType().Name + ": " + ex.Message);
        return message.Length > 300 ? message[..300] : message;
    }

    private static void Write(string value)
    {
        Console.WriteLine(value);
        Report.Add(value);
    }

    private static void SaveAndExit()
    {
        string filename = "RC3040S_AT_Test_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt";
        foreach (var dir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath()
        })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                string file = Path.Combine(dir, filename);
                File.WriteAllLines(file, Report, new UTF8Encoding(false));
                Console.WriteLine("诊断日志：" + file);
                break;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("按回车键退出...");
            Console.ReadLine();
        }
    }
}
