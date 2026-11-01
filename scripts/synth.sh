#!/usr/bin/env bash
# The synthetic bench: every [Category("Synth")] scenario, each feature off against on without the game (tests/Komet.Test/Synth).
# One test host, one scenario after the other: no other load while it measures. Prints the table, slowest gain last.
#
#   scripts/synth.sh [--no-build] [name filter, e.g. TickMirror]
set -euo pipefail
cd "$(dirname "$0")/.."
BUILD=1
[[ ${1:-} == --no-build ]] && { BUILD=0; shift; }
FILTER="TestCategory=Synth${1:+&FullyQualifiedName~$1}"
OUT=${KOMET_SYNTH_OUT:-$(mktemp "${TMPDIR:-/tmp}/komet-synth.XXXXXX.jsonl")}
: >"$OUT"
if ((BUILD)); then
  dotnet build tests/Komet.Test -c Release --disable-build-servers -v q -nologo >/dev/null ||
    { echo "synth.sh: build failed (dotnet build tests/Komet.Test -c Release)" >&2; exit 1; }
fi
KOMET_SYNTH_OUT=$OUT dotnet test tests/Komet.Test -c Release --no-build --filter "$FILTER" -nologo \
  -- NUnit.NumberOfTestWorkers=1 >"$OUT.log" 2>&1 || { grep -E "Failed|Fehler|Error Message" -A4 "$OUT.log" | head -40; }
python3 - "$OUT" <<'EOF'
import json, sys
rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
print(f"{'feature/scenario':44} {'off ns':>9} {'on ns':>9} {'time':>7} {'off B':>8} {'on B':>8} {'bytes':>7} {'noise':>6}  verdict")
for r in sorted(rows, key=lambda r: (r['Verdict'] != 'gain', r['TimeDelta'])):
    print(f"{(r['Feature'] + '/' + r['Scenario'])[:44]:44} {r['OffNs']:9.1f} {r['OnNs']:9.1f} {r['TimeDelta']:+7.1%} "
          f"{r['OffBytes']:8.0f} {r['OnBytes']:8.0f} {r['BytesDelta']:+7.1%} {r['Noise']:6.1%}  {r['Verdict']}")
print(f"\n{len(rows)} scenarios, results in {sys.argv[1]}")
EOF
