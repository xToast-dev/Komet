# Game assemblies for the CI build

`Komet.csproj` compiles against Vintage Story's own assemblies. They are not on a public NuGet
feed, so the ones the build needs live here, with the kind permission of Anego Studios.

`.github/workflows/build.yml` points `VsInstall` at this folder; the csproj resolves its
references from `$(VsInstall)`. Locally nothing changes: `VsInstall` defaults to
`/opt/vintagestory`, i.e. a normal game installation.

Only what `Komet.csproj` references is here, in the layout of the game folder. `SHA256SUMS`
lists every file with its hash; CI checks it before the build, so a missing or changed file
fails by name. Copied from a 1.22.7 installation, byte-identical to the source.

To check the files here, or a fresh game drop before copying it in, run from inside this folder
or the game folder:

```bash
sha256sum --check --strict /path/to/.github/vintagestory/SHA256SUMS
```

A new reference in `Komet.csproj` needs its dll added here and a line in `SHA256SUMS`. After a
game update: replace the files, regenerate `SHA256SUMS`, update the `game` dependency in
`Komet/modinfo.json`, and commit together.
