#!/usr/bin/env python3
"""Verify that the Meta Remote Agent Server access token was rotated. Prints PASS/FAIL lines only.

No token value, prefix or hash is ever printed, logged, written to disk or placed in process arguments.

Run it only after both of these:
  1. Unity Editor -> Meta XR AI Tools (Agent Bridge) settings -> Remote Agent Server -> Access Token ->
     Regenerate -> confirm.
  2. Start the Remote Agent Server (same panel) so that it listens on 127.0.0.1:<port>, default 48735.

Where the tokens come from:
  - Old (exposed) candidate: app-unity/Assets/Resources/DevAgentSettings.asset, `accessToken`. This asset is
    git-ignored. Meta's build processor restores it to its previous value after each build, so regenerating in the
    Editor does not rewrite it.
  - Current server token: EditorPrefs key `Meta.XR.SDK.*.RemoteServer_AccessToken*` in com.unity3d.UnityEditor5.x.

Harmless authenticated operation: GET /agentbridge/messages. This is the read-only event stream; the connection is
closed as soon as the status line arrives. /status is unauthenticated by design, so it proves nothing.
Expected: old token -> 401, current token -> 200.

Exit 0 only if every check passes.
"""

from __future__ import annotations

import http.client
import plistlib
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSET = ROOT / "app-unity/Assets/Resources/DevAgentSettings.asset"
PREFS_DOMAIN = "com.unity3d.UnityEditor5.x"
KEY_RE = re.compile(r"^Meta\.XR\.SDK\..*RemoteServer_AccessToken(_h\d+)?$")
PORT_KEY_RE = re.compile(r"^Meta\.XR\.SDK\..*RemoteServer_Port(_h\d+)?$")


def _old_token() -> str | None:
    if not ASSET.is_file():
        return None
    match = re.search(r"^\s*accessToken:\s*(\S*)\s*$", ASSET.read_text(encoding="utf-8"), re.MULTILINE)
    return match.group(1) if match and match.group(1) else None


def _prefs() -> dict[str, object]:
    exported = subprocess.run(  # noqa: S603 - fixed argv, no shell
        ["/usr/bin/defaults", "export", PREFS_DOMAIN, "-"], capture_output=True, check=False
    )
    if exported.returncode != 0:
        return {}
    data = plistlib.loads(exported.stdout)
    return data if isinstance(data, dict) else {}


def _current_token(prefs: dict[str, object]) -> str | None:
    for key, value in prefs.items():
        if KEY_RE.match(key):
            if isinstance(value, bytes):  # Unity stores EditorPrefs strings as NUL-terminated UTF-8 data
                value = value.rstrip(b"\x00").decode("utf-8", "replace")
            if isinstance(value, str) and value:
                return value
    return None


def _port(prefs: dict[str, object]) -> int:
    for key, value in prefs.items():
        if PORT_KEY_RE.match(key) and isinstance(value, int) and 0 < value < 65536:
            return value
    return 48735


def _status_with(token: str | None, port: int) -> int | None:
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=3)
    try:
        headers = {"Authorization": f"Bearer {token}"} if token else {}
        conn.request("GET", "/agentbridge/messages", headers=headers)
        return conn.getresponse().status
    except OSError:
        return None
    finally:
        conn.close()


def main() -> int:
    checks: list[tuple[str, bool]] = []
    old = _old_token()
    prefs = _prefs()
    new = _current_token(prefs)
    port = _port(prefs)

    checks.append(("old token candidate found in local DevAgentSettings asset", old is not None))
    checks.append(("current server token found in EditorPrefs", new is not None))
    checks.append(("current token differs from old token", old is not None and new is not None and old != new))

    reachable = _status_with(None, port)
    checks.append((f"agent server reachable on 127.0.0.1:{port}", reachable is not None))
    if reachable is not None:
        checks.append(("request without token rejected (401)", reachable == 401))
        if old is not None:
            checks.append(("old token rejected (401)", _status_with(old, port) == 401))
        if new is not None:
            checks.append(("current token accepted (200)", _status_with(new, port) == 200))

    for label, ok in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {label}")
    passed = all(ok for _, ok in checks) and reachable is not None
    print("RESULT:", "PASS" if passed else "FAIL")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
