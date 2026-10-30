"""Read-only profile snapshot -> isolated Vector -> real HTTPS/download/UDP DNS.

No system proxy/TUN changes; never export shared keys or complete launch URLs.
"""
import argparse
import concurrent.futures
import datetime
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import struct
import subprocess
import tempfile
import time
from urllib.parse import urlencode, urlunsplit

from nowhere_loopback import port, socks, wait_socks


def http_probe(local_port, url, output, expected_status, expected_size=None):
    start = time.monotonic()
    run = subprocess.run([
        "curl.exe", "--silent", "--show-error", "--fail", "--socks5-hostname",
        f"127.0.0.1:{local_port}", "--noproxy", "", "--connect-timeout", "12",
        "--max-time", "30", "--output", str(output), "--write-out",
        "%{http_code},%{size_download},%{time_total}", url,
    ], capture_output=True, text=True, timeout=35,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    fields = run.stdout.strip().split(",")
    status = int(fields[0]) if len(fields) == 3 and fields[0].isdigit() else 0
    size = int(fields[1]) if len(fields) == 3 and fields[1].isdigit() else 0
    return {
        "passed": run.returncode == 0 and status == expected_status and (expected_size is None or size == expected_size),
        "curl_exit": run.returncode,
        "http_status": status,
        "bytes": size,
        "elapsed_seconds": round(time.monotonic() - start, 3),
        # curl sees only the local proxy and public target, never the Vector service URL.
        "error": run.stderr.strip()[:300] if run.returncode else None,
    }


def udp_dns_probe(local_port):
    started = time.monotonic()
    control = None
    try:
        control, relay = socks(local_port, 3, 0)
        transaction = secrets.token_bytes(2)
        query = transaction + b"\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00" + b"\x07example\x03com\x00\x00\x01\x00\x01"
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
            client.settimeout(12)
            client.sendto(b"\x00\x00\x00\x01" + socket.inet_aton("1.1.1.1") + struct.pack("!H", 53) + query, relay)
            packet, _ = client.recvfrom(8192)
        if len(packet) < 4 or packet[:3] != b"\x00\x00\x00":
            raise ValueError("Malformed SOCKS UDP reply")
        offset = {1: 10, 4: 22}.get(packet[3])
        if packet[3] == 3:
            offset = 7 + packet[4]
        if offset is None:
            raise ValueError("Unsupported SOCKS UDP address")
        response = packet[offset:]
        valid = len(response) >= 12 and response[:2] == transaction and bool(response[2] & 0x80)
        rcode = response[3] & 15 if valid else None
        answers = struct.unpack("!H", response[6:8])[0] if valid else 0
        return {"passed": valid and rcode == 0 and answers > 0, "rcode": rcode,
                "answers": answers, "elapsed_seconds": round(time.monotonic() - started, 3)}
    except (OSError, RuntimeError, ValueError) as error:
        return {"passed": False, "error_type": type(error).__name__,
                "elapsed_seconds": round(time.monotonic() - started, 3)}
    finally:
        if control:
            control.close()


def test_node(node, executable, xray=None):
    started = time.monotonic()
    extra = json.loads(node.get("ProtoExtra") or "{}")
    carrier = extra.get("NowhereCarrier") or "tcp"
    result = {"host": node["Address"], "port": node["Port"], "carrier": carrier, "frontend": "xray" if xray else "none",
              "trust": "pin" if extra.get("NowherePin") else "system-ca"}
    local_port = port()
    host = node["Address"]
    host = f"[{host}]" if ":" in host else host
    query = {"in": f"127.0.0.1:{local_port}", "morph": "0" if extra.get("NowhereMorph") is False else "1"}
    if node.get("Sni"):
        query["sni"] = node["Sni"]
    if extra.get("NowherePin"):
        query["pin"] = extra["NowherePin"]
    url = urlunsplit(("vector", node["Password"] + "@" + host,
                     f"/{carrier}:{node['Port']}", urlencode(query), ""))
    process = None
    frontend = None
    try:
        with tempfile.TemporaryDirectory(prefix="nowhere-remote-") as directory:
            directory = Path(directory)
            with (directory / "vector.log").open("wb") as log:
                environment = dict(os.environ, NOW_FLOW_SETUP_TIMEOUT="12s")
                process = subprocess.Popen([executable, url], stdout=log, stderr=subprocess.STDOUT,
                                           env=environment, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                wait_socks(process, local_port)
                result["local_socks_ready"] = True
                if xray:
                    frontend_port = port()
                    config = {"log": {"loglevel": "warning"},
                              "inbounds": [{"listen": "127.0.0.1", "port": frontend_port, "protocol": "socks", "settings": {"auth": "noauth", "udp": True}}],
                              "outbounds": [{"protocol": "socks", "settings": {"servers": [{"address": "127.0.0.1", "port": local_port}]}}]}
                    config_path = directory / "frontend.json"
                    config_path.write_text(json.dumps(config), encoding="utf-8")
                    frontend = subprocess.Popen([xray, "run", "-c", str(config_path)], stdout=log, stderr=subprocess.STDOUT,
                                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                    wait_socks(frontend, frontend_port)
                    local_port = frontend_port
                result["https"] = http_probe(local_port, "https://www.gstatic.com/generate_204", directory / "probe", 204)
                if result["https"]["passed"]:
                    result["download"] = http_probe(local_port, "https://speed.cloudflare.com/__down?bytes=1048576", directory / "download", 200, 1048576)
                result["udp_dns"] = udp_dns_probe(local_port)
                if frontend:
                    frontend.terminate()
                    frontend.wait(timeout=5)
                process.terminate()
                process.wait(timeout=5)
                result["owned_process_cleaned"] = process.poll() is not None and (frontend is None or frontend.poll() is not None)
    except Exception as error:
        result["error_type"] = type(error).__name__
    finally:
        for owned in (frontend, process):
            if owned and owned.poll() is None:
                owned.kill()
                owned.wait(timeout=5)
        result["elapsed_seconds"] = round(time.monotonic() - started, 3)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", required=True)
    parser.add_argument("--exe", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--concurrency", type=int, default=3)
    parser.add_argument("--xray", help="Optional Xray frontend to validate the complete local SOCKS bridge")
    args = parser.parse_args()
    start = time.monotonic()
    database = sqlite3.connect(Path(args.database).resolve().as_uri() + "?mode=ro", uri=True)
    database.row_factory = sqlite3.Row
    profiles = [dict(row) for row in database.execute("SELECT Address,Port,Password,Sni,ProtoExtra FROM ProfileItem WHERE ConfigType=15")]
    database.close()
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, min(4, args.concurrency))) as executor:
        futures = [executor.submit(test_node, node, str(Path(args.exe).resolve()), str(Path(args.xray).resolve()) if args.xray else None) for node in profiles]
        for future in concurrent.futures.as_completed(futures):
            result = future.result()
            results.append(result)
            print(json.dumps(result, ensure_ascii=False), flush=True)
    report = {"tested_at": datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).isoformat(),
              "scope": "Isolated frontend/Vector HTTPS, 1 MiB download, UDP DNS. No system proxy or TUN modification.",
              "elapsed_seconds": round(time.monotonic() - start, 3),
              "executable_sha256": hashlib.sha256(Path(args.exe).read_bytes()).hexdigest(),
              "results": results}
    Path(args.output).write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("Total seconds:", report["elapsed_seconds"])
