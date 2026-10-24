# RC-3040S / DTrac NRL2 服务端转发与固件远程接收：第三阶段源码逆向

**分析日期：**2026-10-24  
**分析方式：**用户提供的 DTrac APK 与 STC8H 固件离线静态分析、GitHub 开源服务端只读源码核对；没有访问公网 NRL 服务器、没有给控制板发送任何运动命令。  
**样本：**`dtrac.apk` SHA-256 `120c43f5d09e307ecfd7b168676486c41984d28a2a47b83d19abae7adf04d23a`；8051 固件 `RC_3040S_67S_8H_251217_firmware_8051.bin` SHA-256 `59f5114c0289fb0f273aba50b5987688a27bf88e1034a577a38c46bb45ba019f`。

## 1. 总结

1. **已找到完整 MCU NRL 远程接收至 GS-232 的控制流**：W5500 Socket 7 收到 UDP → `0x7769` → `0x85A0` → `0x482F` → Type 5 → `0x4A3A` → `0x3433`（GS-232）。可从固件入口证实 Type 5 的数据载荷被提取并送进运动控制解析器。
2. MCU 上的 **Socket 2：UDP/60059** 实际上有独立的 `cmd=ping` 发现分支（`0x8E0A` → `0x4E62`）；**Socket 3：TCP/60020** 走 `0x8822` → `0x3433`。Socket 7 为单独的远端服务器 UDP 通道，配置支持 IP 或域名（`0x6935` 附近日志及 `0x8C6C` 目标地址和端口设置）。三者不能混淆。
3. **DTrac APK 用 Type 0x05 把原始 GS-232 放在 NRL2 偏移 +48**；MCU 对 W5500 RX 缓冲使用额外 8 字节 UDP RX 元信息，所以 Type 在 **+28=8+20**，控制指令在 **+56=8+48**。这与 `Lei0.n()` 发包对齐。
4. 当前公开的 `hicaoc/nrllink` 是**不兼容的 NRL 服务端实现**：其 `decodeNRL21()` 解码偏移 24–29 为 `CallSign`，而 DTrac 在此写 `DTrac`。其 `deviceDB.go::IsCallSign()` 只接受大写 `A-Z` / 数字 `0-9`，因此这份 APK 的报文**进入 Type 5 前就会收到 `callsign error` 丢弃**。还存在长度字段和设备标识语义不一致。
5. 即使修改呼号限制，开源 `udphub.go::forwardMsg` 的 Type 5 是按群组在线池广播到其他设备，**没有直接依据偏移 13–19 的 RotorID 做点对点查找**。这不是可以直接兼容的 RC-3040S 专用 NRL relay。
6. 目前未拿到 DTrac 正在使用的公网 NRL 转发服务端二进制/部署源码，也没有实机发包，所以 **公网服务器的实际注册/认证、对应识别码路由、掉线控制措施尚未验收**。

## 2. MCU Socket 拓扑（机器码证据）

| 固件位置 | 证据 | 可信度 |
|---|---|---|
| `0x915B–0x916A` | R7=2、端口 `0xEA9B`=60059，UDP 打开 `0x94BE` | 高 |
| `0x99AE–0x99B9` | 轮询 Socket 2 的 RX 并调用 `0x8E0A` | 高 |
| `0x4F20–0x4F31` | 匹配 `cmd=ping`，随后构造 `cmd=pong`；不匹配则跳 `0x5041` 返回 | 高 |
| `0x7CA8–0x7CB0` | R7=3、端口 `0xEA74`=60020，通过 `0x86E8` 初始化 TCP Socket 3 | 高 |
| `0x7CE6–0x7CF0`、`0x8822–0x885D` | TCP Socket 3 收到数据后调用 `0x3433` 解析 GS-232 | 高 |
| `0x6935–0x69E8` | Socket 7 服务目标按 IP/域名配置，打印 `S7 Use IP connection` / `S7 Use domain name to connect` / `Service IP` | 高 |
| `0x69C5–0x69C7`、`0x8C6C–0x8CA9` | 用 Socket 7 配置远端 IPv4 与远端 UDP 端口，并检查 UDP Socket 状态 `0x22` | 高 |
| `0x99C6–0x99D1` | Socket 7 RX 轮询 → `0x7769` | 高 |
| `0x7769–0x77E7` | Socket 7 网络读取，向 `0x85A0` 交付接收数据 | 高 |
| `0x85A0–0x85F2` | 调用 `0x482F` 迭代处理远端数据 | 高 |
| `0x482F–0x4A53` | 解析 UDP+NRL 数据；Type 5 分支将载荷传给 `0x3433` | 高 |

固件中没有连续 ASCII `NRL2` 字面量，不代表它没有 NRL2 处理：控制流按固定偏移读字段并做比较，而不需要直接保存该字面量。

## 3. 远程 UDP RX 二层视角：正确的缓冲区偏移

W5500 的 UDP RX 读取格式：

```
[0..3]    Source IPv4         (4B)
[4..5]    Source UDP port     (2B)
[6..7]    UDP payload length  (2B)
[8..]     UDP application datagram, here DTrac NRL2
```

APK 的 NRL2 控制报文：

```
[0..3]    ASCII 'NRL2'
[4..5]    DTrac APK's two ASCII decimal total-length chars
[6..12]   7B APP SID
[13..19]  7B RotorID
[20]      Type 0x05
[21..23]  00 00 00
[24..28]  'DTrac'
[29..47]  reserved zeros
[48..]    GS-232 command
```

因此，在 **MCU W5500 RX 缓冲** `0x0049` 为基址、且协议帧从 UDP 元信息第 8 字节后开始时：

| RX 缓冲索引 | UDP 应用层位置 | 含义 | MCU 证据 |
|---|---|---|---|
| `+0..+3` | UDP 元信息 | 源 IPv4，与控制板已配置服务端地址字段做比较 | `0x4876–0x48DC` |
| `+4..+5` | UDP 元信息 | 源端口 | `0x484A–0x486D` |
| `+6..+7` | UDP 元信息 | 数据长度 | `0x48EF–0x491F` |
| `+14..+20` | NRL2 `+6..+12` | APP SID，7 字节 | `0x4966–0x4996` 拷贝到 XDATA `0x0FD0` 处 |
| `+28` | NRL2 `+20` | Type 字段 | `0x48DC–0x4966` |
| `+56..` | NRL2 `+48..` | GS-232 控制文本 | `0x49BB–0x49F7` 拷贝，`0x4A32–0x4A3A` 分发 |

`0x4950..0x4966` 对消息 Type 1/2/5 分流；只有 Type 5 走已证实的 GS-232 处理分支。APP 对 RC-3040S 的 `C2\r\n`, `W%03d %03d\r\n`, `S\r\n` 与此匹配。

**安全性审计发现：**在已审查的 `0x482F–0x4A53` 路径中，没有发现显式以目标 RotorID（NRL2 偏移 `13..19`）和本机配置 ID 做常量时间或逐字节相等性检查的分支；可见字段检查包括远端源 IPv4、Type、长度/边界以及 APP SID 复制。此观察不能证明设备其它路径完全没有验证，更不能把“知道 RotorID”当作认证机制。自建 relay 必须做严格目标 ID/授权校验。

## 4. 服务端详细结论

**对应公开仓库/源码：**
- `https://github.com/hicaoc/nrllink/blob/main/decode.go`，`NRL21packet`、`decodeNRL21()` 约第 126–162 行；
- `https://github.com/hicaoc/nrllink/blob/main/deviceDB.go`，`IsCallSign()` 第 801–818 行；
- `https://github.com/hicaoc/nrllink/blob/main/udphub.go`，`udpProcess` 约 258–367、`NRL21parser` 390–575、`forwardMsg` 951–1043、`forwardCtl` 1046–1147 行。

收到 UDP 后，开源版本验证 48B 最小长度与 `NRL2` 魔数，解析地址/标识、读取 CallSign，并调用 `IsCallSign`；DTrac 写入 `DTrac` 导致此处报错。`Type 5` 在该开源服务器走 `forwardMsg()` 群组转发，而不是 `forwardCtl()`；`Type 6/10` 才按设备到设备控制通道处理。服务器内有心跳 Type 2 建立在线连接池，以及 6 秒静默剔除逻辑（`deviceDB.go`），但它的状态机不能直接假定兼容 DTrac 远程服务器。

即使放宽 `IsCallSign`，还需要：
- 复用 DTrac 特有的 ASCII 双字符长度语义，并按实际 UDP 字节长度交叉校验；
- 从偏移 6–12 与 13–19 恢复客户端 SID 和目标 RotorID，而不是作为 3B DMRID+11B 密码；
- 按 RotorID 映射当前设备的最后可信 UDP endpoint；
- 端到端心跳/短暂 NAT 映射保持与超时剔除；
- 独立的权限/会话验证，禁止一个 APP 广播控制同组其他旋转器；
- 明确 STOP 的转发、过期 GO 丢弃、ACK 与位置回读处理。

**关键区分：**目前服务器源码无证据支持 RC-3040S 的 DTrac 专用单播路由；DTrac 实际公网节点是否运行这份源码，以及中间是否存在定制网关，均未确认。官方产品说明确认 UDP/NRL 远程能力，但不等于开源项目已复刻它。

## 5. 离线字节级交叉验证

`rc3040s_nrl2_offline_verify.py` 使用**虚构 7B SID、7B RotorID、文档保留地址 192.0.2.1**构造三个 Type 5 DTrac NRL2 测试包，然后前置 W5500 8B UDP RX 元信息：

| 命令 | NRL2 总长 | W5500 RX 总长 | `RX[28]` | `RX[56:]` |
|---|---:|---:|---|---|
| `C2\r\n` | 52B | 60B | `0x05` | `C2\r\n` |
| `W181 030\r\n` | 58B | 66B | `0x05` | `W181 030\r\n` |
| `S\r\n` | 51B | 59B | `0x05` | `S\r\n` |

三个断言均已通过。**这是静态偏移交叉验证，不是 MCU 指令级仿真，也不是服务器联网兼容测试。**

## 6. 当前剩余的关键问题与下一步工作

1. **DTrac 专用服务端路由/认证：**优先获得实际公网服务软件、网络规范或经授权的完整双向抓包。当前可用开源 `nrllink` 不是直接替代物。
2. **Socket 7 的实际心跳构造与时序：**固件 `0x9235–0x925A` 和 `0x6BE3–0x6C62` 存在 Socket 7 发送路径；还需要将初始化寄存器、配置端口与包格式逐字节对应，判定保活周期、重连边界与对端应答条件。
3. **目标 RotorID 验证：**核对 MCU 的其它校验子函数以及服务端定制程序，确认目标识别码是否真正被校验，不能仅凭 APP 帧填有目标 ID 就认为目标绑定安全。
4. **控制反馈：**查询 `C2` 的响应如何从设备经 Socket 7 回送 APP、是否存在回包序号或控制队列；需要实测 RTT 和丢包场景。
5. **安全联调：**先断开电机功率、抓 UDP/TCP 双向流量并仅发只读 `C2`；再审查 STOP 与延迟 GO，确认物理限位和本地急停后做小角度控制。

**对 SkyRoof 的直接建议：**先做独立 `DTracNrl2Codec` + `DTracNrlTransport` + `DTracDiscovery` 模块和本地 mock relay，不修改原 PstRotator。不得把 UDP `sendto()` 成功当作云台已执行指令。任何远程控制必须配置可靠 STOP/硬件急停与机械限位。