# DTracNRL2.NET

[![.NET SDK CI](https://github.com/Iu-yang1/DTracNRL2.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/Iu-yang1/DTracNRL2.NET/actions/workflows/ci.yml)

**Unofficial** and experimental .NET 10 implementation of the **DTrac NRL2 dialect** used by the RC-3040S controller. NuGet package/namespace: `DTrac.Nrl2`. **Not affiliated with DTrac and not a general nrllink implementation.** Do not operate a real rotator without local emergency stop and limit protection.

## Scope

- Byte-exact 48-byte DTrac NRL2 envelope: ASCII `NRL2`, two ASCII decimal length bytes, 7-byte APP SID, 7-byte RotorID, Type `0x05`, `DTrac` tag and GS-232 payload at offset 48.
- `C2\r\n` query, `W### ###\r\n` move, `S\r\n` stop, `L/R/U/D\r\n` jog, GS-232 position parser.
- Read-only LAN discovery by `cmd=ping\n` to UDP/60059; direct LAN GS-232 client over TCP/60020 with **no relay or RotorID** (separate transport from NRL2).
- Explicitly configured UDP relay client (no built-in undocumented public server, NAT bypass or automatic motor movement).
- Finite feedback-gated DEMO planner and loopback-only simulator.
- xUnit offline vector/validation tests and GitHub Actions package build.

## Build / offline test

```bash
dotnet test tests/DTrac.Nrl2.Tests/DTrac.Nrl2.Tests.csproj -c Release
dotnet run --project examples/DTrac.Nrl2.Console
# In a separate terminal:
dotnet run --project src/DTrac.Nrl2.Simulator
dotnet run --project examples/DTrac.Nrl2.Console -- --sim-query
```

## LAN relay / UDP native Socket 7 (experimental)

`DTrac.Nrl2.Relay.Nrl2LanRelay` can be hosted by your own application.
It listens on an explicitly selected IPv4 interface and accepts only
**allowlisted device IP + RotorID** pairs. A firmware Type2 packet registers
its UDP endpoint; an APP Type5 packet is forwarded to the registered rotor,
and a firmware Type5 reply returns to the corresponding APP SID endpoint.

**This is an independently designed local routing profile, not verified
compatibility with DTrac's production relay.** If the board requires a
Type1 handshake or proprietary authentication, actual Socket7 operation
still needs real capture and validation. UDP/60059 remains discovery only.
Motion forwarding is **disabled by default**; manual enablement does not
replace hardware stops. Never bind to an untrusted network.

An offline loopback example:

```csharp
using System.Net;
using DTrac.Nrl2.Relay;
using var relay = new Nrl2LanRelay(
    new IPEndPoint(IPAddress.Loopback, 19021),
    new Dictionary<string, IPAddress> { ["123456789ABCDE"] = IPAddress.Loopback });
await relay.RunAsync(cancellationToken);
```

Firmware AT configuration has a distinct **TCP/60030 Socket 0 management ingress** according to static analysis (framing remains unknown; no management network writer is exposed). The firmware also contains `AT+AZ_SPD=` / `AT+EL_SPD=`
and hardware auto-ramping, **but these AT commands are a separate parser from
GS-232 and have not been proven available on TCP/60020 or NRL2 Type5**.
`Rc3040sFirmwareCommands` builds these commands for isolated testing
but does not transmit or pretend the raw speed setting means degrees/s.

## Standalone UDP-to-TCP LAN gateway

`DTrac.Nrl2.Relay.Nrl2TcpGateway` is an alternative when RC-3040S Socket 7
cannot be reconfigured: it accepts **DTrac NRL2 Type5 over local UDP** and
translates requests into the firmware's native **GS-232 TCP/60020** protocol.
No private/public NRL relay, server registration or device Socket7 configuration
is required. This is a gateway, **not native UDP/60059 motion control**.

An executable .NET relay host is built in CI:

```bash
# Listens on localhost only; read-only queries + STOP by default
dotnet run --project src/DTrac.Nrl2.Host -- --tcp-gateway 127.0.0.1 19021 123456789ABCDE 192.0.2.10
# Experimental firmware Socket7 Type2+Type5 UDP relay; requires board service endpoint configuration
dotnet run --project src/DTrac.Nrl2.Host -- --native-relay 127.0.0.1 19021 123456789ABCDE 127.0.0.1
```

IDs and IPs above are examples only. When enabling motion in a trusted, isolated
environment, append `--enable-motion` and use real mechanical limits and STOP.
The TCP gateway requires a recent valid C2 read before target motion, and
rejects open-ended jog commands. **Neither mode provides authenticated
operator commands, a proven watchdog, or motor ACKs.** Do not deploy exposed
to other LAN hosts without strict firewalling and independent physical fail-safe.

## LAN-only use (no DTrac relay needed)

Firmware static analysis shows a separate, direct GS-232 TCP listener on port 60020.
The LAN protocol uses CR-terminated commands (`C2\r`, `S\r`, `W181 030\r`).
The direct client sends **no command on connection**; movement is always explicit.

```csharp
using DTrac.Nrl2.Transport;

using var lan = await Rc3040sLanClient.ConnectAsync("192.0.2.10", 60020);
var current = await lan.QueryPositionAsync(TimeSpan.FromSeconds(2));
// For safety, test queries with motor power disconnected first.
// lan.MoveToAsync(...) is explicitly opt-in; never use it without limit checks.
```

`192.0.2.10` is an **example-only reserved address**, not an expected device IP.
The LAN interface is not authenticated by this SDK; restrict TCP/60020 access to trusted networks.
Current evidence is from firmware static analysis, not hardware-in-the-loop testing.

## API

```csharp
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Rotator;
using DTrac.Nrl2.Transport;

var bytes = Nrl2Frame.Encode("01020304050607", "123456789ABCDE", Gs232Commands.Query());
using var client = new Nrl2UdpClient("127.0.0.1", 19120, "01020304050607", "123456789ABCDE");
var position = await client.QueryPositionAsync(TimeSpan.FromSeconds(2));
```

The IDs above are **fictional**. `127.0.0.1:19120` is only the local simulator, never a default Internet relay. The client will not send any command until explicitly invoked.

## Documentation

- [English technical notes](docs/en/protocol.md)
- [中文协议与安全说明](docs/zh-CN/protocol.md)
- [Reverse-engineering evidence and offline test vectors](docs/reverse-engineering/)

**Limitations:** DTrac's production relay registration, routing, NAT keepalive, authentication, stale-message protection and hardware acknowledgements have not been experimentally verified. Generic `hicaoc/nrllink` is known to reject the observed `DTrac` client tag. Firmware acceleration/deceleration is not reimplemented by this library. `AT+RESET` is experimental and never sent implicitly. The UDP transport cannot provide physical STOP guarantees.

### License

MIT for this independent implementation. The reverse-engineering notes describe interoperability findings, without shipping proprietary APK or firmware binaries.

## Firmware AT / motion evidence

[Stage 5: 60030 management ingress, speed encoding, configuration and motion analysis](docs/reverse-engineering/RC3040S_firmware_stage5_AT_and_motion.md). Raw AZ/EL speed commands are exactly five decimal digits; they may persist settings and cannot be assumed to work on TCP/60020 or NRL2 Type5. An external acceleration/jerk setpoint or a fully decoded native DEMO command has **not** been verified.

## Native management channel: WebSocket/TCP 60030

Firmware disassembly identifies RFC 6455 handshake strings (`Sec-WebSocket-Key`,
`101 Switching Protocols`) and FIN/opcode/mask/`126`/`127` frame parsing on TCP
port **60030**. WebSocket text/binary payload dispatch can call the AT handler
and some GS-232 logic. This corrects the previous "unknown proprietary
management frame" description.

`Rc3040sWebSocketClient.ConnectAsync(host)` performs **only** the WebSocket
handshake; `ReceiveTextAsync` exposes unsolicited messages. Explicit
`SendFirmwareCommandAsync(command, confirmPossiblePersistentWrite: true)`
is available for an *isolated, authorized hardware test* and may write EEPROM.
The first fully confirmed application-level request/response pairs have **not**
yet been captured, so **do not** infer successful configuration from a WebSocket
send. See [stage 6 evidence](docs/reverse-engineering/RC3040S_firmware_stage6_websocket.md).
