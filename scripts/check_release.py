"""Report gate status from docs/progress/phase-state.json (technical-spec S19.2, phases.md S5).

    python scripts/check_release.py --profile r0           # every recorded gate across phases
    python scripts/check_release.py --phase 0              # gates for one phase

Exit 0 only if every required gate in scope has status `pass` and, where an evidence path is recorded and present
locally, its SHA-256 matches. Missing gates are failures: an absent record is never treated as a pass. This script
reads evidence; it never creates it. Standard library only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
STATE = ROOT / "docs" / "progress" / "phase-state.json"
GATE_STATUSES = {"not_run", "pass", "fail", "blocked", "not_applicable"}
GATE_FIELDS = {
    "id", "phase", "required", "status", "command_or_procedure", "environment",
    "evidence_path", "evidence_sha256", "notes",
}  # fmt: skip
# Required gate IDs per phase. A phase cannot pass with any of these missing from the state file.
REQUIRED_GATES: dict[int, set[str]] = {
    0: {
        "P0-toolchain-recorded",
        "P0-unity-imports-compiles",
        "P0-simulator-hand-sample",
        "P0-android-apk",
        "P0-contract-checks",
        "P0-clean-checkout-repeat",
        "P0-second-contributor-repeat",
        "P0-worker-isolation",
        "P0-backend-baseline",
        "P0-model-access-deployment",
    },
    1: {
        "P1-core-command-tests",
        "P1-input-to-command-playmode",
        "P1-manual-simulator-walkthrough",
        "P1-android-build-healthy",
        "P1-drag-loss-automated",
        "P1-drag-loss-device",
        "P1-backend-deployment-record",
    },
    2: {
        "P2-fixture-expectations",
        "P2-thin-obstacle-boundary-tests",
        "P2-frame-rate-independence",
        "P2-evidence-excludes-envelope",
        "P2-cross-runtime-canonical-hash",
        "P2-geometry-mutation-check",
        "P2-edit-replay-explain-automated",
        "P2-manual-simulator-walkthrough",
        "P2-android-build-healthy",
        "P2-xrsim-regression",
        "P2-config-verify",
        "P2-backend-intent-extraction",
        "P2-devagent-token-rotated",
    },
    3: {
        "P3-interpretation-cases",
        "P3-proposal-validation",
        "P3-job-api-isolation-cancellation",
        "P3-late-result-rejection",
        "P3-transactional-replacement",
        "P3-hands-only-alternative",
        "P3-typed-pipeline-local-backend",
        "P3-regressions",
        "P3-android-build-healthy",
        "P3-no-secret-in-apk",
        "P3-asr-human-speech-mac",
        "P3-model-assisted-intent",
        "P3-deployed-backend-reachable",
        "P3-voice-capture-and-transcription-route",
        "P3-voice-benchmark-deployed",
        "P3-manual-simulator-walkthrough",
        "P3-quest-device",
        "P3-devagent-token-rotated",
    },
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    scope = parser.add_mutually_exclusive_group(required=True)
    scope.add_argument("--profile", choices=["r0"])
    scope.add_argument("--phase", type=int, choices=sorted(REQUIRED_GATES))
    args = parser.parse_args(argv)

    state: dict[str, Any] = json.loads(STATE.read_text(encoding="utf-8"))
    gates: list[dict[str, Any]] = state.get("gates", [])
    problems: list[str] = []

    phases = sorted(REQUIRED_GATES) if args.profile else [args.phase]
    by_id = {g.get("id"): g for g in gates}
    if len(by_id) != len(gates):
        problems.append("duplicate gate ids in state file")

    for phase in phases:
        for gate_id in sorted(REQUIRED_GATES[phase] - by_id.keys()):
            problems.append(f"{gate_id}: missing gate record (treated as not passed)")

    for gate in gates:
        if gate.get("phase") not in phases:
            continue
        gid = gate.get("id", "?")
        if set(gate) != GATE_FIELDS:
            problems.append(f"{gid}: malformed record fields {sorted(set(gate) ^ GATE_FIELDS)}")
            continue
        status = gate["status"]
        if status not in GATE_STATUSES:
            problems.append(f"{gid}: unknown status {status!r}")
            continue
        if status == "not_applicable" and gate["required"]:
            problems.append(f"{gid}: required gate cannot be not_applicable")
        if status == "pass" and gate["evidence_path"]:
            evidence = ROOT / gate["evidence_path"]
            if not gate["evidence_sha256"]:
                problems.append(f"{gid}: pass without evidence hash")
            elif evidence.exists() and sha256(evidence) != gate["evidence_sha256"]:
                problems.append(f"{gid}: evidence hash mismatch for {gate['evidence_path']}")
            elif not evidence.exists():
                print(f"note  {gid}: evidence {gate['evidence_path']} not present locally; hash not re-verified")
        print(f"{status:<15} {'required' if gate['required'] else 'optional':<9} {gid}")
        if gate["required"] and status != "pass":
            problems.append(f"{gid}: {status}")

    for problem in problems:
        print(f"FAIL  {problem}")
    print(f"check_release: {len(problems)} gate problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
