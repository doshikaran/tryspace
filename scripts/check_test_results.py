"""Judge a Unity Test Framework (NUnit 3) result file by its contents, not log text (CLAUDE.md).

    python3 scripts/check_test_results.py artifacts/unity/editmode.xml [--min-tests N] [--no-skips]

Exit 1 if the file is missing/unparseable, any test failed or errored, fewer than --min-tests ran, or (with
--no-skips) any test was skipped/ignored/inconclusive. Standard library only.
"""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("results", type=Path)
    parser.add_argument("--min-tests", type=int, default=1)
    parser.add_argument("--no-skips", action="store_true")
    args = parser.parse_args(argv)

    if not args.results.is_file():
        print(f"check_test_results: missing {args.results}")
        return 1
    try:
        root = ET.parse(args.results).getroot()  # noqa: S314 - local file produced by Unity
    except ET.ParseError as exc:
        print(f"check_test_results: unparseable {args.results}: {exc}")
        return 1
    run = root if root.tag == "test-run" else root.find("test-run")
    if run is None:
        print("check_test_results: no <test-run> element")
        return 1

    total = int(run.get("total", 0))
    passed = int(run.get("passed", 0))
    failed = int(run.get("failed", 0))
    skipped = int(run.get("skipped", 0))
    inconclusive = int(run.get("inconclusive", 0))
    executed = passed + failed
    problems = []
    for case in run.iter("test-case"):
        result = case.get("result")
        if result == "Failed":
            message = (case.findtext("failure/message") or "").strip().splitlines()
            problems.append(f"FAILED {case.get('fullname')}: {message[0] if message else ''}")
        elif args.no_skips and result in {"Skipped", "Inconclusive"}:
            reason = (case.findtext("reason/message") or "").strip()
            problems.append(f"SKIPPED {case.get('fullname')}: {reason}")
    if executed < args.min_tests:
        problems.append(f"only {executed} test(s) executed; expected at least {args.min_tests}")

    for problem in problems:
        print(problem)
    print(
        f"check_test_results: {args.results.name} total={total} passed={passed} failed={failed} "
        f"skipped={skipped} inconclusive={inconclusive} -> {'FAIL' if problems or failed else 'PASS'}"
    )
    return 1 if problems or failed else 0


if __name__ == "__main__":
    sys.exit(main())
