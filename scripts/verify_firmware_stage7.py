#!/usr/bin/env python3
"""SHA-pinned offline verification of RC-3040S AT echo and timer load paths."""
from pathlib import Path
import hashlib
import sys

EXPECTED_SHA256 = "59f5114c0289fb0f273aba50b5987688a27bf88e1034a577a38c46bb45ba019f"
CODE = {
    0x3871: "7b ff 7a 9d 79 57 12 93 9e",    # AT+TEST literal match
    0x387D: "7b 01 7a 00 79 51 12 8b ee",    # suffix pointer XDATA 0x0051
    0x8C14: "12 8a 1f",                      # outbound text payload builder
    0x8A5E: "7d 01 02 58 c9",                # R5=1 (opcode Text), enter WebSocket encode
    0x58F0: "ed 44 80 ff",                   # FIN bit OR with opcode
    0x3BFF: "12 95 00 12 7a 44",            # persist/apply AZ
    0x3C27: "12 95 00 12 7a bb",            # persist/apply EL
    0x8BDC: "12 7a 44 12 7a bb",            # cold initialization apply
    0x7A44: "90 09 a3 e0 fe a3 e0",         # AZ stored frequency
    0x7ABB: "90 09 a5 e0 fe a3 e0",         # EL stored frequency
    0x7A83: "8f 8d",                       # AZ timer high byte
    0x7AB4: "8f 8b",                       # AZ timer low byte
    0x7AF6: "8f d6",                       # EL timer high byte
    0x7B27: "8f d7",                       # EL timer low byte
}
LITERALS = {
    0x9D57: "AT+TEST=",
    0xA0DF: "AZ SPEED: %05u  HZ , %02f ",
    0xA101: "EL SPEED: %05u  HZ , %02f ",
}
def verify(path: str):
    data=Path(path).read_bytes()
    if hashlib.sha256(data).hexdigest() != EXPECTED_SHA256:
        raise ValueError("Mismatched firmware SHA256.")
    for address, hex_bytes in CODE.items():
        value=bytes.fromhex(hex_bytes)
        if data[address:address+len(value)] != value:
            raise AssertionError(f"Code mismatch at 0x{address:04X}")
    for address, literal in LITERALS.items():
        if not data.startswith(literal.encode("ascii"), address):
            raise AssertionError(f"Literal mismatch at 0x{address:04X}")
    print(f"PASS: {len(CODE) + len(LITERALS)} static checks (no device interaction).")
if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: verify_firmware_stage7.py <recovered-8051-ROM.bin>")
    verify(sys.argv[1])
