#!/usr/bin/env bash
# Komet in-game benchmark, unattended. Runs the game once in a throwaway data folder on a copy of one save, flies the route in
# scripts/bench.json with one Komet build, and leaves result.json (schema komet-bench/2) and frames.csv in
# ~/.cache/komet-bench/runs/<time>-<label>/. The player's own data folder is only ever read.
#
#   scripts/bench.sh [--profile smoke|aa|full|tess|gcreg|hud] [--label NAME] [--config FILE] [--mod DIR] [--build] [--golden]
#                    [--keep] [--env NAME=VALUE]... [--timeout SECONDS] [--boot-timeout SECONDS] [--force]
#
#   --profile       profile from the config (default smoke, about four minutes with loading; aa, full, tess, gcreg about 25)
#   --mod DIR       the build under test, a Release folder mod (default Releases/komet); two builds measured against each
#                   other are launched alternately, A B B A, with the same profile. Only builds with this harness (result
#                   schema komet-bench/2) read the bench.json written here: an older build runs with its own tree's bench.sh
#   --build         run ./build.sh (Release) into Releases/komet first; not with another --mod
#   --golden        make the golden copy of the save again (the game must be closed)
#   --keep          keep the copied clientsettings.json and save in the run folder (they hold the session key)
#   --env N=V       extra environment for the game process, e.g. --env DOTNET_gcServer=1 --env DOTNET_GCgen0size=0x4000000
#   --force         run although another Vintagestory process is up (GPU contention, shared URI pipe) or the desktop is locked
#
# bench.json in the run folder records the git revision of the tree the mod folder lies in (if any) and the environment the
# game ran with (inherited DOTNET_/COMPlus_ variables, then the profile's sandbox.env, then --env).
#
# Environment: VS_DATA (default ~/.config/VintagestoryData), VS_GAME (default /opt/vintagestory),
#              KOMET_BENCH_CACHE (default ~/.cache/komet-bench).
# The copied clientsettings.json carries the login session: the run folder is 0700, the file is never printed, and it is deleted
# after the run unless --keep.
set -euo pipefail
umask 077

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
CONFIG=$REPO/scripts/bench.json
PROFILE=smoke
LABEL=
KEEP=0
GOLDEN=0
BUILD=0
FORCE=0
TIMEOUT=
BOOT_TIMEOUT=
EXTRA_ENV=()
MOD=
DATA=${VS_DATA:-$HOME/.config/VintagestoryData}
GAME=${VS_GAME:-/opt/vintagestory}
CACHE=${KOMET_BENCH_CACHE:-$HOME/.cache/komet-bench}

usage() { sed -n '2,25p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "bench: $*" >&2; exit 2; }

while (($#)); do
  case $1 in
    --profile) PROFILE=${2:?}; shift 2 ;;
    --label) LABEL=${2:?}; shift 2 ;;
    --config) CONFIG=$(realpath "${2:?}"); shift 2 ;;
    --mod) MOD=$(realpath "${2:?}"); shift 2 ;;
    --build) BUILD=1; shift ;;
    --golden) GOLDEN=1; shift ;;
    --keep) KEEP=1; shift ;;
    --force) FORCE=1; shift ;;
    --timeout) TIMEOUT=${2:?}; shift 2 ;;
    --boot-timeout) BOOT_TIMEOUT=${2:?}; shift 2 ;;
    --env)
      [[ ${2:-} =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || die "--env wants NAME=VALUE, got '${2:-}'"
      EXTRA_ENV+=("$2"); shift 2 ;;
    -h|--help) usage 0 ;;
    *) echo "bench: unknown argument $1" >&2; usage 2 ;;
  esac
done

for tool in python3 sqlite3 timeout pgrep; do command -v "$tool" >/dev/null || die "$tool is required"; done
[[ -f $CONFIG ]] || die "no config at $CONFIG"
[[ -x $GAME/run.sh ]] || die "no game at $GAME (set VS_GAME)"
[[ $LABEL =~ ^[A-Za-z0-9._-]*$ ]] || die "--label may hold letters, digits, . _ - only"
game_running() { pgrep -x Vintagestory >/dev/null 2>&1; }

# The world names and sandbox settings the profile asks for; nothing here is secret
read -r SAVE SAVEGAME_ID CFG_BOOT < <(python3 - "$CONFIG" "$PROFILE" <<'PY'
import json, sys
config = json.load(open(sys.argv[1]))
if sys.argv[2] not in config.get("profiles", {}):
    sys.exit(f"bench: no profile {sys.argv[2]!r}, have: {', '.join(config.get('profiles', {}))}")
world = config["world"]
sandbox = dict(config.get("sandbox", {}), **config["profiles"][sys.argv[2]].get("sandbox", {}))
print(world["save"], world["savegameId"], sandbox.get("bootTimeout", 240))
PY
)
BOOT_TIMEOUT=${BOOT_TIMEOUT:-$CFG_BOOT}
[[ $SAVE =~ ^[A-Za-z0-9._-]+$ && $SAVEGAME_ID =~ ^[A-Za-z0-9-]+$ ]] || die "world.save and world.savegameId must be plain names"

# The build under test: Release only, build.sh lays it out as a folder mod
DEFAULT_MOD=$(realpath -m "$REPO/Releases/komet")
MOD=${MOD:-$DEFAULT_MOD}
if ((BUILD)) && [[ $MOD != "$DEFAULT_MOD" ]]; then die "--build builds $DEFAULT_MOD; drop --mod or --build"; fi
if ((BUILD)); then (cd "$REPO" && ./build.sh) || die "build failed"; fi
[[ -f $MOD/Komet.dll && -f $MOD/modinfo.json ]] || die "no build in $MOD, run ./build.sh or pass --build"
# BenchReport.Schema, a UTF-16 literal in the assembly; an older harness refuses the bench.json below only once the world is up
python3 -c 'import sys; sys.exit(open(sys.argv[1], "rb").read().find("komet-bench/2".encode("utf-16-le")) < 0)' "$MOD/Komet.dll" ||
  die "$MOD holds an older bench harness than this script (no komet-bench/2): run it with its own tree's scripts/bench.sh"
if [[ $MOD == "$DEFAULT_MOD" ]] &&
  [[ -n $(find "$REPO/Komet" -name '*.cs' -newer "$MOD/Komet.dll" -not -path '*/obj/*' -not -path '*/bin/*' -print -quit) ]]; then
  echo "bench: warning: sources are newer than $MOD/Komet.dll, pass --build to benchmark them" >&2
fi
REVISION=$(git -C "$MOD" describe --always --dirty 2>/dev/null || true)

# Golden copy, once: .backup reads a consistent snapshot with the WAL folded in; -readonly never checkpoints the player's file
GOLDEN_DIR=$CACHE/golden
GOLDEN_SAVE=$GOLDEN_DIR/$SAVE.vcdbs
GOLDEN_MAP=$GOLDEN_DIR/$SAVEGAME_ID.db
if ((GOLDEN)) || [[ ! -f $GOLDEN_SAVE ]]; then
  game_running && die "close the game first: the golden copy is taken from the live save"
  [[ -f $DATA/Saves/$SAVE.vcdbs ]] || die "no save $DATA/Saves/$SAVE.vcdbs"
  mkdir -p "$GOLDEN_DIR"
  rm -f "$GOLDEN_SAVE.tmp" "$GOLDEN_MAP.tmp"
  sqlite3 -readonly "$DATA/Saves/$SAVE.vcdbs" ".backup '$GOLDEN_SAVE.tmp'"
  mv -f "$GOLDEN_SAVE.tmp" "$GOLDEN_SAVE"
  if [[ -f $DATA/Maps/$SAVEGAME_ID.db ]]; then
    sqlite3 -readonly "$DATA/Maps/$SAVEGAME_ID.db" ".backup '$GOLDEN_MAP.tmp'"
    mv -f "$GOLDEN_MAP.tmp" "$GOLDEN_MAP"
  fi
  echo "bench: golden copy of $SAVE in $GOLDEN_DIR"
fi
if game_running && ((!FORCE)); then die "another Vintagestory is running; close it or pass --force"; fi
# On a locked desktop the game hangs while it creates its window, before any mod loads
if command -v loginctl >/dev/null && [[ -n ${XDG_SESSION_ID:-} ]] && ((!FORCE)) &&
  [[ $(loginctl show-session "$XDG_SESSION_ID" -p LockedHint --value 2>/dev/null) == yes ]]; then
  die "the desktop is locked, the game cannot open its window; unlock it or pass --force"
fi

# Per-run sandbox on ext4 under ~/.cache: tmpfs would hide the disk latency of the server's chunk loads
RUN=$CACHE/runs/$(date +%Y%m%dT%H%M%S)-${LABEL:-$PROFILE}
mkdir -p "$RUN"/data/{Mods,Saves,Maps,ModConfig} "$RUN/logs"
chmod 700 "$CACHE/runs" "$RUN"
PID=
stop() { kill -TERM -- "-$PID" 2>/dev/null || kill -TERM "$PID" 2>/dev/null || true; }
# timeout forwards a TERM it receives and then exits, so a game that ignores the TERM (it hangs in window or audio setup when the
# desktop is locked, for instance) is left behind without a parent: find it by its sandbox path and kill it
reap() {
  for _ in $(seq 30); do pgrep -f -- "--dataPath $RUN/data" >/dev/null || return 0; sleep 1; done
  pgrep -f -- "--dataPath $RUN/data" >/dev/null || return 0
  echo "bench: the game ignored the TERM, killing it" >&2
  pkill -KILL -f -- "--dataPath $RUN/data" || true
}
cleanup() {
  # bench.sh itself interrupted: timeout leads its own process group, so neither a Ctrl-C nor a TERM to bench.sh reaches the game
  if [[ -n $PID ]]; then stop; reap; fi
  ((KEEP)) && return 0
  rm -f "$RUN/data/clientsettings.json" "$RUN/data/clientsettings.bkp" "$RUN/data/clientsettings.tmp" \
    "$RUN"/data/Saves/bench.vcdbs* "$RUN/data/Maps/$SAVEGAME_ID.db"
}
trap cleanup EXIT

[[ -f $DATA/clientsettings.json ]] || die "no $DATA/clientsettings.json: log in once with the normal game first"
cp "$DATA/clientsettings.json" "$RUN/data/clientsettings.json" # never cat it: it holds the session key
for file in serverconfig.json servermagicnumbers.json; do
  if [[ -f $DATA/$file ]]; then cp "$DATA/$file" "$RUN/data/$file"; fi
done
cp "$GOLDEN_SAVE" "$RUN/data/Saves/bench.vcdbs"
if [[ -f $GOLDEN_MAP ]]; then cp "$GOLDEN_MAP" "$RUN/data/Maps/$SAVEGAME_ID.db"; fi
cp -r "$MOD" "$RUN/data/Mods/komet"

# Patch the copies in place and write the flat bench.json the mod reads. Prints the time budget and the game's extra environment.
mapfile -t PLAN < <(python3 - "$CONFIG" "$PROFILE" "$RUN" "$SAVEGAME_ID" "$REVISION" "${EXTRA_ENV[@]}" <<'PY'
import json, os, sys
config_path, profile, run, savegame, revision = sys.argv[1:6]
config = json.load(open(config_path))

def merge(base, over):
    out = dict(base)
    for key, value in over.items():
        out[key] = merge(out[key], value) if isinstance(value, dict) and isinstance(out.get(key), dict) else value
    return out

chosen = config["profiles"][profile]
sandbox = merge(config.get("sandbox", {}), chosen.get("sandbox", {}))
env = {k: v for k, v in os.environ.items() if k.startswith(("DOTNET_", "COMPlus_"))}  # inherited: the game process sees them too
env.update((key, str(value)) for key, value in sandbox.get("env", {}).items())
env.update(item.split("=", 1) for item in sys.argv[6:])  # --env wins over the profile
flat = {k: v for k, v in chosen.items() if k != "sandbox"}
flat.update({"name": profile, "output": f"{run}/result.json", "modDir": f"{run}/data/Mods", "world": config["world"],
             "sandbox": sandbox, "env": env})
if revision:
    flat["revision"] = revision

def put(section, key, value):
    # the game reads these dictionaries case-insensitively; replace an existing key whatever its case
    for existing in list(section):
        if existing.lower() == key.lower():
            del section[existing]
    section[key] = value

data = f"{run}/data"
settings_path = f"{data}/clientsettings.json"
settings = json.load(open(settings_path, encoding="utf-8-sig"))
lists, ints, bools = (settings.setdefault(name, {}) for name in ("stringListSettings", "intSettings", "boolSettings"))
put(lists, "modPaths", ["Mods", f"{data}/Mods"])  # "Mods" resolves against the game folder: game, survival, creative
put(lists, "disabledMods", [])
width, height = sandbox.get("window", [1920, 1080])
for key, value in (("gameWindowMode", sandbox.get("windowMode", 0)), ("screenWidth", width), ("screenHeight", height), ("musicLevel", 0),
                   ("vsyncMode", sandbox.get("vsyncMode", 0)), ("maxFps", sandbox.get("maxFps", 241))):
    put(ints, key, int(value))
if sandbox.get("viewDistance") is not None:
    put(ints, "viewDistance", int(sandbox["viewDistance"]))
put(bools, "pauseGameOnLostFocus", False)
put(bools, "extendedDebugInfo", False)  # it also switches on CalcFragmentation and debug text, a different workload
put(bools, "showSurvivalHelpDialog", False)
with open(settings_path, "w", encoding="utf-8") as out:
    json.dump(settings, out, indent=2)

magic_path = f"{data}/servermagicnumbers.json"
magic = json.load(open(magic_path, encoding="utf-8-sig")) if os.path.exists(magic_path) else {"ServerMagicNumVersion": "1.3"}
put(magic, "ServerAutoSave", 86400)  # the 5-minute autosave would land in random measured frames of one arm
json.dump(magic, open(magic_path, "w"), indent=2)

hud = {"Visible": False, "UpdateCheck": False, "UpdateAsked": True}  # no opt-in dialog, no GitHub call, no HUD cost
hud.update(sandbox.get("hud", {}))  # a profile that tests the HUD itself switches it on here
json.dump(hud, open(f"{data}/ModConfig/komet-hud.json", "w"), indent=2)

json.dump(flat, open(f"{run}/bench.json", "w"), indent=2)

# The time budget only; the defaults are the mod's (BenchConfig.cs), a profile overrides them
settle, lap = flat.get("settle", {}), flat.get("lap", {})
per_lap = settle.get("lapSeconds", 10) + lap.get("still", 10) + lap.get("rotate", 20) + 2 * lap.get("leg", 40) + 2 * lap.get("turn", 2)
laps = flat.get("laps", 8) + flat.get("warmupLaps", len(flat.get("arms", [None])))
print(int(300 + settle.get("seconds", 150) + laps * per_lap * 1.2 + 300))
for key, value in env.items():
    print(f"{key}={value}")
PY
)
((${#PLAN[@]} > 0)) || die "could not prepare the sandbox from $CONFIG"
BUDGET=${TIMEOUT:-${PLAN[0]}}
EXTRA_ENV=("${PLAN[@]:1}")
ENV_TEXT=
((${#EXTRA_ENV[@]} == 0)) || ENV_TEXT=", env ${EXTRA_ENV[*]}"

echo "bench: run $RUN, profile $PROFILE, mod $MOD${REVISION:+ ($REVISION)}, budget ${BUDGET}s$ENV_TEXT"
# timeout makes itself a process-group leader and signals the whole group, so run.sh's child gets the TERM too;
# ClientProgram maps SIGTERM to WindowExit(HardExit)
env "${EXTRA_ENV[@]}" KOMET_BENCH="$RUN/bench.json" \
  timeout -s TERM -k 60 "$BUDGET" "$GAME/run.sh" --dataPath "$RUN/data" --logPath "$RUN/logs" -o bench \
  >"$RUN/logs/stdout.log" 2>&1 &
PID=$!

# Watchdog: no status file in time means the game hangs before the mods load (a login screen, for instance), a status stuck at
# "loaded" means the world never finalized. Once result.json is there the game gets a minute to leave on its own.
START=$SECONDS
STATUS=
while kill -0 "$PID" 2>/dev/null; do
  # the mod rewrites the file in place, so a read can land between truncate and write: keep the last status seen
  READ=$(cut -d' ' -f1 "$RUN/result.json.status" 2>/dev/null || true)
  [[ -n $READ ]] && STATUS=$READ
  if [[ -f $RUN/result.json ]]; then
    for _ in $(seq 60); do kill -0 "$PID" 2>/dev/null || break; sleep 1; done
    kill -0 "$PID" 2>/dev/null && { echo "bench: the game did not exit after the result, stopping it" >&2; stop; }
    break
  fi
  if { [[ -z $STATUS ]] || [[ $STATUS == loaded ]]; } && ((SECONDS - START > BOOT_TIMEOUT)); then
    echo "bench: no world after ${BOOT_TIMEOUT}s (status '${STATUS:-none}'), stopping the game" >&2
    stop
    break
  fi
  sleep 2
done
set +e
wait "$PID"
CODE=$?
PID=
set -e
reap

[[ -f $RUN/logs/client-crash.log ]] && echo "bench: the client crashed, see $RUN/logs/client-crash.log" >&2
[[ $CODE == 124 ]] && echo "bench: the time budget of ${BUDGET}s ran out" >&2
if [[ ! -f $RUN/result.json ]]; then
  echo "bench: no result, logs in $RUN/logs (exit code $CODE)" >&2
  exit 1
fi

python3 - "$RUN/result.json" <<'PY'
import json, sys
result = json.load(open(sys.argv[1]))
revision = result.get("config", {}).get("revision")
print(f"bench: complete={result.get('complete')} error={result.get('error')}" + (f" revision={revision}" if revision else ""))
def show(value, digits=2):
    return "-" if value is None else f"{value:.{digits}f}"
for arm in result.get("arms", []):
    pooled = arm["pooled"]["all"]
    print(f"  {arm['name']:>10}: n={pooled['n']:>7} avg {show(pooled['avgMs'])} ms  p99 {show(pooled['p99Ms'])} ms  "
          f"1% low {show(pooled['low1Fps'], 1)}  0.1% low {show(pooled['low01Fps'], 1)} fps  >25ms/min {show(pooled['over25PerMin'], 1)}")
for delta in result.get("deltas", []):
    parts = []
    for name, metric in delta["metrics"].items():
        mean, se = metric.get("mean"), metric.get("se")
        t = f" t {mean / se:.1f}" if mean is not None and se else ""
        parts.append(f"{name} {show(mean, 3)}±{show(se, 3)}{t}")
    print(f"  {delta['arm']} - {delta['vs']}, mean±se over {delta['blocks']} blocks: " + ", ".join(parts))
sys.exit(0 if result.get("complete") else 1)
PY
echo "bench: result $RUN/result.json"
