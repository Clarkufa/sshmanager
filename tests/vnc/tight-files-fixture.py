"""Loopback TightVNC 2 protocol fixture. No remote desktop or SSH service is required."""

import socket
import struct
import sys
from pathlib import Path

REQUESTS = {
    0x102: b"FTCFLRST", 0x106: b"FTCFURST", 0x108: b"FTCUDRST",
    0x10A: b"FTCUERST", 0x10C: b"FTCFDRST", 0x10E: b"FTCDDRST",
    0x111: b"FTCMDRST", 0x113: b"FTCFRRST", 0x115: b"FTCFMRST",
}
REPLIES = {
    0x103: b"FTSFLRLY", 0x107: b"FTSFURLY", 0x109: b"FTSUDRLY",
    0x10B: b"FTSUERLY", 0x10D: b"FTSFDRLY", 0x10F: b"FTSDDRLY",
    0x110: b"FTSDERLY", 0x112: b"FTSMDRLY", 0x114: b"FTSFTRLY",
    0x116: b"FTSFMRLY", 0x119: b"FTLRFRLY",
}
PREFIX = 0xFC000000
MODIFIED = 1760000000000


def pack(format, *values):
    return struct.pack(">" + format, *values)


def string(value):
    data = value.encode("utf-8")
    return pack("I", len(data)) + data


def block(data):
    return pack("BII", 0, len(data), len(data)) + data


def caps(values):
    return b"".join(pack("I", PREFIX | code) + b"TGHT" + name for code, name in values.items())


def serve(sock, mode):
    sock.settimeout(20)
    stream = sock.makefile("rb")

    def read(count):
        data = stream.read(count)
        assert len(data) == count, (count, len(data))
        return data

    def unpack(format):
        return struct.unpack(">" + format, read(struct.calcsize(">" + format)))

    def path():
        return read(unpack("I")[0]).decode("utf-8")

    def reply(code, payload=b""):
        sock.sendall(pack("I", PREFIX | code) + payload)

    sock.sendall(b"RFB 003.008\n")
    assert read(12) == b"RFB 003.008\n"
    if mode == "plain":
        sock.sendall(bytes([1, 1]))
        assert stream.read(1) == b""
        return

    sock.sendall(bytes([1, 16]))
    assert read(1) == bytes([16])
    sock.sendall(pack("III", 0, 0, 0))
    assert read(1) == bytes([1])  # Shared session.
    sock.sendall(pack("HH", 640, 480) + bytes(16) + string("Fixture"))
    if mode == "absent":
        sock.sendall(pack("HHHH", 0, 0, 0, 0))
        assert stream.read(1) == b""
        return

    sock.sendall(pack("HHHH", len(REPLIES), len(REQUESTS), 0, 0) + caps(REPLIES) + caps(REQUESTS))
    if mode in ("denied", "broken"):
        assert unpack("IB") == (PREFIX | 0x102, 0) and path() == "/"
        if mode == "denied":
            reply(0x119, string("File transfer disabled"))
            assert stream.read(1) == b""
        return

    entries = {"/C:": None}
    upload = download = None
    position = 0
    calls = set()
    while header := stream.read(4):
        code = struct.unpack(">I", header)[0] - PREFIX
        calls.add(code)
        if code == 0x102:
            assert unpack("B") == (0,)
            folder = path()
            assert folder == "/" or entries[folder] is None
            rows = []
            for name, data in entries.items():
                if name[:name.rindex("/")] == folder.rstrip("/"):
                    rows.append(pack("QQH", len(data or b""), MODIFIED, int(data is None)) + string(name.rsplit("/", 1)[1]))
            reply(0x103, block(pack("I", len(rows)) + b"".join(rows)))
        elif code == 0x111:
            folder = path()
            assert folder not in entries
            entries[folder] = None
            reply(0x112)
        elif code == 0x106:
            upload = path()
            assert upload not in entries and unpack("BQ") == (0, 0)
            entries[upload] = b""
            reply(0x107)
        elif code == 0x108:
            compressed, packed, plain = unpack("BII")
            assert compressed == 0 and packed == plain and 0 < plain <= 65536
            entries[upload] += read(plain)
            reply(0x109)
        elif code == 0x10A:
            flags, updated = unpack("HQ")
            assert flags == 0 and updated >= MODIFIED and len(entries[upload]) >= 131073
            upload = None
            reply(0x10B)
        elif code == 0x115:
            source, destination = path(), path()
            assert destination not in entries
            entries[destination] = entries.pop(source)
            reply(0x116)
        elif code == 0x10C:
            download = entries[path()]
            assert unpack("Q") == (0,)
            position = 0
            reply(0x10D)
        elif code == 0x10E:
            compressed, maximum = unpack("BI")
            assert compressed == 0 and maximum <= 65536
            data = download[position:position + maximum]
            position += len(data)
            if data:
                reply(0x10F, block(data))
            else:
                reply(0x110, pack("BQ", 0, MODIFIED))
        elif code == 0x113:
            del entries[path()]
            reply(0x114)
        else:
            raise AssertionError(f"Unexpected request {code:#x}")
    assert calls == REQUESTS.keys() and entries == {"/C:": None}


if __name__ == "__main__":
    with socket.create_server(("127.0.0.1", 0)) as listener:
        listener.settimeout(30)
        Path(sys.argv[1]).write_text(str(listener.getsockname()[1]), encoding="ascii")
        for mode in ("enabled", "absent", "plain", "denied", "broken"):
            connection, _ = listener.accept()
            with connection:
                serve(connection, mode)
            print(f"ok {mode}", flush=True)
