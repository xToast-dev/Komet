#!/usr/bin/env bash
# Puts shaderc for Windows x64 into the release (native/win-x64): Windows has none of its own, and Vulkan-only would wait
# for ShadercFetch's download otherwise. Same package and pinned hash as ShadercFetch; cached in ~/.cache/komet-build.
#
#   scripts/shaderc-native.sh <release native folder>
set -euo pipefail
OUT=${1:?release native folder}
URL=https://api.nuget.org/v3-flatcontainer/silk.net.shaderc.native/2.23.0/silk.net.shaderc.native.2.23.0.nupkg
SHA=50f67d5ebaefe97bd55f9b0eed4044f57c925675d55100d35a51718ccab948be
CACHE=${XDG_CACHE_HOME:-$HOME/.cache}/komet-build
PKG=$CACHE/silk.net.shaderc.native.2.23.0.nupkg
mkdir -p "$CACHE" "$OUT/win-x64"
if ! echo "$SHA  $PKG" | sha256sum -c --status 2>/dev/null; then
  curl -sSfL --retry 3 -o "$PKG.part" "$URL"
  echo "$SHA  $PKG.part" | sha256sum -c --status || { echo "shaderc-native.sh: package hash mismatch" >&2; rm -f "$PKG.part"; exit 1; }
  mv "$PKG.part" "$PKG"
fi
unzip -p "$PKG" runtimes/win-x64/native/shaderc_shared.dll >"$OUT/win-x64/shaderc_shared.dll"
cat >"$OUT/win-x64/NOTICE.txt" <<'TXT'
shaderc_shared.dll: shaderc by Google (https://github.com/google/shaderc), Apache License 2.0
(https://www.apache.org/licenses/LICENSE-2.0), unmodified, as built in the nuget package Silk.NET.Shaderc.Native 2.23.0.
TXT
