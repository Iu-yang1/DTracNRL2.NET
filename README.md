# DTracNRL2.NET

[![.NET SDK CI](https://github.com/Iu-yang1/DTracNRL2.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/Iu-yang1/DTracNRL2.NET/actions/workflows/ci.yml)

**Unofficial** and experimental .NET 10 implementation of the **DTrac NRL2 dialect** used by the RC-3040S controller. NuGet package/namespace: `DTrac.Nrl2`. **Not affiliated with DTrac and not a general nrllink implementation.** Do not operate a real rotator without local emergency stop and limit protection.

## Scope

- Byte-exact 48-byte DTrac NRL2 envelope: ASCII `NRL2`, two ASCII decimal length bytes, 7-byte APP SID, 7-byte RotorID, Type `0x05`, `DTrac` tag and GS-232 payload at offset 48.
- `C2\r\n` query, `W### ###\r\n` move, `S\r\n` stop, `L/R/U/D\r\n` jog, GS-232 position parser.
- Read-only LAN discovery by `cmd=ping\n` to UDP/60059; TCP/60020 is **local** GS-232, **not** NRL2 remote relay.
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
