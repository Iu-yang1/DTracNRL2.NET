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
