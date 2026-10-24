# 第八阶段：Android APK 与 RC-3040S 251217 的 DEMO 命令兼容性差异

分析对象：
- 用户提供 `dtrac(1).apk`，SHA-256: `120c43f5d09e307ecfd7b168676486c41984d28a2a47b83d19abae7adf04d23a`
- 原始 `classes.dex`，SHA-256: `084e55db085a187cfb31f268bd3e7c2f8bb2b69e969576f56e2cb0cc2a7deb06`
- `RC_3040S_67S_8H_251217` 的 8051 ROM，SHA-256: `59f5114c0289fb0f273aba50b5987688a27bf88e1034a577a38c46bb45ba019f`

## APK 实际 DEMO 控制

DEX 字符串表含 `AT+DEMO=ON` (string 1665) 与 `AT+DEMO=OFF` (string 1664)：
- `Ll00;->onClick(Landroid/view/View;)V`，DEX code unit `0x017F..0x0190`：
  用户点击后反转 DEMO 按钮状态，分别加载 `AT+DEMO=ON` 或 `AT+DEMO=OFF`。
- `0x0186..0x018F` 附加 `\r\n` 并 `String.getBytes()`。
- `0x0190` 调用 `Lei0;->j([B)V`，排入旋转器发送队列。
- `Lei0.j` 调用 `LinkedList.add`，并通过 `Lci0.run`、`Ldi0.run` 派发连接层数据。具体传输路径由应用当前连接方式决定；不应据此宣称一定通过本地 TCP 或 Socket7 UDP。

**这是 APK 明确实现的原生按钮操作，但它并不能证实此版固件会接受同样的文本。**

## 与 251217 固件矛盾的直接证据

- 完整 41,738B ROM 搜索 `AT+DEMO` 得到 **0 次**；
- 固件主 AT 比较器 `0x3863..0x3C30` 的字符串表识别 `AT+TEST=`、DHCP/IP、型号/行程、速度等，
  没有 `AT+DEMO=ON`、`AT+DEMO=OFF`。
- 固件在 `0x387D→0x8BEE→0x8A1F→0x58C9` 将 `AT+TEST=` 后缀送 WebSocket 文本发送器。
  因此 `AT+TEST=` 更支持“文本回显诊断”而非原生往返巡航。
- 未搜到字符串不能排除其它**二进制**巡航模式或不同固件分支，但对本版 AT 直接匹配路径而言，
  APP 的 `AT+DEMO=ON/OFF` 不能作为已验证支持的命令。

## SDK 决策

- **不实现声称“固件原生 DEMO”的 `AT+DEMO` 自动发送器。**
- 已有 `DemoCruise` 保留为宿主程序软件闭环巡航；与设备内建 DEMO 严格分开。
- 未来如果实机抓包证明 `AT+DEMO` 被某条其它接口接受，需按型号+固件哈希加入能力表，
  并以机械安全试验验证 Start/Stop 而非因 APK 文本存在直接发送到设备。
- 串口协议、WebSocket 配置回读、真实加减速曲线与 Socket7 Type1 应答仍未完成。

## 复现

`scripts/verify_apk_firmware_demo_gap.py` 用 Python 标准库检验指定 APK 和该固件镜像的哈希、
DEX 中 DEMO 两条 ASCII 字符串，以及固件没有对应的 DEMO 字符串，
同时证实固件具备 `AT+TEST=`。此检查不运行 APK 或厂商 EXE，不发送任何命令。
