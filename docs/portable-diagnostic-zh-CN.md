# RC-3040S 免安装 Windows 诊断程序

适用 Windows 10/11 x64。**不需要安装 Git、Python、Visual Studio、.NET SDK 或 .NET Runtime**，运行所需依赖已经打包到 EXE。

## 下载和操作（无需任何命令）

1. 打开 [DTracNRL2.NET 的 Actions](https://github.com/Iu-yang1/DTracNRL2.NET/actions/workflows/ci.yml)，选择最新成功的 `.NET SDK CI`。
2. 页面底部的 **Artifacts** 选择 `RC3040S-Diagnostic-Windows-x64`，下载 ZIP 并解压。
3. 双击 `RC3040S-Diagnostic.exe`，输入云台 IP，例如 `192.168.1.9`。
4. 程序显示测试结果并将 `RC3040S_Diagnostic_日期_时间.txt` 保存到桌面；请把这个 TXT 发回来即可。

## 检查什么

- TCP/60020：仅尝试建立并关闭一次 TCP 连接，**不会发送 `C2` 或运动命令**。
- TCP/60030：同上。
- WebSocket/60030：进行标准 HTTP Upgrade，并尝试被动读取一条设备主动消息；**不会发送 AT 配置或运动指令**。
- UDP/60059：向广播地址发送一次固定的 `cmd=ping` 设备发现报文，并等待两秒。该报文不是云台运动命令。

程序不会自动执行速度设置、PARK、DEMO、复位、`AT+INIT` 或 EEPROM 写入，也不会配置网络服务器地址。**建议测试前暂停 SkyRoof 的自动跟踪**，避免与固件有限的 TCP 连接容量竞争。

如果没收到 UDP 应答，不代表云台没有 UDP 功能；如果 TCP/60020 偶发连接失败，也不否定 SkyRoof 实际已成功控制的证据。

## 开发人员

也可以使用 `RC3040S-Diagnostic.exe --host 192.168.1.9` 跳过交互输入，或 `--self-test` 执行无网络的最小自检。

这个工具面向可信局域网；诊断日志可能包含局域网 IP 和设备主动消息，请在公开分享前检查敏感信息。GitHub Actions 每次提交均编译自包含 Windows x64 单文件，保留 30 天。

## v2：针对实机验证成功的 WebSocket 进一步测试

2026-10-24 实机日志确认 `192.168.1.9:60030` 的 RFC 6455 WebSocket 握手成功，设备在 2 秒内无主动文本。UDP/60059 广播发现未响应，但与 NRL2 Socket7 UDP 可用性无直接等价关系。

- 双击后输入设备 IP，再根据屏幕提示选择是否发送 **只读的 GS-232 `C2\r` 角度查询**。默认 N，不发送任何 C2。
- 选择 Y 时，分别向 TCP/60020 和 WebSocket/60030 发出一次 C2，并记录回复或超时。C2 不属于运动命令，但测试时仍建议暂停 SkyRoof 跟踪。
- 无论是否选择 Y，UDP/60059 都会先执行一次广播发现，再对指定控制板 IP 执行一次定向单播发现；这两个请求都只发送固定的 `cmd=ping\n`，并不发送 NRL2 运动封装。
- 如果收到未识别 UDP 数据报，将保留有限长度的原始 ASCII 响应用于格式分析，并遮蔽 `rotorid=` / `id=` 后面的七字节设备 ID。
- **未收到回应不是失败的充分证据**。有些固件不主动发送 WebSocket welcome，或者发现报文仅在特定网口、端口或时间返回。

也可用命令行 `RC3040S-Diagnostic.exe --host 192.168.1.9 --read-only-query` 显式开启 C2 只读查询。无需安装任何开发环境。

## v3：与固件 260223_C 的实机响应兼容

2026-10-24 操作者已证实 TCP/60020 `C2` 返回 AZ/EL 角度、WebSocket/60030 握手成功且在 `C2` 后收到版本标识 `RC_3040S_67S_8H_260223_C`、UDP/60059 **单播**返回包含 `192.168.001.009` 的 75 字节 `cmd=pong`。

v3 修复了十进制补零 IPv4 的 PONG 解析；原先显示为 `RAW` 的合法 PONG 应可正常显示解析后的 TCP 端口及脱敏 RotorID。WebSocket 固件标识会单独显示，不再误称为角度回复；不会在日志里暴露完整 14 位十六进制设备 ID。

无需重测已通过的 TCP 连通性。只有需要验证更新后的发现解析时，才下载新 EXE；继续使用双击、输入 IP、可选 Y 启用只读 C2 的流程。
