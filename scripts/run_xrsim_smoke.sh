#!/usr/bin/env bash
# M0 gate: run the XRSimulator PlayMode category against the standalone Meta XR Simulator (macOS Apple Silicon).
#   TRYSCAPE_UNITY_EDITOR=... scripts/run_xrsim_smoke.sh
# Sets the OpenXR runtime only for this Unity process (no system-wide activation). Generates a simulator config
# from Meta's installed default with batch mode on and absolute paths, under artifacts/ (not committed).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sim="${TRYSCAPE_XRSIM_ROOT:-/Applications/MetaXRSimulator.app/Contents/Resources/MetaXRSimulator}"
runtime_json="$sim/meta_openxr_simulator.json"
[[ -f "$runtime_json" ]] || { echo "Meta XR Simulator runtime not found at $runtime_json" >&2; exit 2; }

out="$root/artifacts/unity"; mkdir -p "$out"
config="$out/xrsim_config.json"
python3 - "$sim" "$config" "$out/meta_xrsim.log" <<'PY'
import json, os, sys
sim, dest, log = sys.argv[1:4]
cfg = json.load(open(os.path.join(sim, "config", "sim_core_configuration.json")))
def absolutize(node):
    if isinstance(node, dict):
        return {k: absolutize(v) for k, v in node.items()}
    if isinstance(node, str) and not node.startswith("/") and os.path.exists(os.path.join(sim, node)):
        return os.path.join(sim, node)
    return node
cfg = absolutize(cfg)
cfg["use_batch_mode"] = True
cfg["log_location"] = log
json.dump(cfg, open(dest, "w"), indent=2)
PY

# One Unity process per scene: switching between XR rig scenes inside a single play session leaves Interaction SDK
# transformer references stale (NullReferenceException in TrackingToWorldTransformerOVR). Tryscape uses one scene.
status=0
for test in BootstrapRigTracksSimulatedHands InteractionSdkHandGrabSampleTracksSimulatedHands; do
  results="$out/xrsim-$test.xml"
  rm -f "$results"
  XR_RUNTIME_JSON="$runtime_json" XR_SELECTED_RUNTIME_JSON="$runtime_json" META_XRSIM_CONFIG_JSON="$config" \
    "$root/scripts/unity_batch.sh" "xrsim-$test" "Saving results to" -- \
    -batchmode -projectPath "$root/app-unity" -runTests -testPlatform PlayMode \
    -testFilter "Tryscape.Tests.PlayMode.XR.SimulatorSmokeTests.$test" -testResults "$results" || true
  grep -h "\[Tryscape\]\[XRSIM\]" "$out/xrsim-$test.log" || true
  python3 "$root/scripts/check_test_results.py" "$results" --min-tests 1 --no-skips || status=1
done
exit $status
