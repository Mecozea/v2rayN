#!/bin/bash
# Run on an Apple Silicon Mac / macos-14 runner. No mutable core-bin downloads.
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:-0.0.0}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version must be X.Y.Z"; exit 1; }
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { echo "Requires Apple Silicon macOS"; exit 1; }
[[ ! -e dist/macos-arm64 ]] || { echo "dist/macos-arm64 must not exist"; exit 1; }
root="$PWD"
out="$root/dist/macos-arm64"
app="$out/v2rayN.app"
mac="$app/Contents/MacOS"
cache="${NOWHERE_MACOS_CACHE:-$out/cache}"
mkdir -p "$mac" "$app/Contents/Resources" "$cache"
dotnet publish v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r osx-arm64 --self-contained true -p:Version="$version" -o "$mac"
dotnet publish v2rayN/AmazTool/AmazTool.csproj -c Release -r osx-arm64 --self-contained true -p:PublishTrimmed=true -p:Version="$version" -o "$mac"
python3 scripts/stage-nowhere-macos.py "$mac" "$cache"
resources="$app/Contents/Resources"
cp "$mac/v2rayN.icns" "$resources/AppIcon.icns"
# MacOS contains code only; rules, manifests and icons are sealed resources.
mv "$mac/bin" "$mac/LICENSE" "$mac/nowhere-macos-arm64-assets.json" "$resources/"
mv "$mac/v2rayN.icns" "$mac/v2rayN.png" "$resources/"
printf '%s\n' 'Keep configuration in the user data directory.' > "$resources/NotStoreConfigHere.txt"
/usr/bin/python3 - "$app/Contents/Info.plist" "$version" <<'PY'
import plistlib,sys
with open(sys.argv[1], 'wb') as f:
    plistlib.dump({'CFBundleDevelopmentRegion':'en','CFBundleDisplayName':'v2rayN Nowhere',
    'CFBundleExecutable':'v2rayN','CFBundleIconFile':'AppIcon','CFBundleIdentifier':'com.mecozea.v2rayN',
    'CFBundleName':'v2rayN','CFBundlePackageType':'APPL','CFBundleShortVersionString':sys.argv[2],
    'CFBundleVersion':sys.argv[2],'LSMinimumSystemVersion':'13.6','NSHighResolutionCapable':True}, f)
PY
# Sign individual Mach-O files before the enclosing bundle. Ad-hoc signing is
# required for Apple Silicon execution; this is NOT Developer ID/notarization.
while IFS= read -r -d '' file; do
    # Official Nowhere bytes are pinned by the runtime. Preserve its signature.
    [[ "$file" == "$resources/bin/nowhere/nowhere" ]] && continue
    if /usr/bin/file -b "$file" | grep -q 'Mach-O'; then
        chmod u+x "$file"
        codesign --force --sign - "$file"
    fi
done < <(find "$app/Contents" -type f -print0)
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"
"$resources/bin/nowhere/nowhere" --version
"$resources/bin/xray/xray" version
"$resources/bin/sing_box/sing-box" version
# Replace the complete app bundle on update; changing only Contents/MacOS
# would invalidate the enclosing signature and leave stale Info.plist metadata.
codesign --verify --deep --strict "$app"
ditto -c -k --norsrc --noextattr --keepParent "$app" dist/v2rayN-macos-arm64.zip
python3 - <<'PY'
import stat, zipfile
with zipfile.ZipFile("dist/v2rayN-macos-arm64.zip") as archive:
    for item in archive.infolist():
        parts = item.filename.rstrip("/").split("/")
        assert parts[0] == "v2rayN.app" and ".." not in parts, item.filename
        assert not stat.S_ISLNK(item.external_attr >> 16), item.filename
    for name in ("MacOS/v2rayN", "MacOS/AmazTool", "Resources/bin/nowhere/nowhere", "Resources/bin/xray/xray", "Resources/bin/sing_box/sing-box"):
        item = archive.getinfo("v2rayN.app/Contents/" + name)
        assert (item.external_attr >> 16) & 0o111, "Executable mode missing: " + name
PY
mkdir "$out/dmg"
cp -R "$app" "$out/dmg/"
ln -s /Applications "$out/dmg/Applications"
hdiutil create -volname 'v2rayN Nowhere' -srcfolder "$out/dmg" -ov -format UDZO dist/v2rayN-macos-arm64.dmg
hdiutil verify dist/v2rayN-macos-arm64.dmg
(cd dist && shasum -a 256 v2rayN-macos-arm64.zip v2rayN-macos-arm64.dmg > SHA256SUMS-macos-arm64.txt)
