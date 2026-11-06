#!/usr/bin/env bash
# Places the bundled on-device speech model in app-unity/Assets/StreamingAssets/asr/ (decision D12).
# The model (148 MB) is not in git: it exceeds GitHub's file limit. Source order: local cache, then the official
# whisper.cpp model repository. The SHA-256 must match; anything else is deleted and the script fails.
#
#   scripts/fetch_asr_model.sh
set -euo pipefail
NAME="ggml-base.en.bin"
SHA="a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"   # docs/dependencies.md
URL="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$NAME"
root="$(cd "$(dirname "$0")/.." && pwd)"
dest_dir="$root/app-unity/Assets/StreamingAssets/asr"
dest="$dest_dir/$NAME"
cache="${TRYSCAPE_MODEL_CACHE:-$HOME/.cache/tryscape/models}/$NAME"
mkdir -p "$dest_dir"

check() { [[ -f "$1" ]] && [[ "$(shasum -a 256 "$1" | cut -d' ' -f1)" == "$SHA" ]]; }

if check "$dest"; then echo "model present and verified: ${dest#"$root"/}"; exit 0; fi
if check "$cache"; then
  cp "$cache" "$dest.tmp"
else
  echo "downloading $URL"
  curl -fL --retry 3 -o "$dest.tmp" "$URL"
fi
if ! check "$dest.tmp"; then rm -f "$dest.tmp"; echo "model hash mismatch; refusing to use it" >&2; exit 1; fi
mv "$dest.tmp" "$dest"
echo "model installed and verified: ${dest#"$root"/}"
