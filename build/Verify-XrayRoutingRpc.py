#!/usr/bin/env python3
"""Pinned CLI-only regression probe: synthetic Unix gRPC peer, no Xray server/TUN or live configuration."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import struct
import subprocess
import tempfile
import threading
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def frame(kind, flags, stream, payload=b""):
    return len(payload).to_bytes(3, "big") + bytes((kind, flags)) + struct.pack("!I", stream) + payload


def literal(name, value):
    # HPACK literal without indexing; these fixed strings fit in a single length byte.
    return b"\0" + bytes((len(name),)) + name + bytes((len(value),)) + value


def probe(executable, root, reference, assets, expected_success):
    name = reference.split(":")[1]
    endpoint = str(root / "mock.sock")
    captured, failures = bytearray(), []
    with socket.socket(socket.AF_UNIX) as server:
        server.bind(endpoint)
        server.listen(1)
        server.settimeout(8)

        def serve():
            try:
                with server.accept()[0] as peer:
                    peer.settimeout(8)

                    def read(count):
                        result = b""
                        while len(result) < count:
                            data = peer.recv(count - len(result))
                            if not data:
                                raise EOFError("Synthetic peer closed early")
                            result += data
                        return result

                    assert read(24) == b"PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"
                    peer.sendall(frame(4, 0, 0))
                    while True:
                        header = read(9)
                        length = int.from_bytes(header[:3], "big")
                        kind, flags, stream = header[3], header[4], int.from_bytes(header[5:], "big")
                        assert length <= 1024 * 1024
                        data = read(length)
                        if kind == 4 and not flags & 1:
                            peer.sendall(frame(4, 1, 0))
                        elif kind == 6 and not flags & 1:
                            peer.sendall(frame(6, 1, 0, data))
                        elif kind == 0:
                            captured.extend(data)
                            if flags & 1:
                                # Emulate lazy lookup in the server's unchanged startup directory.
                                status = b"0" if (assets / name).is_file() else b"13"
                                peer.sendall(frame(1, 4, stream, b"\x88" + literal(b"content-type", b"application/grpc"))
                                             + frame(0, 0, stream, b"\0\0\0\0\0")
                                             + frame(1, 5, stream, literal(b"grpc-status", status)))
                                return
            except Exception as error:
                failures.append(type(error).__name__)

        worker = threading.Thread(target=serve, daemon=True)
        worker.start()
        config = root / "synthetic.json"
        config.write_text(json.dumps({"routing": {"rules": [{"outboundTag": "nonhome", "domain": [reference]}]}}))
        environment = {key: value for key, value in os.environ.items() if not key.lower().startswith(("xray_", "xray."))}
        environment["XRAY_LOCATION_ASSET"] = str(root / "candidate-assets")
        result = subprocess.run([str(executable), "api", "adrules", "--server=unix://" + endpoint,
                                 "--timeout=3", "--append=false", str(config)], cwd=root, env=environment,
                                capture_output=True, timeout=6)
        worker.join(timeout=9)
        assert not worker.is_alive() and not failures, (failures, result.stdout.decode(), result.stderr.decode())
        assert (result.returncode == 0) == expected_success, "Unexpected synthetic routing result"
        assert reference.split(":")[1].encode() in captured, "RPC stopped carrying the lazy DAT filename; review publication contract"
        assert len(captured) < 1024, "Unexpected expanded or unbounded routing request"
    os.unlink(endpoint)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, default=ROOT / "artifacts/core/Xray-macos-arm64-v8a.zip")
    args = parser.parse_args()
    specification = (ROOT / "src/EgressController.Runtime/XrayCoreReleaseManager.cs").read_text()
    pin = re.search(r'PinnedMacArm64ExecutableSha256 = "([a-f0-9]{64})"', specification)[1]
    with tempfile.TemporaryDirectory(prefix="egress-rpc-", dir="/tmp") as directory:
        root = Path(directory)
        with zipfile.ZipFile(args.archive) as archive:
            content = archive.read("xray")
        assert hashlib.sha256(content).hexdigest() == pin, "Pinned executable hash mismatch"
        executable = root / "xray"
        executable.write_bytes(content)
        executable.chmod(0o700)
        candidate = root / "candidate-assets"
        stable = root / "core-startup-assets"
        candidate.mkdir()
        stable.mkdir()
        # Minimal valid GeoSite protobuf: one CN site containing a full synthetic domain.
        def field(number, value):
            assert len(value) < 128
            return bytes((number * 8 + 2, len(value))) + value
        for index in range(2):
            dat = field(1, field(1, b"CN") + field(2, b"\x08\x03" + field(2, f"generation{index}.invalid".encode())))
            (candidate / "geosite.dat").write_bytes(dat)
            probe(executable, root, "ext:geosite.dat:cn", stable, False)
            name = hashlib.sha256(dat).hexdigest() + ".dat"
            (candidate / name).write_bytes(dat)
            (stable / name).write_bytes(dat)
            probe(executable, root, "ext:" + name + ":cn", stable, True)
        for item in sorted(stable.iterdir()):
            probe(executable, root, "ext:" + item.name + ":cn", stable, True)
    print("Pinned Xray CLI sends lazy DAT references, including immutable filenames; mock Unix RPC only, no core/TUN started.")


if __name__ == "__main__":
    main()
