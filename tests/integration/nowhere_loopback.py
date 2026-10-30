"""Real Portal/Vector loopback smoke test. Python stdlib; no system proxy changes.

Run: python tests/integration/nowhere_loopback.py --exe <nowhere.exe>
Only terminates processes created by this test. Keys are ephemeral test data.
"""
import argparse
import concurrent.futures
import datetime
import hashlib
import json
import os
import platform
import re
import socket
import struct
import subprocess
import tempfile
import time
from pathlib import Path


def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def exact(sock, length):
    result = b""
    while len(result) < length:
        part = sock.recv(length - len(result))
        if not part:
            raise RuntimeError("Unexpected SOCKS EOF")
        result += part
    return result


def socks(local_port, command, target_port):
    sock = socket.create_connection(("127.0.0.1", local_port), timeout=10)
    sock.sendall(b"\x05\x01\x00")
    if exact(sock, 2) != b"\x05\x00":
        raise RuntimeError("SOCKS negotiation failed")
    sock.sendall(bytes([5, command, 0, 1]) + socket.inet_aton("127.0.0.1") + struct.pack("!H", target_port))
    reply = exact(sock, 4)
    if reply[:2] != b"\x05\x00":
        sock.close()
        raise RuntimeError("SOCKS request failed")
    family = reply[3]
    address = socket.inet_ntop(socket.AF_INET if family == 1 else socket.AF_INET6, exact(sock, 4 if family == 1 else 16))
    bound_port = struct.unpack("!H", exact(sock, 2))[0]
    return sock, (address, bound_port)


def tcp_echo(listener):
    connection, _ = listener.accept()
    with connection:
        connection.settimeout(10)
        data = connection.recv(4096)
        connection.sendall(data)


def udp_echo(listener):
    data, remote = listener.recvfrom(4096)
    listener.sendto(data, remote)


def wait_log(process, log, pattern):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("Nowhere exited before ready")
        text = log.read_text(encoding="utf-8", errors="replace")
        match = re.search(pattern, text)
        if match:
            return match
        time.sleep(0.05)
    raise TimeoutError("Nowhere readiness timeout")


def wait_socks(process, local_port):
    deadline = time.monotonic() + 15
    while True:
        try:
            with socket.create_connection(("127.0.0.1", local_port), timeout=0.2) as probe:
                probe.sendall(b"\x05\x01\x00")
                if exact(probe, 2) == b"\x05\x00":
                    return
        except (OSError, RuntimeError):
            pass
        if process.poll() is not None or time.monotonic() > deadline:
            raise RuntimeError("SOCKS readiness failed")
        time.sleep(0.05)


def run(executable, carrier, morph, directory, xray=None, failures=False):
    key = "0123456789abcdef0123456789abcdef"
    remote, local = port(), port()
    children = []
    streams = []
    try:
        def launch(url, name):
            log = directory / name
            stream = log.open("wb")
            streams.append(stream)
            environment = dict(os.environ, NOW_HANDSHAKE_TIMEOUT="1s", NOW_FLOW_SETUP_TIMEOUT="2s")
            process = subprocess.Popen([executable, url], stdout=stream, stderr=subprocess.STDOUT, env=environment,
                                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            children.append(process)
            return process, log

        # Both listeners share one generated certificate; trust its local process log.
        portal, portal_log = launch(f"portal://{key}@127.0.0.1:{remote}?morph={morph}", "portal.log")
        pin = wait_log(portal, portal_log, r"tls certificate fingerprint: ([0-9a-f]{64})").group(1)
        vector, _ = launch(f"vector://{key}@127.0.0.1/{carrier}:{remote}?in=127.0.0.1:{local}&morph={morph}&pin={pin}", "vector.log")
        wait_socks(vector, local)

        if xray:
            frontend_port = port()
            config = {
                "log": {"loglevel": "warning"},
                "inbounds": [{"listen": "127.0.0.1", "port": frontend_port, "protocol": "socks", "settings": {"auth": "noauth", "udp": True}}],
                "outbounds": [{"protocol": "socks", "settings": {"servers": [{"address": "127.0.0.1", "port": local}]}}],
            }
            config_path = directory / "xray.json"
            config_path.write_text(json.dumps(config), encoding="utf-8")
            stream = (directory / "xray.log").open("wb")
            streams.append(stream)
            front = subprocess.Popen([xray, "run", "-c", str(config_path)], stdout=stream, stderr=subprocess.STDOUT,
                                     creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            children.append(front)
            wait_socks(front, frontend_port)
            local = frontend_port

        payload = b"v2rayN Nowhere real loopback payload " * 16
        with socket.socket() as target:
            target.settimeout(15)
            target.bind(("127.0.0.1", 0))
            target.listen()
            with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
                echo = executor.submit(tcp_echo, target)
                connection, _ = socks(local, 1, target.getsockname()[1])
                with connection:
                    connection.sendall(payload)
                    assert exact(connection, len(payload)) == payload
                echo.result(timeout=15)

        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as target:
            target.settimeout(15)
            target.bind(("127.0.0.1", 0))
            with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
                echo = executor.submit(udp_echo, target)
                control, relay = socks(local, 3, 0)
                with control, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                    client.settimeout(15)
                    packet = b"\x00\x00\x00\x01" + socket.inet_aton("127.0.0.1") + struct.pack("!H", target.getsockname()[1]) + payload
                    client.sendto(packet, relay)
                    reply, _ = client.recvfrom(4096)
                    assert reply[3] == 1 and reply[10:] == payload
                echo.result(timeout=15)
        rejected = []
        if failures and morph == 1:
            for fault in ("wrong-pin", "wrong-key", "wrong-morph", "untrusted-self-signed"):
                failed_port = port()
                test_key = "f" * 32 if fault == "wrong-key" else key
                test_pin = "0" * 64 if fault == "wrong-pin" else pin
                test_morph = 0 if fault == "wrong-morph" else morph
                trust = "" if fault == "untrusted-self-signed" else f"&pin={test_pin}"
                bad, _ = launch(f"vector://{test_key}@127.0.0.1/{carrier}:{remote}?in=127.0.0.1:{failed_port}&morph={test_morph}{trust}", f"{fault}.log")
                wait_socks(bad, failed_port)
                with socket.socket() as target:
                    target.bind(("127.0.0.1", 0))
                    target.listen()
                    try:
                        connected, _ = socks(failed_port, 1, target.getsockname()[1])
                    except RuntimeError:
                        rejected.append(fault)
                    else:
                        connected.close()
                        raise AssertionError(f"{fault} unexpectedly allowed a business connection")
        return {"carrier": carrier, "morph": morph, "tcp": "pass", "udp": "pass", "trust": "local-generated-certificate-pin", "frontend": "xray" if xray else "none", "rejected": rejected}
    finally:
        for child in reversed(children):
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait(timeout=5)
        for stream in streams:
            stream.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--output")
    parser.add_argument("--xray", help="Also verify the SOCKS frontend bridge with an Xray executable")
    parser.add_argument("--failures", action="store_true", help="Verify bad key/pin/Morph and untrusted certificates cannot establish TCP business flows")
    args = parser.parse_args()
    results = []
    with tempfile.TemporaryDirectory(prefix="v2rayn-nowhere-") as temp:
        for carrier in ("tcp", "udp"):
            for morph in (0, 1):
                directory = Path(temp) / f"{carrier}-{morph}"
                directory.mkdir()
                results.append(run(str(Path(args.exe).resolve()), carrier, morph, directory,
                                   str(Path(args.xray).resolve()) if args.xray else None, args.failures))
    report = json.dumps({
        "tested_at": datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).isoformat(),
        "platform": platform.platform(),
        "nowhere_version": subprocess.check_output([args.exe, "--version"], text=True).strip(),
        "nowhere_sha256": hashlib.sha256(Path(args.exe).read_bytes()).hexdigest(),
        "frontend_version": subprocess.check_output([args.xray, "version"], text=True).splitlines()[0] if args.xray else None,
        "scope": "local loopback only; no system proxy or TUN changes",
        "results": results,
    }, indent=2)
    print(report)
    if args.output:
        Path(args.output).write_text(report + "\n", encoding="utf-8")
