#!/usr/bin/env python3
"""Scan files, APKs (zip entries) and git objects for development secrets. Prints counts only, never values.

Looked for:
  - devagent_token:       the local Remote Agent Server token, read in memory from the git-ignored
                          app-unity/Assets/Resources/DevAgentSettings.asset;
  - devagent_server:      that asset's serverAddress (this Mac's LAN address);
  - player_connection_ip: Unity's development player/profiler connection key in boot.config
                          (`player-connection-ip=`), reported per APK as present or absent; its value is not printed;
  - wait_for_connection / development flags in boot.config (key names only).

Usage: scan_build_secrets.py [--git] PATH...    Exit 1 if any secret value is found.
"""

from __future__ import annotations

import re
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSET = ROOT / "app-unity/Assets/Resources/DevAgentSettings.asset"
BOOT_KEYS = ("player-connection-ip", "player-connection-mode", "player-connection-wait-timeout", "wait-for-managed-debugger")


def _secrets() -> dict[str, bytes]:
    found: dict[str, bytes] = {}
    if ASSET.is_file():
        text = ASSET.read_text(encoding="utf-8")
        for key, label in (("accessToken", "devagent_token"), ("serverAddress", "devagent_server")):
            m = re.search(rf"^\s*{key}:\s*(\S+)\s*$", text, re.MULTILINE)
            if m and len(m.group(1)) >= 6:
                found[label] = m.group(1).encode()
    return found


def _count(blob: bytes, secrets: dict[str, bytes]) -> dict[str, int]:
    return {k: blob.count(v) for k, v in secrets.items()}


def main(argv: list[str]) -> int:
    git = "--git" in argv
    paths = [Path(a) for a in argv if a != "--git"]
    secrets = _secrets()
    print(f"secret candidates loaded: {sorted(secrets)} (values not shown)")
    hits = 0
    for path in paths:
        if path.suffix == ".apk":
            totals = dict.fromkeys(secrets, 0)
            boot: list[str] = []
            with zipfile.ZipFile(path) as apk:
                for info in apk.infolist():
                    data = apk.read(info)
                    for k, n in _count(data, secrets).items():
                        totals[k] += n
                    if info.filename.endswith("boot.config"):
                        keys = {line.split("=", 1)[0] for line in data.decode("utf-8", "replace").splitlines() if "=" in line}
                        boot = sorted(k for k in BOOT_KEYS if k in keys)
            print(f"{path.name}: {totals}  boot.config dev-connection keys present: {boot or 'none'}")
            hits += sum(totals.values())
        elif path.is_dir():
            totals = dict.fromkeys(secrets, 0)
            for f in path.rglob("*"):
                if f.is_file() and f.resolve() != ASSET.resolve() and f.stat().st_size < 50_000_000:
                    for k, n in _count(f.read_bytes(), secrets).items():
                        totals[k] += n
            print(f"{path}: {totals}")
            hits += sum(totals.values())
    if git:
        objects = subprocess.run(  # noqa: S603, S607 - fixed argv
            ["git", "cat-file", "--batch-all-objects", "--batch"], cwd=ROOT, capture_output=True, check=True
        ).stdout
        totals = _count(objects, secrets)
        print(f"git objects (all, incl. unreachable): {totals}")
        hits += sum(totals.values())
    print("RESULT:", "FAIL" if hits else "PASS", f"({hits} secret-value hits)")
    return 1 if hits else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
