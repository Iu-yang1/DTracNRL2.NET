#!/usr/bin/env python3
"""Offline RC-3040S / DTrac transport comparison, using invented IDs only.
No sockets are created and no rotator command is transmitted.
"""
from __future__ import annotations
import ipaddress, struct

HEAD = b'NRL2'
SUFFIX = bytes.fromhex('05000000445472616300000000000000000000000000000000000000')
APP_ID = bytes.fromhex('01020304050607')
ROT_ID = bytes.fromhex('123456789ABCDE')


def apk_packet(command: bytes) -> bytes:
    length = 48 + len(command)
    if length > 99:
        raise ValueError('DTrac two-character ASCII length exceeded')
    packet = HEAD + str(length).encode('ascii') + APP_ID + ROT_ID + SUFFIX + command
    assert len(packet) == length
    return packet


def w5500_udp_rx(source_ip: str, source_port: int, datagram: bytes) -> bytes:
    return (ipaddress.ip_address(source_ip).packed
            + struct.pack('!HH', source_port, len(datagram)) + datagram)


def firmware_offset_check(udp_rx: bytes) -> dict:
    # Reproduces offsets recovered from MCU 0x482F through 0x4A3A;
    # this is an evidence check, not an instruction-level emulator.
    if len(udp_rx) < 8 + 48:
        raise ValueError('Too short')
    src_ip = str(ipaddress.ip_address(udp_rx[0:4]))
    src_port, datagram_length = struct.unpack('!HH', udp_rx[4:8])
    if datagram_length != len(udp_rx) - 8:
        raise ValueError('Inconsistent W5500 length')
    nrl = udp_rx[8:]
    return {
        'udp_source_ip':src_ip,
        'udp_source_port':src_port,
        'nrl_magic':nrl[:4].decode('ascii', errors='replace'),
        'app_sid':nrl[6:13].hex().upper(),
        'rotor_id':nrl[13:20].hex().upper(),
        'message_type':udp_rx[28],  # 8 + 20
        'gs232_from_firmware_payload_offset_56':udp_rx[56:], # 8 + 48
        'declared_dtrac_length_ascii':nrl[4:6].decode('ascii', errors='replace'),
    }


def current_nrllink_callsign_valid(callsign: bytes) -> bool:
    # Mirrors hicaoc/nrllink deviceDB.go IsCallSign() as of 2026-10-24.
    return all(65 <= b <= 90 or 48 <= b <= 57 for b in callsign)


if __name__ == '__main__':
    for command in (b'C2\r\n', b'W181 030\r\n', b'S\r\n'):
        p = apk_packet(command)
        r = firmware_offset_check(w5500_udp_rx('192.0.2.1', 60050, p))
        assert r['message_type'] == 5
        assert r['gs232_from_firmware_payload_offset_56'] == command
        assert r['app_sid'] == APP_ID.hex().upper()
        assert r['rotor_id'] == ROT_ID.hex().upper()
        assert int(r['declared_dtrac_length_ascii']) == len(p)
        assert not current_nrllink_callsign_valid(p[24:30].rstrip(b'\x00'))
        print(f'{command!r:16} DTrac bytes={len(p)} W5500 bytes={len(p)+8} '
              f'NRL type={r["message_type"]}, firmware GS232={r["gs232_from_firmware_payload_offset_56"]!r} '
              'current upstream nrllink callsign valid=False')
    print('PASS: all three offline offset/compatibility assertions')