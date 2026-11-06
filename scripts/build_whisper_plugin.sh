#!/usr/bin/env bash
# Builds the on-device speech plugin (native/whisper_shim + pinned whisper.cpp) and copies it into the Unity project.
#
#   scripts/build_whisper_plugin.sh [android|macos|all]      (default: all)
#
# Env: TRYSCAPE_WHISPER_SRC (default ~/.cache/tryscape/whisper.cpp), TRYSCAPE_ANDROID_NDK_ROOT (pinned NDK r27c).
# Outputs: app-unity/Assets/Plugins/Android/arm64-v8a/libtryscape_whisper.so (Quest)
#          app-unity/Assets/Plugins/macOS/libtryscape_whisper.dylib       (Editor on Apple silicon, for tests)
# and records hashes in native/whisper_shim/BUILD_RECORD.txt.
set -euo pipefail
WHISPER_COMMIT="d1be6fde11ac6e0407606b4e42fe72d34add8037"   # v1.9.5 (docs/dependencies.md)
root="$(cd "$(dirname "$0")/.." && pwd)"
src="${TRYSCAPE_WHISPER_SRC:-$HOME/.cache/tryscape/whisper.cpp}"
ndk="${TRYSCAPE_ANDROID_NDK_ROOT:-/Applications/Unity/Hub/Editor/6000.6.8f1/PlaybackEngines/AndroidPlayer/SDK/ndk/27.2.12479018}"
target="${1:-all}"

actual="$(git -C "$src" rev-parse HEAD)"
[[ "$actual" == "$WHISPER_COMMIT" ]] || { echo "whisper.cpp at $actual, expected $WHISPER_COMMIT" >&2; exit 1; }
[[ -z "$(git -C "$src" status --porcelain --untracked-files=no)" ]] || { echo "whisper.cpp checkout has local changes" >&2; exit 1; }

build_dir="$root/native/whisper_shim/build"
record="$root/native/whisper_shim/BUILD_RECORD.txt"

build_android() {
  local out="$build_dir/android-arm64"
  grep -q "Pkg.Revision = 27.2.12479018" "$ndk/source.properties" || { echo "NDK is not r27c: $ndk" >&2; exit 1; }
  cmake -S "$root/native/whisper_shim" -B "$out" -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-32 \
    -DGGML_CPU_ARM_ARCH=armv8.2-a+dotprod+fp16 -DWHISPER_SOURCE_DIR="$src" >/dev/null
  cmake --build "$out" --target tryscape_whisper -j 8 >/dev/null
  mkdir -p "$root/app-unity/Assets/Plugins/Android/arm64-v8a"
  cp "$out/libtryscape_whisper.so" "$root/app-unity/Assets/Plugins/Android/arm64-v8a/"
  "$ndk/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip" --strip-unneeded "$root/app-unity/Assets/Plugins/Android/arm64-v8a/libtryscape_whisper.so"
}

build_macos() {
  local out="$build_dir/macos-arm64"
  cmake -S "$root/native/whisper_shim" -B "$out" -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 -DWHISPER_SOURCE_DIR="$src" >/dev/null
  cmake --build "$out" --target tryscape_whisper -j 8 >/dev/null
  mkdir -p "$root/app-unity/Assets/Plugins/macOS"
  cp "$out/libtryscape_whisper.dylib" "$root/app-unity/Assets/Plugins/macOS/"
}

case "$target" in
  android) build_android ;;
  macos) build_macos ;;
  all) build_android; build_macos ;;
  *) echo "usage: $0 [android|macos|all]" >&2; exit 2 ;;
esac

{
  echo "whisper.cpp commit: $WHISPER_COMMIT"
  echo "shim: native/whisper_shim/tryscape_whisper.c sha256 $(shasum -a 256 "$root/native/whisper_shim/tryscape_whisper.c" | cut -d' ' -f1)"
  echo "ndk: $(grep Pkg.Revision "$ndk/source.properties" | cut -d= -f2 | tr -d ' ')"
  for f in "$root/app-unity/Assets/Plugins/Android/arm64-v8a/libtryscape_whisper.so" "$root/app-unity/Assets/Plugins/macOS/libtryscape_whisper.dylib"; do
    [[ -f "$f" ]] && echo "${f#"$root"/}: $(stat -f%z "$f") bytes sha256 $(shasum -a 256 "$f" | cut -d' ' -f1)"
  done
} > "$record"
cat "$record"
