#!/usr/bin/env python3
"""Offline checks for one specific RC-3040S STC8H firmware image.
Usage: python scripts/verify_firmware_stage6.py path/to/firmware_8051.bin

Does not execute vendor code, open sockets, send commands, or change hardware.
No firmware/APK binaries are included in this repository.
"""
from __future__ import annotations
import hashlib
from pathlib import Path
import sys

EXPECTED_SHA256 = "59f5114c0289fb0f273aba50b5987688a27bf88e1034a577a38c46bb45ba019f"

AT_MATCHERS = {
    0x3877: (0x9D57, "AT+TEST="),
    0x388F: (0x9D60, "AT+INIT"),
    0x38AA: (0x9D68, "AT+RESET"),
    0x38D1: (0x9D75, "AT+DHCP=ON"),
    0x38E2: (0x9D80, "AT+DHCP=OFF"),
    0x38F4: (0x9D8C, "AT+CALL="),
    0x3966: (0x9D95, "AT+SSID="),
    0x39AB: (0x9D9E, "AT+IP="),
    0x39EB: (0x9DA5, "AT+GATEWAY="),
    0x3A2B: (0x9DB1, "AT+MASK="),
    0x3A6B: (0x9DBA, "AT+DNS="),
    0x3AAB: (0x9DC2, "AT+D_IP="),
    0x3B2C: (0x9DCB, "AT+YT=3040"),
    0x3B3E: (0x9DD6, "AT+YT=3051"),
    0x3B4E: (0x9DE1, "AT+YT=3060"),
    0x3B5E: (0x9DEC, "AT+YT=YS3051"),
    0x3B6E: (0x9DF9, "AT+YT=SP5050"),
    0x3B80: (0x9E06, "AT+P450"),
    0x3B93: (0x9E0E, "AT+P360"),
    0x3BE6: (0x9E2A, "AT+AZ_SPD="),
    0x3C0E: (0x9E35, "AT+EL_SPD="),
}

WEB_SOCKET_STRINGS = {
    0x9FD0: "HTTP/1.1 101 Switching Protocols",
    0x9FF3: "Upgrade: websocket",
    0xA008: "Connection: Upgrade",
    0xA01E: "Sec-WebSocket-Accept: ",
    0xA038: "Sec-WebSocket-Key:",
    0xA2E5: "GET / HTTP",
    0x9E4B: "WebSocket close ack",
    0x9EA7: "WebSocket ERR",
}

CODE_SIGNATURES = {
    0x624E: bytes.fromhex("12 3F 6B"),  # WebSocket frame parser call
    0x401F: bytes.fromhex("EF 64 7E"),  # extended payload len 126
    0x4071: bytes.fromhex("E0 64 7F"),  # extended payload len 127
    0x5DB7: bytes.fromhex("12 38 63"),  # AT parser
    0x5DCE: bytes.fromhex("12 34 33"),  # GS232 parser
    0x3BEC: bytes.fromhex("7F 0A 12 72 0E"),  # fixed 5-digit AZ parsing
    0x3C14: bytes.fromhex("7F 0A 12 72 0E"),  # fixed 5-digit EL parsing
    0x3C02: bytes.fromhex("12 7A 44"),  # apply AZ timing
    0x3C2A: bytes.fromhex("12 7A BB"),  # apply EL timing
}

def check(data: bytes) -> int:
    if hashlib.sha256(data).hexdigest() != EXPECTED_SHA256:
        raise ValueError("Firmware does not match the analyzed build SHA-256.")

    count = 0
    for offset, (address, command) in AT_MATCHERS.items():
        expected = bytes((0x7B, 0xFF, 0x7A, address >> 8,
                          0x79, address & 0xFF, 0x12, 0x93, 0x9E))
        if data[offset-6:offset+3] != expected:
            raise AssertionError(f"AT matcher xref changed at 0x{offset:04X}")
        actual = data[address:address+len(command)].decode("ascii")
        if actual != command or data[address+len(command)] != 0:
            raise AssertionError(f"AT literal changed at 0x{address:04X}: {actual!r}")
        count += 1

    for offset, value in WEB_SOCKET_STRINGS.items():
        if not data.startswith(value.encode("ascii"), offset):
            raise AssertionError(f"Missing WebSocket string 0x{offset:04X}: {value}")
        count += 1

    for offset, sequence in CODE_SIGNATURES.items():
        if data[offset:offset+len(sequence)] != sequence:
            raise AssertionError(f"Machine instruction check failed 0x{offset:04X}")
        count += 1

    print(f"PASS: {count} static assertions on SHA-256-pinned firmware.")
    print("Scope: ASCII AT matcher table, WebSocket handshake/frame branches,")
    print("      AT+GS232 dispatch, and 5-digit speed-to-timer path.")
    print("LIMIT: no network interchange, no physical speed/accel measurements.")
    return count

if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    check(Path(sys.argv[1]).read_bytes())
