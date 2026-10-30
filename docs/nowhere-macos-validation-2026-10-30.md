# Apple Silicon implementation and local validation — 2026-10-30

Implemented macOS arm64 Nowhere support, trusted native tar.gz installation, executable permissions, pinned package/executable SHA-256, version validation, atomic core replacement and validated rollback. The shipped official Nowhere executable must retain its original bytes and signature.

The existing sing-box TUN frontend now has safer sudo argument handling, password EOF handling and graceful descendant shutdown. System proxy failures propagate; partial application is cleaned only for matching owned endpoints, with failed cleanup retried on stop. Custom proxy scripts do not have an ownership contract and are not blindly cleared.

GUI updates validate and stage before exiting. macOS uses a complete signed `.app` ZIP; the updater verifies the staged bundle with `codesign --verify --deep --strict`, swaps entire directories on the same volume and restores the previous bundle on activation failure. Obsolete old files disappear with the old bundle. Windows file updates use a replacement journal and reverse rollback, preserving separately managed cores. A failed rollback retains recovery files. This is exception rollback, not guaranteed recovery after power loss.

Local Windows validation:

- ServiceLib: **225/225 passed**, including actual Windows Vector lifecycle tests and the official Mac archive/hash test.
- WebAPI: **388/388 passed**.
- macOS proxy script offline behavior with mocked networksetup: **5/5 passed**.
- Apple Silicon self-contained Avalonia GUI and AmazTool: cross-published successfully.
- Actual macOS Nowhere/Xray/sing-box archives and default rules downloaded, hash-verified and staged successfully.
- Windows WPF build: passed with zero warnings/errors. YAML/JSON/Python/PowerShell/Bash syntax, UTF-8 docs and git diff checks passed.

The GitHub workflow builds Windows x64 ZIP and Apple Silicon DMG/complete-app ZIP, runs native tests and local Portal/Vector loopback before exposing artifacts, and publishes signed prerelease assets for `nowhere-vX.Y.Z` tags. Fork version tags remain intact in download URLs and compare by their numeric version. GUI update discovery points to Mecozea/v2rayN. Upstream's WinGet job is restricted to the upstream repository.

## Native Apple Silicon validation (2026-10-30)

This continuation ran on an arm64 MacBook Pro, macOS 26.2 (25C56), Darwin 25.2.0, using .NET SDK 10.0.403 installed in a temporary directory. Application version: 7.25.6. No Actions workflow or public release was triggered.

- Native ServiceLib: **225/225 passed**, including the official Nowhere process lifecycle (100 cycles), Xray integration and, in the full-core run, sing-box generated TUN configuration check. The latter is an offline check, not an active utun session.
- Native WebAPI: **388/388 passed** after fixing macOS lock ownership reads with native flock/open and Darwin-specific flags. The retry test window was increased from 120 ms to 2 s to avoid scheduling-dependent failures.
- System proxy mocked-networksetup tests: **5/5 passed**. No real system proxy settings were changed.
- Official Nowhere 3.2.0, Xray 26.10.29 and sing-box 1.14.4 executed natively. Final Nowhere executable SHA-256 remains `774c5126ae706f9c1569ba11e42e1d32cac1f807fb28fd72e5c370860c1538f9`; its original bytes/signature were preserved.
- Direct Vector and Xray -> Vector -> local Portal: TLS/QUIC × Morph 0/1 × TCP/UDP echo payloads passed. Wrong pin/key/Morph and untrusted self-signed certificate were rejected for both carriers. Trust was an ephemeral local certificate pin; this establishes neither public connectivity nor the CA trust path. Sanitized results are saved alongside this report as `nowhere-macos-{direct,xray}-loopback-2026-10-30.json`.
- Native self-contained GUI/updater publication, ad-hoc App signing and `codesign --verify --deep --strict`: passed.
- Actual DMG creation and `hdiutil verify`: passed. Read-only mount, copy to a temporary directory containing Chinese characters/spaces, detach, and verification of the copied App: passed. Complete ZIP extraction and strict App signature verification: passed. This did not launch the GUI or install into Applications.

### Build defects fixed

The original bundle placed rules/manifests/icons under Contents/MacOS. Native codesign treated those files as unsigned code and rejected the bundle. They now live under Contents/Resources, while executables/native libraries stay under MacOS. Startup locates the bundled core directory and user-data marker through the matching Resources path. The native CI core paths and ZIP executable checks follow that layout.

Downloads now have bounded connection/total timeouts, and an optional external cache remains subject to the same pinned hashes. This machine timed out connecting directly to the sing-box release and failed TLS for some raw rule files; the exact assets were retrieved through GitHub's official release/contents API and verified against the unchanged manifest. The installed Homebrew Python 3.14 has a broken expat linkage; plist generation uses the working system Python, while staging retains modern Python for safe tar extraction.

### Local artifacts

Under repository `dist/` (ignored generated output):

| Artifact | SHA-256 |
| --- | --- |
| v2rayN-macos-arm64.dmg | 106b4a515ec610eacbddf41908ea1f3b25d13fb41697d5ae9987442c7c9f517d |
| v2rayN-macos-arm64.zip | 6c7be283b199f5721158868c7f16614e496f9cd5386f37aad403908ae69d87df |

`dist/SHA256SUMS-macos-arm64.txt` records both hashes; native test TRX reports are in `dist/test-results/` and `dist/validation/`.

## Remaining verification

GUI startup/import/persistence and installation into Applications remain unverified; this machine already contains personal v2rayN data, which was not replaced for an isolated test. Public Portal/CA trust, real networksetup ownership cleanup, privileged TUN/utun routing, GUI core/app update and rollback, network/sleep transitions, original-protocol connectivity and 24-hour stability remain pending. Packages are ad-hoc signed, not Developer ID signed or notarized. See [acceptance checklist](nowhere-macos-acceptance.md). Native build and local loopback success do not establish full platform acceptance.

## 7.25.7 follow-up: user startup failure and unified updates

The user supplied a startup log showing `dns.rules[0].preferred_by: unknown field`. Native version probes confirmed the persistent user core is sing-box **1.13.14**, while the signed App contains **1.14.4**. Startup previously preserved and then preferred the old user binary. Mac App core resolution now preserves that file but selects the bundled stable sing-box when the installed core cannot support the generated configuration (minimum 1.14.0); a compatible installed core stays preferred. Missing/incompatible candidates produce an actionable error. SOCKS readiness errors now identify the Nowhere frontend separately from Vector.

Nowhere is listed in the shared Check Updates window on supported platforms, with selection persistence, Check Only and Check and Update. Check Only verifies the installed executable against the application's trusted version manifest; missing/corrupt/older files are eligible for verified installation. Healthy cores do not trigger a download or reconnect when Nowhere is the only selected item. Updates honor the proxy toggle and retain the existing verified installer/lifecycle path. Separate install/repair and rollback menu items and bindings were removed in both Avalonia and WPF; rollback APIs remain available. New upstream releases still require a reviewed trusted manifest update in the application.

App discovery and download metadata are verified to use `https://github.com/Mecozea/v2rayN` and its corresponding GitHub API. Invalid or missing release tags now fail parsing instead of appearing as an available 0.0.0 update. Fork `nowhere-vX.Y.Z` tags remain intact in asset URLs.

- Final native WebAPI: **388/388 passed** after shared version parsing/update changes.
- Final native ServiceLib: **232/232 passed**, including actual 1.13.14 -> bundled 1.14.4 selection without changing the old file, compatible-core preference, missing-core handling, official Nowhere check-only cases, update-list support, fork URLs and invalid release responses.
- Native GUI/updater 7.25.7 published; strict App signing and DMG verification passed. Mounted DMG contents and extracted complete ZIP signatures passed.
- GUI launch/import/node list are now supported by user screenshots of the installed 7.25.6 App, rather than an independent automated GUI run. They do not establish privileged TUN acceptance. The new 7.25.7 update window still needs user interaction verification.
- Existing `/Applications/v2rayN.app`, personal configuration and user core files were not replaced or stopped during this fix.

New local artifacts replace the top-level dist outputs; 7.25.6 outputs are retained in `dist/previous-7.25.6/`:

| Artifact | SHA-256 |
| --- | --- |
| v2rayN-macos-arm64.dmg (7.25.7) | cc4a2dc1ea3bbdcd9006984321f3fd5469ded570ad71ff63afa536c8276fdffc |
| v2rayN-macos-arm64.zip (7.25.7) | 45ad838466f68d9b01285f4a4a628de736a36a0534f807ed3b92185b245388c0 |

No public release or GitHub push was performed. The 7.25.7 package remains ad-hoc signed and not notarized.
