"""Stage only SHA-256 pinned macOS cores and default routing rules."""
import hashlib, json, pathlib, shutil, subprocess, sys, urllib.request

root = pathlib.Path(__file__).resolve().parent
manifest = json.loads((root / "nowhere-macos-arm64-assets.json").read_text())
output, cache = map(pathlib.Path, sys.argv[1:])
cache.mkdir(parents=True, exist_ok=True)

def download(entry, name):
    path = cache / name
    if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
        subprocess.run(["curl", "--fail", "--location", "--retry", "3", "--connect-timeout", "20", "--max-time", "300", "--output", str(path), entry["url"]], check=True)
    if hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
        raise RuntimeError("SHA-256 mismatch: " + name)
    return path

for core in manifest["cores"]:
    archive = download(core, pathlib.PurePosixPath(core["url"]).name)
    stage = cache / core["name"]
    stage.mkdir(exist_ok=True)
    # Pinned official archives; shutil's data filter rejects traversal/symlinks.
    shutil.unpack_archive(str(archive), str(stage), filter="data") if not archive.name.endswith(".zip") else shutil.unpack_archive(str(archive), str(stage))
    target = output / "bin" / core["name"]
    target.mkdir(parents=True, exist_ok=True)
    executable = {"nowhere":"nowhere", "xray":"xray", "sing_box":"sing-box"}[core["name"]]
    found = list(stage.rglob(executable))
    if len(found) != 1:
        raise RuntimeError("Expected exactly one " + executable)
    # Preserve licenses, geo data and native dependencies beside the executable.
    for item in found[0].parent.iterdir():
        if item.is_file():
            shutil.copy2(item, target / item.name)
    (target / executable).chmod(0o755)
    if core["name"] == "xray":
        for name in ("geoip.dat", "geosite.dat"):
            if (target / name).exists():
                shutil.move(str(target / name), str(output / "bin" / name))
for rule in manifest["rules"]:
    target = output / "bin" / "srss" / rule["name"]
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(download(rule, rule["name"]), target)
shutil.copy2(root / "nowhere-macos-arm64-assets.json", output)
shutil.copy2(root.parent / "LICENSE", output)
shutil.copy2(root.parent / "docs/licenses/Nowhere-LICENSE", output / "bin/nowhere/LICENSE")
