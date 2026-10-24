#!/usr/bin/env python3
"""Offline-only DTrac RC-3040S discovery/NRL2 framing lab.

Reconstructed from user-supplied dtrac.apk for interoperability study.
No network operations, no connection, no physical motion.
"""
from __future__ import annotations
import argparse
import ipaddress
import re

DISCOVERY_QUERY = b"cmd=ping\n"
MAGIC = b"NRL2"
ROTOR_SUFFIX = bytes.fromhex(
    "05000000445472616300000000000000000000000000000000000000"
)
RADIO_SUFFIX = bytes.fromhex(
    "06000000445472616300000000000000000000000000000000000000"
)


def id_bytes(hex14: str) -> bytes:
    if not re.fullmatch(r"[0-9a-fA-F]{14}", hex14):
        raise ValueError("ID must be 14 hexadecimal characters (7 bytes)")
    return bytes.fromhex(hex14)


def build_nrl(app_sid: str, rotor_id: str, payload: bytes, *, category: str = "rotor") -> bytes:
    """Reproduce *APK-observed* framing, including ASCII-decimal length quirk."""
    length = 48 + len(payload)
    if not (48 <= length <= 99):
        raise ValueError("APK's ASCII length field changes width beyond 99 bytes: do not assume it interoperable")
    suffix = ROTOR_SUFFIX if category == "rotor" else RADIO_SUFFIX if category == "radio" else None
    if suffix is None:
        raise ValueError("category must be rotor or radio")
    pkt = MAGIC + str(length).encode('ascii') + id_bytes(app_sid) + id_bytes(rotor_id) + suffix + payload
    assert len(pkt) == length and pkt[20:25] == (b'\x05\x00\x00\x00\x44' if category == 'rotor' else b'\x06\x00\x00\x00\x44')
    return pkt


def parse_nrl(raw: bytes, *, expected_sid: str | None = None, expected_rotor_id: str | None = None) -> dict:
    if len(raw) < 48 or not raw.startswith(MAGIC):
        raise ValueError("Not a complete NRL2 datagram")
    declared = raw[4:6]
    length_ascii = declared.decode('ascii', errors='replace')
    # This reports evidence; unlike the APK it does not treat arbitrary NRL2 as authenticated.
    if not (length_ascii.isdigit() and int(length_ascii) == len(raw)):
        raise ValueError(f"ASCII length {length_ascii!r} does not equal actual datagram length {len(raw)}")
    sid = raw[6:13].hex().upper()
    rotor = raw[13:20].hex().upper()
    if expected_sid and sid != expected_sid.upper():
        raise ValueError("Client SID mismatch")
    if expected_rotor_id and rotor != expected_rotor_id.upper():
        raise ValueError("RotorID mismatch")
    return {
        'length': len(raw), 'length_bytes_hex': declared.hex().upper(),
        'sid': sid, 'rotor_id': rotor, 'type': raw[20],
        'client_tag': raw[24:29].decode('ascii', errors='replace'),
        'payload_hex': raw[48:].hex().upper(),
        'payload_ascii_escaped': ascii(raw[48:])[2:-1],
    }


def parse_discovery_reply(payload: bytes) -> dict:
    s = payload.decode('ascii', 'replace').strip()
    parts = s.split('&')
    if not parts or parts[0] != 'cmd=pong':
        raise ValueError('Not a DTrac discovery pong')
    fields = {}
    for part in parts[1:]:
        if '=' in part:
            k, v = part.split('=', 1)
            fields[k] = v
    typ = fields.get('type','')
    if 'ip' in fields: ipaddress.ip_address(fields['ip'])
    if 'port' in fields and not 1 <= int(fields['port']) <= 65535:
        raise ValueError('Bad port')
    identifier = fields.get('id',fields.get('rotorid'))
    return {'type': typ, 'ip': fields.get('ip'), 'port': int(fields['port']) if 'port' in fields else None,
            'rotor_id': identifier, 'fields': fields}


def demo() -> None:
    sid, rotor = '01020304050607', '123456789ABCDE'  # fictitious test IDs
    print('DISCOVERY TX', DISCOVERY_QUERY.hex(' ').upper())
    print('DISCOVERY RX', parse_discovery_reply(b'cmd=pong&type=RC-3040S&ip=192.0.2.10&port=60020&rotorid=123456789ABCDE'))
    for payload in [b'C2\r\n', b'W181 030\r\n', b'S\r\n']:
        pkt=build_nrl(sid,rotor,payload)
        parsed=parse_nrl(pkt,expected_sid=sid,expected_rotor_id=rotor)
        assert bytes.fromhex(parsed['payload_hex']) == payload
        print('\nCommand:',repr(payload),'\nBytes:',pkt.hex(' ').upper(), '\nDecoded:',parsed)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description='DTrac offline-only NRL protocol study')
    parser.add_argument('--demo',action='store_true',help='print fixed fake-ID examples')
    args=parser.parse_args()
    demo()