# 260223_C TCP/60030 间歇性连接失败：2026-10-24 12:10 +08

实机 11:38 的 v4 经 `ws://192.168.1.9:60030/` 握手并接收了多帧配置转储。
12:10 的首个 `RC3040S-AT-Tester 1.0.0` 在只读预检查**建立连接时**
得到 `WebSocketException: Unable to connect to the remote server`。
还没有发送 GS-232 C2、AT+TEST、速度设置、EEPROM 写入或重启指令。
不能据此认定 AT 指令不支持、设备故障或 60030 恒久关闭。

检查 SDK 发现 `Rc3040sWebSocketClient.DisposeAsync` 在**每个正常断开**时
无条件调用 `socket.Abort()`，不曾尝试 WebSocket Close 帧/握手。
这属于可以明确证实的本地代码缺陷，但尚不能证明它导致了远端 W5500
Socket0 的连接资源占用。现改为有界正常关闭，失败时回退 Abort。
另对 `192.168.x.x/10.x.x.x/172.16..31.x.x/127/8/169.254/16` 本地字面
IPv4 地址明确绕过系统 WebSocket HTTP 代理，而不更改 Windows 全局代理设置。
由于原 v4 和 AT Tester 使用相同 WebSocket SDK，**代理因素仍是待证假设**。

新的 AT 测试包会在首次只读 WS 建连失败时自动记录异常链、
Socket ErrorCode/NativeErrorCode、本机 TCP 会话状态，并对 60030/60020
进行一次短超时直连探测；只读路径可在短延迟后**最多再尝试一次**，
但任何首次故障会禁止本轮所有 AT 写命令。
原生 GS-232/60020 控制和 UDP 中继实现不作改动。

用户仅需运行新版 EXE 并发回日志，**无须安装 PowerShell 脚本、开发环境或反复手动端口测试**。
