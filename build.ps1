dotnet build "$PSScriptRoot/Komet/Komet.csproj" -c Release -t:Package @args
exit $LASTEXITCODE
