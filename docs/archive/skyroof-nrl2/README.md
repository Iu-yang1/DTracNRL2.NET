# Retired SkyRoof DTrac NRL2 UDP implementation — archival source

This folder stores the **exact source snapshot** of the discontinued SkyRoof-side experimental NRL2 UDP driver, its discovery/editor, DEMO/operations dialog, regression tests, and historical Settings.json field definitions. Archived .cs code is named `.cs.txt` to ensure it is not compiled.

The maintained protocol stack is in this repository's `src/DTrac.Nrl2` (discovery, framing, sessions, relay), not in SkyRoof. The archive is for documentation/provenance only and is not an endorsement of its correctness or real-hardware safety.

Legacy user configuration keys from SkyRoof include: `DtracNrlServerHost`, `DtracNrlServerPort`, `DtracNrlRotorId`, `DtracNrlAppSid`, `DtracUseNativePark`, and `DtracDemo*`. These are schema names; user-specific values are **not** migrated to GitHub and should not be copied. Existing SkyRoof Settings.json will not be reused as an NRL2 transport config.

**Removal scope:** SkyRoof replaces the experimental UDP driver with RC-3040S direct LAN TCP/60020 GS-232 motion control and TCP/60030 management. No UDP relay access is needed for these LAN features. NRL2 remote interop remains research-only here.

The enum value for legacy `DtracNrl2` was 10 before removal; preserving this fact matters if loading old numeric JSON enums. Settings can be imported only after an explicit user-directed migration.
