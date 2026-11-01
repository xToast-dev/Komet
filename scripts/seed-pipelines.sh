#!/usr/bin/env bash
# Renews the pipeline recipes Komet ships (Komet/assets/komet/config/pipelines.json), which let a first Vulkan-only start
# build its pipelines before the first draw. A Vulkan-only bench run with an empty seed records what the session draws,
# and that becomes the seed. Run it after changing a shader patch: recipes are keyed by the shaders' sources, so a stale
# seed starts nothing (the client log then says "0 scene and 0 terrain pipelines begun before their first draw").
#
#   scripts/seed-pipelines.sh [bench options, e.g. --config FILE --profile NAME]
set -euo pipefail
cd "$(dirname "$0")/.."

empty=$(mktemp --suffix=.json)
trap 'rm -f "$empty"' EXIT
echo '{}' >"$empty"
scripts/bench.sh --build --hud VulkanOnly=true --label seed "$@" \
  --data-file "$empty=Mods/komet/assets/komet/config/pipelines.json"

run=$(ls -td ~/.cache/komet-bench/runs/*-seed | head -1)
python3 - "$run/data/Cache/komet-vulkan/pipelines.json" Komet/assets/komet/config/pipelines.json <<'EOF'
import json, sys
recipes = json.load(open(sys.argv[1]))
json.dump(recipes, open(sys.argv[2], "w"), separators=(",", ":"))
print(f"seed: {len(recipes)} programs, {sum(map(len, recipes.values()))} pipelines")
EOF
