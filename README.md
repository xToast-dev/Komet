# Komet

Performance-Mod für Vintage Story (2.0, C#).

## Bauen

Voraussetzungen:

- .NET SDK 10 (siehe `global.json`)
- Eine Vintage-Story-Installation; der Pfad wird über `VsInstall` übergeben (Standard: `/opt/vintagestory`)

```bash
./build.sh                          # Release, Standardpfad
./build.sh --configuration=Debug
VsInstall=/pfad/zu/vintagestory ./build.sh
```

Windows: `.\build.ps1` mit denselben Argumenten. Die Umgebungsvariable `VsInstall` kann alternativ
als MSBuild-Property gesetzt werden: `dotnet build -p:VsInstall=C:\Games\VintageStory`.

Das Ergebnis liegt unter `Releases/komet_<version>.zip`. Der Build validiert vorher die JSON-Assets
und die Power-of-Ten-Regeln (siehe `CakeBuild/Program.cs`); ein Verstoß bricht den Build ab.

## Tests

```bash
dotnet test
```
