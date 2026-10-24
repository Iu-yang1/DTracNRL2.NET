# DTrac NRL2 协议及安全说明

这是基于 DTrac APK 与 RC-3040S 固件离线静态分析的**非官方互操作实现**，不能泛化成所有 NRL2 系统。

## UDP 应用数据报结构

| 偏移 | 长度 | 含义 |
| ---: | ---: | --- |
| 0 | 4 | ASCII `NRL2` |
| 4 | 2 | 两个 ASCII 十进制长度字节，表示帧总长 |
| 6 | 7 | APP SID，二进制 |
| 13 | 7 | RotorID，二进制 |
| 20 | 1 | Type `0x05`，GS-232 |
| 21 | 3 | 零 |
| 24 | 5 | `DTrac` |
| 29 | 19 | 保留零填充 |
| 48 起 | 可变 | GS-232 ASCII 命令 |

`C2\r\n` 长 52B，`W181 030\r\n` 长 58B，`S\r\n` 长 51B。W5500 RX 缓冲前面另有 8B UDP 元信息，因此固件内 Type 位于 +28，GS-232 载荷位于 +56。

## 网络端口区别

- UDP/60059：只用于局域网 `cmd=ping\n` 发现、`cmd=pong` 应答。
- TCP/60020：本地 GS-232 控制路径。
- W5500 Socket 7：经设备配置地址/端口连接 DTrac NRL2 UDP 远端中继，**不能使用 60059 取代远端中继**。

## 功能与证据边界

已从固件代码定位 Type 5 解析到 GS-232 的控制链。角度命令、STOP、连续方向命令的封装与 APK 样本一致。自动加减速由 RC-3040S 固件执行，本库不控制电机 PWM 曲线。PARK 为单次 W 定位，不是机械回零；DEMO 为宿主程序根据及时反馈逐点控制，不是已验证的设备内建巡航。

公网 DTrac 中继的注册、保活、路由及鉴权行为尚未实测；开源 `hicaoc/nrllink` 不可直接视为兼容服务端。实验性 `AT+RESET\r\n` 不会由本库自动发送，远端能否接受亦待核验。**网络发包成功不代表机械动作成功或已经安全停机。** 请保留真实硬件限位、物理急停，先在断电机功率环境做只读测试。


## 推荐：内网直连，不需要私有中继

本 SDK 提供 `Rc3040sLanClient.ConnectAsync("控制板局域网IP", 60020)`，
与 RC-3040S 固件 TCP Socket 3 的 GS-232 解析器通信，不封装 NRL2，不需要 APP SID、RotorID 或 DTrac 公网服务器。

使用 UDP/60059 仅发现控制板；通过 `DiscoveredRotor.Address` 及 `LocalTcpPort`
建立 TCP 直连（建议先只读调用 `QueryPositionAsync`）。固件静态分析显示
`C2\r` 返回预期 `AZ=ddd  EL=ddd\r\n`，未完成实机验收。

- `ConnectAsync` 不会自动运动；
- `QueryPositionAsync` 只执行 C2 只读查询，超时返回 null；
- `MoveToAsync`、`JogAsync`、`StopAsync` 必须显式调用；
- TCP 写入成功不等于机械动作确认；STOP 亦不可替代物理急停；
- 本地 TCP 不提供鉴权，建议在可信 LAN/VLAN 内使用，勿将 60020 直接暴露公网。

如明确需要兼容官方 DTrac APP 的 **NRL2 UDP**，才需研究局域网自建中继，
不能把 NRL2 Type 5 报文直接发往设备的 UDP 60059 发现端口。
