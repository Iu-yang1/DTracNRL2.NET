# RC-3040S speed/EEPROM/acceleration reverse engineering — Stage 12 (2026-10-24)

**Evidence:** direct inspection of 41,738-byte 8051 image recovered from the provided STC EXE, SHA256 `59f5114c0289fb0f273aba50b5987688a27bf88e1034a577a38c46bb45ba019f`. The supplied Android APK SHA256 is `120c43f5d09e307ecfd7b168676486c41984d28a2a47b83d19abae7adf04d23a`. This static analysis applies strictly to firmware **251217**. Operator hardware firmware is newer, **260223_C**; no motor movement or AT write was performed.

## Recovered AT write-to-timer path

| Path | AZ | EL |
| --- | --- | --- |
| AT entry | `0x3BE0` | `0x3C08` |
| fixed five-digit parser | `0x3BEC → 0x720E` | `0x3C14 → 0x720E` |
| temporary parsed 16-bit value | `0x102A–2B` | `0x102C–2D` |
| config/EEPROM mirror | `0x09A3–A4` (offset 2–3) | `0x09A5–A6` (offset 4–5) |
| persistence | `0x3BFF → 0x9500 → 0x8CE7` | `0x3C27 → 0x9500 → 0x8CE7` |
| timer apply | `0x7A44` | `0x7ABB` |
| SFR register writes | TH1=0x8D, TL1=0x8B | T2H=0xD6, T2L=0xD7 |
| startup apply | `0x8BDC` | `0x8BDF` |

Factory initialization `0x53F3–0x540B` stores `0x1388` = **5000** independently to the two speed config fields. User's 260223_C returned `AZ_SPD=05000` and `EL_SPD=05000` but that is not proof of identical runtime timing between firmware builds.

At `0x7A65..0x7AAF` and corresponding EL blocks, static 32-bit arithmetic uses constant `0x000E1000` (921600), signed division `0x8A63`, and `0x00010000 - quotient` to compute 16-bit timer reload:

`count = floor(921600 / S)`; `reload = (65536 - count) mod 65536`.

At `S=5000`, count=184 and reload=**0xFF48**. With an effective 921600 ticks/s timer base, event cadence would be **5008.70 Hz** (rounding of the nominal 5000); this is not yet a direct measurement of motor steps.

**Major recovered conversion:** at `0x608D–0x6167` firmware uses IEEE754 `0x48B09000 = 361600.0f` (AZ), `0x489F6000 = 326400.0f` (EL), and `0x43B40000 = 360.0f` with float division routine `0x02CF` to display:

- AZ degrees/second = `S*360/361600`; at 05000, **4.9779°/s**.
- EL degrees/second = `S*360/326400`; at 05000, **5.5147°/s**.

ROM diagnostic formats `AZ SPEED: %05u HZ , %02f °/s` and `EL SPEED: ...`; degree mark is GBK bytes `A1 E3`. These are **firmware nominal rates**, not measured physical shaft angular velocities. Mechanical ratio and any pulse gate timing still require physical confirmation. STC timer register semantics: https://github.com/IOsetting/FwLib_STC8/blob/master/include/fw_tim.h.

## Independent configuration readback

At `0x24DF–0x251C` the firmware independently loads AZ XDATA mirror `0x09A3/4`, calls integer formatter `0x704B`, sends the `AT+AZ_SPD=` text. At `0x2536–0x2573` it does the same with EL `0x09A5/6` and `AT+EL_SPD=`. Thus it **does** have a readback code path separate from the AT write routine, and the 260223_C hardware has been observed emitting both configuration strings. This is **XDATA configuration mirror readback**, not independent verification of actual timer output, successful EEPROM commit, or a stand-alone speed-specific query command. The observed dump spans delayed WebSocket frames; 700ms silence must not be misclassified as a protocol end marker.

## Acceleration and deceleration

Timer reload routines `0x7A44` and `0x7ABB` have just two direct call sites each: **AT configuration write** and **firmware startup initialization**. The observed SFR timer reload writes `TH1/TL1/T2H/T2L` each occur once in those routines. Timer ISR entry paths `0x001B → 0x8D22 → 0x9432` (AZ) and `0x0063 → 0x8D5D → 0x9842` (EL) continue through position/state logic; **no confirmed periodic timer reload changes as a function of remaining angle** have been identified. Nor are `AT+ACC`, `AT+DEC`, `AT+JERK` strings present. This supports fixed timer period plus state gating **in the inspected 251217 image**, not a proven native trapezoid/S-curve ramp.

This does not exclude pulse skipping in an ISR, acceleration inside the external motor driver, or a new implementation in actual 260223_C firmware. Android DEX has `AT+DEMO=ON/OFF` text but no `AT+AZ_SPD=` or `AT+EL_SPD=` literal; absence of literals does not prove dynamic construction cannot occur. DEMO is not evidence of automatic acceleration.

## Numeric safety and SDK correction

When `S=1..14`, `floor(921600/S)>65535`: writing the 16-bit reload may wrap; `ushort` alone **does not** make that safe. This update rejects raw speed values **below 15** at command-construction time. **15..65535 is not an approved mechanical operating range**, only an arithmetic representation domain under the recovered 251217 model. Unknown driver and mechanical limits still require independent validation. Repeated AT speed writes may persist full config/EEPROM; do not use them in SkyRoof's per-update tracking loop.

## Reproduction, version risk and remaining validation

The original APK/firmware binary is not tracked. A read-only machine-code verification script records relevant instruction anchors and computed values and can be run locally against the **matching SHA256 firmware**. No real RotorID, MAC or cloud credentials are recorded.

Next: obtain real 260223_C firmware binary; fully trace timer IRQ pulse/position state transitions; measure isolated STEP pulse frequency and output angular rate against S=05000; determine the precise WebSocket dump completion marker; only then design a safe one-off speed write/read/restore workflow. Avoid AT+INIT and automated speed ramp writes.