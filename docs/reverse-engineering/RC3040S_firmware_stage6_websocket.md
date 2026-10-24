# RC-3040S 固件逆向第六阶段：TCP/60030 WebSocket 管理协议

分析日期：2026-10-24。目标固件 SHA-256: `59f5114c0289fb0f273aba50b5987688a27bf88e1034a577a38c46bb45ba019f`。本阶段仅离线检查，未向实体设备发送命令。

## 一、重大纠正：60030 是 WebSocket 通道

第五阶段报告曾将 `0x3F6B` 识别为未知私有 TCP 管理封包解析器。通过反汇编及字符串交叉验证，现确认其布局与 RFC 6455 WebSocket 一致：

- 固件代码段字符串 `0x9FD0`: `HTTP/1.1 101 Switching Protocols\r\n`
- `0x9FF3`: `Upgrade: websocket\r\n`
- `0xA008`: `Connection: Upgrade\r\n`
- `0xA01E`: `Sec-WebSocket-Accept: `
- `0xA038`: `Sec-WebSocket-Key:`
- `0xA2E5`: `GET / HTTP`
- `0x9E4B`: `WebSocket close ack`
- `0x9EA7`: `WebSocket ERR`
- `0x9E80`: `rx: %s`

`0x3F9A..0x3FC3`: 从首字节取 FIN 位、低四位 opcode。
`0x3FE1..0x4017`: 从第二字节取 MASK 位，`byte1 & 0x7F`。
`0x401F..0x406C`: `0x7E` 分支读取随后两个长度字节。
`0x406E..0x40D4`: `0x7F` 分支读取随后八个长度字节。
`0x4135..0x419F`: 存在四字节 mask key 提取路径。
`0x41DA..0x425E`: 存在依照掩码还原数据的路径。

`0x89D9` 根据连接状态进行 HTTP 握手与二进制帧分类；`0x5FA7` 包含 `Sec-WebSocket-Key` 搜索与 `0x84F0` 响应发送。固件的 WebSocket 端点是 Socket0/TCP 60030，握手路径预期 `/`。本次仅从机器码恢复标准协议层，不代表已实机验证所有标准浏览器连接选项。

## 二、WebSocket opcode 分发与 AT / GS-232 解析器关系

`0x6240` 调用 `0x3F6B`；`0x6257..0x6270` 对解码后的 opcode 分类：
- opcode 2（二进制）：`0x62BD` → `0x5CE1`
- opcode 1（文本）：`0x6270` → `0x5CE1`
- opcode 8（Close）：`0x6300` → Close 应答/终止分支
- opcode 9（Ping）：`0x62DD` → Pong 应答分支

`0x5CE1` 将负载复制至固定内部缓冲，再由 `0x5DB7` 调用 AT 解析器 `0x3863`；`0x5DCE` 在特定分类标志下还调用 GS-232 解析器 `0x3433`。这证明 WebSocket 应用数据确实可到达 AT 处理器，而不是把裸 AT 直接送至 60030。

固件包含固定 JSON 片段 `{"type":"welcome","message":"Hello from STC8H!"}`、`{"status":"LED_ON"}` 和 `{"status":"LED_OFF"}`，说明管理通道还承担设备状态或调试消息；**这些 JSON 的完整命令/应答语义尚未恢复**。不要在 SDK 中把 welcome 当成配置操作成功的 ACK。

## 三、速度与加减速

AT 命令 `AT+AZ_SPD=` / `AT+EL_SPD=` 经固件 `0x720E` 固定读取五位十进制，经 `0x9500` 持久化，`0x7A44` / `0x7ABB` 修改 STC 定时器等寄存器。存在格式字符串：
- `AZ SPEED: %05u  HZ , %02f .../s`
- `EL SPEED: %05u  HZ , %02f .../s`

由文字 `HZ` 可以判断固件把原始速度参数用于频率相关控制，但 `/s` 换算公式需要跟踪浮点运算和硬件传动比才能证明。

`AT+TEST=` 在 `0x3871` 匹配后调用 `0x8BEE`，其后续涉及 `0x8A1F` / `0x977C`。这条路径目前**不能证实**参数是巡航起止角、循环次数或速度，也不能认定它提供可直接配置的加速度/减速度曲线。

**尚未复原独立加速度指令、定量坡度曲线及设备配置完整回读协议。** 不应将控制板广告中的自动加减速描述误写成可配置的 ACC/DEC 参数。

## 四、SDK 实现

`Rc3040sWebSocketClient` 使用 .NET `ClientWebSocket`（RFC 6455 标准 Mask/Opcode/扩展长度交给框架），连接 `ws://host:60030/`。仅建立连接不会发送 AT 或电机命令。支持受限的消息接收、原始文本发送，显式确认后发送 AT 配置；默认不自动写 EEPROM。Loopback WebSocket 协议测试模拟 RFC 6455 Upgrade 与字节收发。

边界：固件没有网络鉴权或防重放的已验证实现，不应暴露 WebSocket 管理端口至互联网或不受信任的网络。是否需要附加 WebSocket 子协议、实际收到的 ACK 格式、速度单位及合法机械速度范围仍需实机读写验证。
