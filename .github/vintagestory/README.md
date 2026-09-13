# Game assemblies for the CI build

`Komet.csproj` compiles against Vintage Story's own assemblies. They are not on a public NuGet
feed, so the ones the build needs live here, with the kind permission of Anego Studios.

`.github/workflows/build.yml` points `VsInstall` at this folder; the csproj resolves its
references from `$(VsInstall)`. Locally nothing changes: `VsInstall` defaults to
`/opt/vintagestory`, i.e. a normal game installation.

## Expected layout

Only what the csproj references, in the layout of the game folder:

```
.github/vintagestory/
  VintagestoryAPI.dll
  VintagestoryLib.dll
  Lib/
    0Harmony.dll
    cairo-sharp.dll
    Newtonsoft.Json.dll
    OpenTK.Graphics.dll
```

A new reference in `Komet.csproj` needs its dll added here and to the check list in
`build.yml`, otherwise CI fails on the first unresolved type.

## What is actually in here

Copied from a 1.22.7 installation on 2026-09-14, byte-identical to the source. 11 MB in total.

Run this from inside `.github/vintagestory` to confirm that what is here is still what was
verified. It prints nothing and exits 0 when everything matches:

```bash
sha256sum --check --quiet <<'EOF'
034283e7e9d98eae45ee63005576fd89badc3c995b531cc4c3fe46f3eb2d3296  VintagestoryAPI.dll
e08f22b493b92feaf0aaeb79d22437ea0f7efc38aa7f72a04a47f98bc0e40df0  VintagestoryLib.dll
64b8b8f926efe5b926b832aaedb93e73de34af2e84d9f8f5d51c6fb70ab61149  Lib/0Harmony.dll
a8d3645f639a9d02b1f0220b8e24135f799868b30a253cefa9769918eb2c8af0  Lib/cairo-sharp.dll
a28c251dfe36d881e9e2462e171441b8b0ec156fe3f452602c9149b1b9efe05b  Lib/Newtonsoft.Json.dll
5b57957ca9e4c5f7bfb7ec495b991e6b88cfac52863a19e405492ab8c33cb1d2  Lib/OpenTK.Graphics.dll
EOF
```

Run the same block in the game folder (`cd /opt/vintagestory`) to check a fresh drop before
copying it in. After a game update: replace the files here, update the hashes above and the
`game` dependency in `Komet/modinfo.json`, and commit together.
