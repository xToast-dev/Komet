# Game assemblies for the CI build

Vintage Story's assemblies are not on NuGet, so the ones `Komet.csproj` references live here (with the kind permission of Anego
Studios), copied byte-identical from a 1.22.7 installation. CI points `VsInstall` at this folder and checks `SHA256SUMS` first;
locally `VsInstall` defaults to `/opt/vintagestory`.

After a game update or a new reference: replace or add the dlls, regenerate `SHA256SUMS`, update the `game` dependency in
`Komet/modinfo.json`, and commit together.

```bash
sha256sum --check --strict SHA256SUMS
```
