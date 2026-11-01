#!/usr/bin/env bash
# The developer site (docs/site): API reference from Komet.Api's XML comments plus the articles, built with DocFX like
# apidocs.vintagestory.at. Also packs the Vulkan shader template as a download.
#
#   scripts/apidocs.sh [--serve]
#
#   output      docs/site/_site (open index.html, or --serve for http://localhost:8080)
#
# DocFX runs on ASP.NET Core; without it on the system a private copy goes to ~/.cache/komet-bench/tools/dotnet-docs, and
# DocFX itself to ~/.cache/komet-bench/tools/docfx. Nothing is installed system wide.
set -euo pipefail
cd "$(dirname "$0")/.."
TOOLS=~/.cache/komet-bench/tools
DOCFX=$TOOLS/docfx/docfx
DOCFX_VERSION=2.81.0

if ! dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.'; then
  if [[ ! -d $TOOLS/dotnet-docs/shared/Microsoft.AspNetCore.App ]]; then
    mkdir -p "$TOOLS"
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "$TOOLS/dotnet-install.sh"
    bash "$TOOLS/dotnet-install.sh" --runtime aspnetcore --channel 10.0 --install-dir "$TOOLS/dotnet-docs" >/dev/null
    bash "$TOOLS/dotnet-install.sh" --runtime dotnet --channel 10.0 --install-dir "$TOOLS/dotnet-docs" >/dev/null
  fi
  export DOTNET_ROOT=$TOOLS/dotnet-docs
fi
[[ -x $DOCFX ]] || dotnet tool install --tool-path "$TOOLS/docfx" docfx --version "$DOCFX_VERSION" >/dev/null

# The template as a zip, the same files the in-game button writes
TEMPLATE=Komet/assets/komet/config/komet-templates/vulkan-example
mkdir -p docs/site/downloads
rm -f docs/site/downloads/komet-vulkan-template.zip
python3 - "$TEMPLATE" docs/site/downloads/komet-vulkan-template.zip <<'PY'
import os, sys, zipfile
source, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:  # one folder, myshaders-dev/: the mod and its code
    for root, _, files in os.walk(source):
        for name in sorted(files):
            path = os.path.join(root, name)
            # C# sources travel as .cs.txt in Komet's assets (the game refuses a zipped mod with .cs outside src/)
            z.write(path, os.path.join("myshaders-dev", os.path.relpath(path, source)).replace(".cs.txt", ".cs"))
PY

cd docs/site
rm -rf api/*.yml _site
"$DOCFX" metadata docfx.json
"$DOCFX" build docfx.json
echo "apidocs.sh: docs/site/_site/index.html"
if [[ ${1:-} == --serve ]]; then "$DOCFX" serve _site; fi
