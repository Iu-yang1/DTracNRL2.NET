# Provenance and limitations

The attached reports and offline vectors originate from static analysis of a user-supplied DTrac APK and an RC-3040S 8051 firmware image on 2026-10-24. No binary firmware, APK, private credentials, device identifier or secret keys are redistributed. Code addresses and offsets are evidence from those specific samples, not guarantees for all firmware versions.

- `DTrac_RC3040S_APK_Firmware_Cross_Analysis.md`: cross-analysis of APK message construction and firmware control paths.
- `RC3040S_reverse_engineering_stage2.md`: LAN discovery and local TCP firmware evidence.
- `RC3040S_NRL2_Server_Firmware_Stage3_20261024.md`: Socket 7, relay mismatch and Type 5 path.
- `dtrac_protocol_vectors.txt`: fabricated-ID test vectors.
- Offline Python utilities are reference fixtures, not network clients.

The reports contain reverse-engineering findings rather than decompiled source code; the SDK implementation is independently structured. Observed incompatibility with generic nrllink is documented explicitly.
