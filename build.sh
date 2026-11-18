#!/bin/sh
# Release build: the JSON parse, the Komet.Rules analyzer, the compile with every warning as an error, then Releases/komet and komet_<version>.zip
set -e
dotnet build "$(dirname "$0")/Komet/Komet.csproj" -c Release -t:Package "$@"
# the SDK (kometsdk, the shader workbench) as its own zip beside Komet's
exec dotnet build "$(dirname "$0")/Komet.Sdk/Komet.Sdk.csproj" -c Release -t:Package "$@"
