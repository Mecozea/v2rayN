"""Offline behavioral tests of the embedded macOS proxy script with networksetup mocked."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

BASH = shutil.which("bash") if os.name != "nt" else r"C:\Program Files\Git\bin\bash.exe"
SCRIPT = Path(__file__).resolve().parents[2] / "v2rayN/ServiceLib/Sample/proxy_set_osx_sh"

@unittest.skipUnless(BASH and Path(BASH).exists(), "bash required")
class MacProxyTests(unittest.TestCase):
    def run_script(self, *args, fail=False, fail_list=False, fail_query=False):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "proxy.sh").write_text(SCRIPT.read_text().replace("/usr/sbin/networksetup", "bash ./networksetup"), encoding="utf-8", newline="\n")
            (root / "networksetup").write_text(r'''#!/bin/bash
case "$1" in
-listallnetworkservices) [[ "$FAIL_LIST" != 1 ]] || exit 7; printf 'An asterisk denotes a disabled service.\nWi-Fi\nUSB LAN\n*Disabled\n' ;;
-getwebproxy) [[ "$FAIL_QUERY" != 1 ]] || exit 7; printf 'Enabled: Yes\nServer: 127.0.0.1\nPort: 10808\n' ;;
-getsecurewebproxy) printf 'Enabled: Yes\nServer: 127.0.0.1\nPort: 9999\n' ;;
-getsocksfirewallproxy) printf 'Enabled: No\nServer: 127.0.0.1\nPort: 10808\n' ;;
*) [[ "$FAIL" != 1 ]] || exit 7
   printf '%s|' "$@" >> calls; printf '\n' >> calls ;;
esac
''', encoding="utf-8", newline="\n")
            result = subprocess.run([BASH, "proxy.sh", *args], cwd=root, env={**os.environ, "FAIL": "1" if fail else "0", "FAIL_LIST": "1" if fail_list else "0", "FAIL_QUERY": "1" if fail_query else "0"}, capture_output=True, text=True)
            calls = (root / "calls").read_text() if (root / "calls").exists() else ""
            return result.returncode, calls

    def test_owned_clear_preserves_other_endpoints_and_disabled_proxies(self):
        code, calls = self.run_script("clear-owned", "127.0.0.1", "10808")
        self.assertEqual(code, 0)
        self.assertEqual(calls.splitlines(), ["-setwebproxystate|Wi-Fi|off|", "-setwebproxystate|USB LAN|off|"])

    def test_set_keeps_service_names_and_uses_empty_bypass_token(self):
        code, calls = self.run_script("set", "127.0.0.1", "10808")
        self.assertEqual(code, 0)
        self.assertIn("-setproxybypassdomains|USB LAN|Empty|", calls)
        self.assertNotIn("Disabled", calls)
        self.assertEqual(len(calls.splitlines()), 8)

    def test_service_enumeration_failure_is_reported(self):
        code, calls = self.run_script("set", "127.0.0.1", "10808", fail_list=True)
        self.assertNotEqual(code, 0)
        self.assertEqual(calls, "")

    def test_ownership_query_failure_is_reported(self):
        code, calls = self.run_script("clear-owned", "127.0.0.1", "10808", fail_query=True)
        self.assertNotEqual(code, 0)
        self.assertEqual(calls, "")

    def test_permission_failure_is_reported(self):
        code, _ = self.run_script("set", "127.0.0.1", "10808", fail=True)
        self.assertNotEqual(code, 0)

if __name__ == "__main__":
    unittest.main()
