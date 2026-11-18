# Komet optional integrieren

## Brauche ich eine Integration?

Mods, die normale Spielpfade nutzen, können bereits von den dort aktiven Komet-Optimierungen profitieren. Eigener
Integrationscode lohnt sich, wenn deine Mod einen eigenen Renderer oder Shader-Effekt in Komets Renderpfad einhängen
soll oder eine Seite in Komets Optionen möchte. Beginne mit einem Effekt.

## Projekt einrichten

Erstelle im C#-Projekt einen Ordner `Komet/`. Kompiliere die Bridge in deine normale Mod-DLL. Referenziere
`Komet.Core.dll` mit `Private="false"` und liefere sie nicht mit deiner Mod aus: Der Komet des Spielers bringt sie mit.
Lass Komet aus den Pflichtabhängigkeiten in `modinfo.json` heraus.

## Genau einen Pfad starten

Ersetze `config` und `StartNormalRenderer` durch deine eigenen Teile. Der normale Start muss ein Handle zurückgeben,
das seine Renderer und Hooks wieder entfernt. Nur die Bridge nennt KAPI-Typen (`Komet.Api`), und das Client-System ruft
sie nur auf, wenn Komet aktiv ist; so lädt deine Mod auch ohne Komet. Die Bridge verbindet sich mit der KAPI-Version, für
die sie geschrieben ist; deine Mod-ID kommt aus der `modinfo.json`.

```csharp
// Client-System. Halte jeden KAPI-Typ aus dieser Datei heraus.
private IDisposable? _own;

public override void StartClientSide(ICoreClientAPI api)
{
    if (config.UseKomet && api.ModLoader.IsModEnabled("komet"))
    {
        try { TryKomet(api); return; }
        catch (Exception e) when (e is System.IO.FileNotFoundException or
            System.IO.FileLoadException or TypeLoadException or MissingMethodException)
        {
            api.Logger.Warning("mymod: optional Komet API unavailable: {0}", e.Message);
        }
    }

    StartOwn(api);
}

[System.Runtime.CompilerServices.MethodImpl(
    System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
private void TryKomet(ICoreClientAPI api) =>
    KometBridge.Start(this, config.FragmentSource, () => StartOwn(api));

private void StartOwn(ICoreClientAPI api) => _own = StartNormalRenderer(api);

public override void Dispose()
{
    _own?.Dispose();
    _own = null;
    base.Dispose();
}
```

```csharp
// Komet/KometBridge.cs
using System;
using Komet.Api;
using Vintagestory.API.Common;

internal static class KometBridge
{
    internal static void Start(ModSystem system, string source, Action startOwn)
    {
        var komet = Kapi.Connect(system, "1.0");
        if (komet is null) { startOwn(); return; } // kein KAPI 1.0: das Log sagt, warum

        var fx = komet.Shaders.TryAddPasses(new KapiPass("grade", source));
        if (!fx.Active) startOwn(); // fx.Error und das Log sagen, warum
    }
}
```

## In Komets Frame zeichnen

Ein eigener Renderer kann als Render-Schritt in Komets Frame sitzen, statt sich selbst beim Spiel anzumelden:
`komet.Rendering.AddStep(new KapiRenderStep("outline", KapiRenderStage.AfterTransparent, Draw))`. Komet misst ihn,
verbucht ihn in `.komet debug`, schaltet ihn für die Welt aus, wenn er eine Ausnahme wirft, und der Spieler kann deine
Schritte ausschalten. Jede verbundene Mod steht mit ihren Schritten und deren Kosten auf der Seite Verbundene Mods
(*Übersicht › Verbundene Mods*); eine Mod, die Komet abgelehnt hat, steht dort auch, mit dem Grund.

## Fallback prüfen

- Ohne Komet oder bei ausgeschaltetem Schalter startet der normale Pfad.
- Bietet Komet kein KAPI 1.0, gibt `Connect` null zurück: Der normale Pfad startet, und das Log meldet
  `KAPI did not connect` mit beiden Versionen.
- Lehnt `TryAddPasses` den Effekt ab, ist `fx.Active` false. Der normale Pfad startet, `fx.Error` und das Log nennen
  den Grund.
- Klappt es, gehört der Effekt Komet, bis die Welt schließt; das nächste `StartClientSide` verbindet neu. Teste
  Darstellung und Weltwechsel auf beiden Renderern, die du unterstützen willst.

[API-Referenz und Versionen](docs-api)

[Shader-API und Renderpfade](docs-api-shaders)

[Render-Schritte](docs-api-rendering)
