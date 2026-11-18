#!/bin/sh
# Release build: the JSON parse, the Komet.Rules analyzer, the compile with every warning as an error, then Releases/komet and
# komet_<version>.zip, the Shader Studio (Komet.Sdk) inside
exec dotnet build "$(dirname "$0")/Komet/Komet.csproj" -c Release -t:Package "$@"
