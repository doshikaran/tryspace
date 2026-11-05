#!/usr/bin/env bash
# Run one Unity batch step and judge success from evidence, not log text alone.
#
#   scripts/unity_batch.sh <log-name> <success-marker-regex> -- <unity args...>
#
# Requires TRYSCAPE_UNITY_EDITOR (path to the pinned Unity executable). Logs go to artifacts/unity/<log-name>.log.
#
# Exit policy:
#   * Unity exit 0                                  -> success.
#   * Unity exit 134 (SIGABRT) AND the log contains Unity's teardown abort signature
#     "terminate_handler unexpectedly returned" AND the step's success marker was logged before it
#                                                   -> success with a WARNING (known intermittent editor-shutdown
#                                                      abort with Unity 6000.6.8f1 + Meta XR 207; docs/progress.md).
#   * anything else                                 -> failure (Unity's exit code is propagated).
# Callers must still check the step's own evidence (test-result XML, APK + manifest hash).
set -euo pipefail

if [[ $# -lt 4 || "$3" != "--" ]]; then
  echo "usage: $0 <log-name> <success-marker-regex> -- <unity args...>" >&2
  exit 2
fi
: "${TRYSCAPE_UNITY_EDITOR:?set TRYSCAPE_UNITY_EDITOR to the pinned Unity executable}"

name="$1"; marker="$2"; shift 3
root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$root/artifacts/unity"
log="$root/artifacts/unity/$name.log"

set +e
"$TRYSCAPE_UNITY_EDITOR" "$@" -logFile "$log"
code=$?
set -e

if [[ $code -eq 0 ]]; then
  echo "unity_batch: $name OK (exit 0) log=$log"
  exit 0
fi

if [[ $code -eq 134 ]] && grep -q "terminate_handler unexpectedly returned" "$log"; then
  marker_line=$(grep -n -E "$marker" "$log" | head -1 | cut -d: -f1 || true)
  abort_line=$(grep -n "terminate_handler unexpectedly returned" "$log" | tail -1 | cut -d: -f1)
  if [[ -n "$marker_line" && "$marker_line" -lt "$abort_line" ]] && ! grep -qE "error CS[0-9]+|Scripts have compiler errors" "$log"; then
    echo "unity_batch: WARNING $name succeeded but Unity aborted during shutdown (exit 134); log=$log"
    exit 0
  fi
fi

echo "unity_batch: $name FAILED (exit $code) log=$log" >&2
exit "$code"
