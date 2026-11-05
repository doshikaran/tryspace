#!/usr/bin/env bash
# Run one Unity batch step and judge it from fresh evidence, not log text alone.
#
#   scripts/unity_batch.sh <log-name> <success-marker-regex> [--artifact <path>]... [--strict] -- <unity args...>
#
# Requires TRYSCAPE_UNITY_EDITOR (path to the pinned Unity executable). Log: artifacts/unity/<log-name>.log.
# Status record: artifacts/unity/<log-name>.status.json (process exit code, classification, artifact hashes).
#
# Each --artifact path is deleted before Unity starts and must afterwards exist, be non-empty and be newer than
# the invocation start; otherwise the step fails whatever Unity's exit code was. Use it for the step's real output
# (test-result XML, APK and its manifest). A success marker alone is never sufficient.
#
# Classification (reported separately from Unity's process exit code):
#   clean_success                  Unity exit 0, marker logged, all artifacts fresh.
#   success_with_shutdown_abort    Unity exit 134 (SIGABRT) with the teardown signature
#                                  "terminate_handler unexpectedly returned" logged AFTER the success marker,
#                                  no compiler errors, all artifacts fresh. Known intermittent editor-shutdown
#                                  abort with Unity 6000.6.8f1 + Meta XR 207 (docs/environment/m0-environment.md).
#                                  Exit 0 by default; exit 3 with --strict.
#   failure                        anything else; Unity's exit code (or 1) is propagated.
# Callers must still judge the artifact's contents (scripts/check_test_results.py, APK manifest hash).
set -euo pipefail

usage() { echo "usage: $0 <log-name> <success-marker-regex> [--artifact <path>]... [--strict] -- <unity args...>" >&2; exit 2; }
[[ $# -ge 3 ]] || usage
name="$1"; marker="$2"; shift 2
artifacts=(); strict=0
while [[ $# -gt 0 && "$1" != "--" ]]; do
  case "$1" in
    --artifact) [[ $# -ge 2 ]] || usage; artifacts+=("$2"); shift 2 ;;
    --strict) strict=1; shift ;;
    *) usage ;;
  esac
done
[[ $# -ge 2 && "$1" == "--" ]] || usage
shift
: "${TRYSCAPE_UNITY_EDITOR:?set TRYSCAPE_UNITY_EDITOR to the pinned Unity executable}"

root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$root/artifacts/unity"
log="$root/artifacts/unity/$name.log"
status_file="$root/artifacts/unity/$name.status.json"
rm -f "$log" "$status_file"
for a in ${artifacts[@]+"${artifacts[@]}"}; do rm -f "$a"; done

stamp="$root/artifacts/unity/.$name.start"
touch "$stamp"
started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

set +e
"$TRYSCAPE_UNITY_EDITOR" "$@" -logFile "$log"
code=$?
set -e

marker_line=""; abort_line=""; compile_errors=0
if [[ -f "$log" ]]; then
  marker_line=$(grep -n -E "$marker" "$log" | head -1 | cut -d: -f1 || true)
  abort_line=$(grep -n "terminate_handler unexpectedly returned" "$log" | tail -1 | cut -d: -f1 || true)
  grep -qE "error CS[0-9]+|Scripts have compiler errors" "$log" && compile_errors=1
fi

artifact_ok=1; artifact_json=""
for a in ${artifacts[@]+"${artifacts[@]}"}; do
  if [[ -s "$a" && "$a" -nt "$stamp" ]]; then
    sha=$(shasum -a 256 "$a" | cut -d' ' -f1); fresh=true
  else
    sha=""; fresh=false; artifact_ok=0
    echo "unity_batch: artifact missing, empty or stale: $a" >&2
  fi
  artifact_json+="${artifact_json:+,}{\"path\":\"${a//\"/\\\"}\",\"fresh\":$fresh,\"sha256\":\"$sha\"}"
done
rm -f "$stamp"

classification=failure
if [[ $artifact_ok -eq 1 && -n "$marker_line" && $compile_errors -eq 0 ]]; then
  if [[ $code -eq 0 ]]; then
    classification=clean_success
  elif [[ $code -eq 134 && -n "$abort_line" && "$marker_line" -lt "$abort_line" ]]; then
    classification=success_with_shutdown_abort
  fi
fi

cat > "$status_file" <<JSON
{"step":"$name","started_at":"$started_at","unity_exit_code":$code,"classification":"$classification","success_marker_logged":$([[ -n "$marker_line" ]] && echo true || echo false),"shutdown_abort_logged":$([[ -n "$abort_line" ]] && echo true || echo false),"compile_errors":$([[ $compile_errors -eq 1 ]] && echo true || echo false),"artifacts":[${artifact_json}]}
JSON

case "$classification" in
  clean_success)
    echo "unity_batch: $name clean_success (unity exit 0) log=$log"
    exit 0 ;;
  success_with_shutdown_abort)
    echo "unity_batch: WARNING $name success_with_shutdown_abort: work completed and artifacts are fresh, but the Unity process aborted during shutdown (unity exit 134); log=$log"
    [[ $strict -eq 1 ]] && exit 3
    exit 0 ;;
  *)
    echo "unity_batch: $name FAILED (unity exit $code, marker=$([[ -n "$marker_line" ]] && echo yes || echo no), artifacts_ok=$artifact_ok, compile_errors=$compile_errors) log=$log" >&2
    [[ $code -ne 0 ]] && exit "$code"
    exit 1 ;;
esac
