# DTrac NRL2 protocol — evidence-driven notes

The protocol described here applies to an observed DTrac APK and one RC-3040S firmware sample, **not** every NRL2 implementation.

## Envelope (UDP payload, byte offsets)

| Offset | Length | Meaning |
| ---: | ---: | --- |
| 0 | 4 | ASCII `NRL2` |
| 4 | 2 | ASCII decimal **total datagram length**, 48–99 |
| 6 | 7 | APP SID (raw bytes) |
| 13 | 7 | Rotor ID (raw bytes) |
| 20 | 1 | Type `0x05` for GS-232 |
| 21 | 3 | Zero |
| 24 | 5 | `DTrac` |
| 29 | 19 | Reserved zero padding |
| 48 | variable | GS-232 ASCII payload |

Examples: `C2\r\n` = 52 bytes, `W181 030\r\n` = 58, `S\r\n` = 51. On the W5500 RX buffer, an 8-byte UDP metadata prefix means Type is at +28 and command at +56.

## Three distinct network paths

1. Local discovery: UDP broadcast port **60059**, `cmd=ping\n`, device replies `cmd=pong&...`.
2. Local GS-232 control: TCP **60020** according to firmware analysis.
3. Remote NRL2 path: independent W5500 Socket 7 UDP session to a **configured** relay endpoint. **Do not send Type 5 to discovery port 60059.**

## Confirmed versus unknown

Firmware evidence: Type 5 enters GS-232 handler via `0x482F -> 0x4A3A -> 0x3433`. Host `W` commands request a target position; firmware is responsible for motor ramps. `PARK` uses a single W command, not encoder rehoming. Host-side DEMO is a feedback-gated series of W commands, **not** a verified firmware cruise command.

Not confirmed: production DTrac relay binary/protocol, authentication, control arbitration, heartbeat, public routing, precise stale reply handling, hardware remote reset `AT+RESET`, physical motor ACK/STOP semantics. **Do not use without physical limits and an emergency stop.** Use real hardware only after isolated bench checks.


## Direct LAN control does not require the proprietary relay

`Rc3040sLanClient.ConnectAsync(host, 60020)` opens the firmware's direct
GS-232 TCP listener (Socket 3). No NRL2 envelope, APP SID, RotorID or DTrac
public relay is involved. UDP/60059 is discovery only.

For local TCP, the SDK sends CR-terminated `C2\r`, `S\r`,
`W### ###\r` or direction commands, not the CRLF APK/NRL2 wrapping.
The expected position response is `AZ=ddd  EL=ddd\r\n`, based on static
firmware analysis. The connection itself sends **no motion command**.
Physical emergency stop and mechanical limits remain mandatory; the LAN TCP
interface is not authenticated and should be network isolated.

Hardware compatibility, timeout and TCP reconnect behavior have not been
measured on a real RC-3040S. If NRL2 transport is specifically required,
an independently validated LAN relay would still be needed.
