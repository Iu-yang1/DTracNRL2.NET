# DTrac RC-3040S NRL2 (experimental)

In Settings > Rotator Control choose **Controller Driver: Native** and **Native Protocol: DtracNrl2**. UDP transport is automatically selected. Enter a DTrac-compatible NRL2 relay host/port, the RC-3040S 14-character Rotor ID and a persistent 14-character APP SID.

The Rotor ID editor discovers controllers on LAN using UDP broadcast `255.255.255.255:60059` and `cmd=ping\n`; discovery is separate from remote NRL2 relay control. Local TCP GS-232 control uses port 60020. The dialect includes a 48-byte header with two 7-byte IDs, Type 5 and a two-character ASCII length field. It is **not** interchangeable with generic nrllink server protocol: the latter may reject its `DTrac` callsign.

Supported commands: GS-232 `C2\r\n` query, `W### ###\r\n` target, `S\r\n` STOP and continuous-jog commands. Responses are checked against both expected identifiers; only a valid fresh position authorizes motion when open loop is disabled.

**PARK**: select *DTrac Single Target PARK* to send a single W command to the saved PARK position. This is **not** encoder calibration or verified mechanical homing. Hardware firmware handles acceleration/deceleration automatically.

**DEMO**: the NRL2 button on the manual rotator card opens a separate dialog. Configure start/end AZ, EL, step, dwell seconds and 1–5 cycles in Rotator settings. SkyRoof manages a finite feedback-gated cruise, not undocumented firmware AT+TEST. Emergency STOP, stale feedback, tracking changes and timeouts abort the cruise.

**Restart**: the dialog offers experimental `AT+RESET\r\n` after STOP and a confirmation prompt. Its remote acceptance has **not** been validated on hardware. Restart latches safety and requires reapplying settings after checking the antenna. Never treat UDP sends as physical motor acknowledgements.

The DTrac public relay has not been end-to-end verified. Test with isolated hardware and a real emergency stop before relying on remote operation.
