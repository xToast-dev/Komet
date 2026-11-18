#!/usr/bin/env bash
# The workbench's shader test, unattended: builds Komet (unless --no-build), runs the game in the bench
# sandbox (a copy of the bench world; the player's data folder is only read), lets the Shader Studio show every vanilla shader in
# the 3D view and closes the game. Pictures, contact sheets (sheet-N.png) and report.txt land in the folder printed at
# the end (~/.cache/komet-shadertest/<time>). Runs beside a game that is already open (--force).
#
#   scripts/shadertest.sh [--no-build] [--timeout SECONDS]
set -euo pipefail
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
BUILD=1
TIMEOUT=1500
while (($#)); do
  case $1 in
    --no-build) BUILD=0; shift ;;
    --timeout) TIMEOUT=${2:?}; shift 2 ;;
    *) echo "shadertest: unknown option $1" >&2; exit 2 ;;
  esac
done
export VS_GAME=${VS_GAME:-$HOME/.local/share/vintagestory}
OUT=${KOMET_SHADERTEST_CACHE:-$HOME/.cache/komet-shadertest}/$(date +%Y%m%dT%H%M%S)
mkdir -p "$OUT"
if ((BUILD)); then
  (cd "$REPO" && ./build.sh --disable-build-servers &&
    dotnet build Komet.Bench/Komet.Bench.csproj -c Release -t:Package --disable-build-servers) >/dev/null || { echo "build failed" >&2; exit 1; }
fi
# the Shader Studio ships inside Komet: the bench's Komet runs the test
"$REPO/scripts/bench.sh" --profile shadertest --label shadertest --force \
  --env "KOMET_SHADER_TEST=$OUT" --timeout "$TIMEOUT" >"$OUT/bench.log" 2>&1 || true
if [[ -f $OUT/done ]]; then
  echo "shader test: $OUT"
  grep -cv " ok " "$OUT/report.txt" | sed 's/^/lines not ok (with header): /'
else
  echo "shader test did not finish, see $OUT/bench.log" >&2
  exit 1
fi
