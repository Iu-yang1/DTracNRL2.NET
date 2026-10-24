#!/usr/bin/env python3
"""Non-invasive comparison of DTrac APK and a pinned RC-3040S 8051 ROM."""
from pathlib import Path
import hashlib
import sys
import zipfile

APK_SHA = "120c43f5d09e307ecfd7b168676486c41984d28a2a47b83d19abae7adf04d23a"
DEX_SHA = "084e55db085a187cfb31f268bd3e7c2f8bb2b69e969576f56e2cb0cc2a7deb06"
ROM_SHA = "59f5114c0289fb0f273aba50b5987688a27bf88e1034a577a38c46bb45ba019f"

def inspect(apk_path: str, rom_path: str) -> None:
    apk = Path(apk_path).read_bytes()
    rom = Path(rom_path).read_bytes()
    assert hashlib.sha256(apk).hexdigest() == APK_SHA, "Unexpected APK variant"
    assert hashlib.sha256(rom).hexdigest() == ROM_SHA, "Unexpected ROM variant"
    with zipfile.ZipFile(apk_path) as z:
        dex = z.read("classes.dex")
    assert hashlib.sha256(dex).hexdigest() == DEX_SHA, "Unexpected classes.dex"
    assert dex.count(b"AT+DEMO=ON") == 1
    assert dex.count(b"AT+DEMO=OFF") == 1
    assert rom.count(b"AT+DEMO") == 0
    assert rom.count(b"AT+TEST=") == 1
    print("PASS: APK DEMO commands present but absent from the pinned firmware AT literal table.")
    print("This is evidence of version/feature mismatch, not proof of every possible binary command.")

if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: verify_apk_firmware_demo_gap.py <dtrac.apk> <firmware_8051.bin>")
    inspect(sys.argv[1], sys.argv[2])
