#!/usr/bin/env bash
# Unit tests, fast: one build, then shards in parallel, each in a test host of its own. Not one host with NUnit's parallelism: nearly
# every fixture patches engine methods (TestHarmony) and Komet's statics are shared, so only separate processes keep them apart.
#
#   scripts/test.sh [--all] [--no-build] [-- extra dotnet test arguments]
#
#   default     the quick set: everything but [Category("Slow")], the golden and engine-parity sweeps (about a minute with the build)
#   --all       every test, the release gate (build.sh does not run them)
#   --no-build  the last build of the tests as it is
#
# A failing shard prints its failures; the exit code is non-zero when any shard failed.
set -euo pipefail
cd "$(dirname "$0")/.."
ALL=0 BUILD=1 EXTRA=()
while (($#)); do
  case $1 in
    --all) ALL=1 ;;
    --no-build) BUILD=0 ;;
    --) shift; EXTRA=("$@"); break ;;
    *) echo "test.sh: unknown argument $1" >&2; exit 2 ;;
  esac
  shift
done

PROJECT=tests/Komet.Test
OUT=$(mktemp -d "${TMPDIR:-/tmp}/komet-test.XXXXXX")
trap 'rm -rf "$OUT"' EXIT
START=$SECONDS

if ((BUILD)); then
  dotnet build "$PROJECT" -c Release --disable-build-servers -v q -nologo >"$OUT/build.log" 2>&1 ||
    { grep -E "error|Fehler" "$OUT/build.log" | sort -u | head -40; echo "test.sh: build failed" >&2; exit 1; }
fi

ns() { echo "FullyQualifiedName~Komet.Test.$1"; }
any() { local IFS='|'; echo "$*"; }
# Balanced by measured time (2026-10-07): LightRepair alone is a third of the full run, the GPU fixtures cost a context each
GPU_A=$(any $(for p in B C D E F G H M O; do ns "Gpu.$p"; done))
GPU_B="$(ns Gpu)$(for p in B C D E F G H M O; do printf '&FullyQualifiedName!~Komet.Test.Gpu.%s' $p; done)" # the rest: none left out
SHARDS=(
  "lightrepair|$(ns World.LightRepairTests)"
  "world|$(ns World)&FullyQualifiedName!~Komet.Test.World.LightRepairTests"
  "shapes|$(ns Shapes)"
  "tessellation|$(ns Tessellation)"
  "gpu-a|$GPU_A"
  "gpu-b|$GPU_B"
  "rest|FullyQualifiedName!~Komet.Test.World&FullyQualifiedName!~Komet.Test.Shapes&FullyQualifiedName!~Komet.Test.Tessellation&FullyQualifiedName!~Komet.Test.Gpu"
)
((ALL)) || QUICK="&TestCategory!=Slow"

PIDS=()
for shard in "${SHARDS[@]}"; do
  name=${shard%%|*} filter="(${shard#*|})${QUICK:-}"
  dotnet test "$PROJECT" -c Release --no-build --filter "$filter" -nologo "${EXTRA[@]}" >"$OUT/$name.log" 2>&1 &
  PIDS+=("$!:$name")
done

FAILED=0
for entry in "${PIDS[@]}"; do
  pid=${entry%%:*} name=${entry#*:}
  if wait "$pid"; then
    printf '  %-13s %s\n' "$name" "$(grep -oE "(erfolgreich|Passed): +[0-9]+" "$OUT/$name.log" | tail -1)"
  elif grep -qE "No test matches|Keine Tests entsprechen" "$OUT/$name.log"; then
    printf '  %-13s none\n' "$name"
  else
    FAILED=1
    printf '  %-13s FAILED\n' "$name"
    grep -E "^\s+(Fehler|Failed) |Fehlermeldung|Error Message" -A4 "$OUT/$name.log" | head -60
  fi
done

echo "test.sh: $( ((ALL)) && echo all || echo quick ) in $((SECONDS - START)) s"
exit $FAILED
