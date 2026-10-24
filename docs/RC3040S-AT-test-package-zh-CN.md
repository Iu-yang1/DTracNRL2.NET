# RC-3040S TCP/60030 WebSocket AT 测试包

适用 Windows 10/11 x64。自包含单文件，无需安装 .NET、Git、Visual Studio、Python。

## 运行

1. 在本仓库 GitHub Actions 的最新成功 CI 页面下载 `RC3040S-AT-Tester-Windows-x64` 产物。
2. 解压后双击 `RC3040S-AT-Tester.exe`。
3. 输入控制板 IP（之前的设备为 `192.168.1.9`）。首先自动执行**只读**的 WebSocket/60030 `C2\r` 配置快照。
4. 默认选择 **0**，只读诊断结束并输出桌面的 `RC3040S_AT_Test_*.txt` 文件；发回分析即可。
5. 如果要主动验证 AT 命令，需要在界面上显式选择测试操作，并输入**两次文字确认**；每次运行最多尝试一次 AT 操作，不重试。

## 可选实验操作（默认绝不执行）

| 菜单 | 实际发送 | 风险 / 说明 |
|---|---|---|
| 0 | 仅 `C2\r` | 默认，固件和 NET SETUP 只读 |
| 1 | `AT+TEST=<ASCII>\r` | 实验回显（251217 固件静态分析；260223_C 未验证），实际效果不确定 |
| 2 | `AT+AZ_SPD=xxxxx\r` | 方位轴速度：可能即时改变电机状态 / EEPROM |
| 3 | `AT+EL_SPD=xxxxx\r` | 俯仰轴速度：可能即时改变电机状态 / EEPROM |
| 4 | `AT+RESET\r` | 高风险重启，可能断联和改变运动状态 |

**不要直接把示例数值 00300 当作安全值测试。** 速度 `1..65535` 仅为十六位无溢出的编码范围，未标定单位，也不是机械安全边界。菜单要求操作者提供先前从原厂界面核实的**当前原始速度值**和准备写入值、两次确认，并提示断开电机功率、保留控制板供电及紧急停止能力。如无当前原始值与恢复办法，应先使用只读模式，不测试速度写入。

原始 `AT+INIT`、未知 ACC/DEC/JERK、原生 DEMO、自定义任意 AT 文本均不向用户开放。测试包**不会向 TCP/60020 发运动命令，也不经 UDP 中继**。

## 重要验证边界

- **已由先前真实设备测试验证**：60030 WebSocket 握手、`C2` 配置转储、UDP/60059 子网发现、60020 GS-232 角度查询。
- **未完成真实设备验证**：260223_C 对 `AT+TEST`、AZ/EL 速度、`AT+RESET` 的执行和确认语义。
- `WebSocketSendCompleted=true` 仅表示 .NET WebSocket 已完成网络发送，不等于固件已执行。报告中的 `DeviceAcknowledgementVerified`、`PersistentSettingVerified` 和 `ReadbackAvailable` 在没有证据时均为 false。
- 操作前新的 WebSocket 会话会发 `C2` 做只读预检查，固件与之前读回的版本不一致则**拒绝 AT 写入**，不会自动重试。
- 分享 TXT 前请检查所有信息。软件将隐藏设备 ID、MAC 和若干可能涉及隐私的 AT 设置，但 IP 地址等网络配置仍会显示。

代码、构建和模拟测试都无法代替真机安全验证。建议第一次验证只进行 **0：只读**，如需进一步主动测试，可优先在电机供电隔离的实验环境中测试菜单 **1：诊断回显**，之后再决定是否验证速度和复位。

## 构建者

CI 在 Windows runner 执行：

`dotnet publish src/DTrac.Nrl2.AtTester/DTrac.Nrl2.AtTester.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist-at`

输出 `RC3040S-AT-Tester.exe`、`README-AT-TEST-ZH-CN.md`，自动验证 `--self-test` 在**无网络**条件下完成。
