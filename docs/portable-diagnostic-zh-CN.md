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
