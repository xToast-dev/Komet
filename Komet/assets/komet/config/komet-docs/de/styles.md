# Menü-Stile

Komet zeichnet sein Optionsmenü in einem **Stil**. Ein Stil legt Farben, Schriften, Größen, Logo und Hintergrund fest. Komet bringt zwei Stile mit: **Pragmatic** (oben Reiter, darunter Seitenliste, Zeilen und Details vor einem Planeten im Orbit) und **Vintage** (das Braun, Pergament und die Schriften Lora und Montserrat der Spieldialoge vor der verschwommenen Welt).

Du wählst den Stil unter Komet › Stil › **Stil**. Er wechselt sofort, du musst nicht **Übernehmen** drücken. Der Stil gibt seine Farben und Schriften auch an Komets HUD und dessen Fenster weiter. Jeder kann weitere Stile bauen, ganz ohne Code.

**Aussehen und Name von Pragmatic sind eine Anspielung auf die Menüs von Capcoms Spiel Pragmata. Komet hat keine Verbindung zu Capcom und wird nicht von Capcom unterstützt.**

## Schnellstart

Du brauchst weder Code noch einen Neustart. Fang mit einer von zwei Vorlagen an:

- Modding › Vorlagen › **Beispiel-Stil anlegen** schreibt einen kleinen Stil nach `ModConfig/komet/styles/example/` und wählt ihn gleich aus. Er ändert nur vier Farben und eine Größe.
- Komet › Stil › **Diesen Stil kopieren** kopiert den Stil, den du gerade nutzt, nach `ModConfig/komet/styles/<id>-copy/` (ist der Name vergeben: `<id>-copy2` und so weiter). Komet wählt die Kopie aus und öffnet den Ordner. Die Kopie schreibt alle Farben, Größen und Schriften aus, dazu `background.fsh`, wenn der Stil einen Hintergrund-Shader hat. Logo und Schriftdateien erbt sie vom Original.

Danach geht es immer gleich weiter:

```flow
Vorlage anlegen
style.json im Editor ändern
Datei speichern
Komet › Stil › Stile neu laden
Neues Aussehen sofort sehen
```

Fehler stehen in `client-main.log`, in Zeilen mit `Komet styles:`.

## Woher Stile kommen

Komet sucht Stile an drei Stellen, in dieser Reihenfolge. Haben zwei Stile dieselbe ID, gewinnt der spätere.

| Quelle | Ort |
|---|---|
| Assets jeder Mod | `assets/<domain>/config/komet-styles/<id>/style.json` |
| Dein Ordner | `ModConfig/komet/styles/<id>/style.json` |
| Code | `KometStyles.Register(...)` |

- Die ID ist der Name des Ordners in Kleinbuchstaben, es sei denn, `style.json` setzt `"id"`.
- Aus `ModConfig/komet/styles` liest Komet bis zu 32 Ordner.
- Dateinamen in einem Stil (Logo, Shader, Schriften) beziehen sich auf den Ordner des Stils. Mit Doppelpunkt nennen sie direkt ein Asset, zum Beispiel `komet:config/fonts/SairaSemiCondensed-Regular.ttf`.
- Eine Inhalts-Mod braucht keinen Code: Sie legt den Stil-Ordner in ihre Assets, und jeder Spieler bekommt den Stil mit der Mod.

## Der Beispiel-Stil

**Beispiel-Stil anlegen** (Modding › Vorlagen) legt diese `style.json` an. Ein Stil nennt nur, was er ändert. Alles andere kommt aus seinem Ausgangsstil.

```json
{
  "$schema": "https://raw.githubusercontent.com/xtoast-dev/Komet/main/docs/schema/komet-style.schema.json",
  "id": "example",
  "name": "komet:example-style",
  "author": "Komet",
  "base": "pragmatic",
  "colors": {
    "accent": "#E8A23A",
    "select": "#B8741C",
    "title": "#F2C27A",
    "changed": "#FFD08A"
  },
  "sizes": {
    "tracking": 4
  }
}
```

| Stelle | Was sie tut |
|---|---|
| `$schema` | Dein Editor schlägt damit beim Tippen die Schlüssel vor. Komet selbst braucht die Zeile nicht |
| `id` | Der Name, unter dem Komet den Stil kennt |
| `name` | Steht in der Stilauswahl. `komet:example-style` ist ein Sprachschlüssel, normaler Text geht auch |
| `author` | Freier Text |
| `base` | Der Ausgangsstil, hier Pragmatic. Was hier fehlt, sieht aus wie dort |
| `accent` | Aktive Bedienelemente, Unterstrich des Reiters und Aufblitzen, hier Bernstein |
| `select` | Der Hintergrund der ausgewählten Zeile |
| `title` | Überschriften und Knopfbeschriftungen |
| `changed` | Änderungen, die du noch nicht übernommen hast, und ihre Anzahl |
| `tracking` | Der Buchstabenabstand des Schriftzugs KOMET und der Seitentitel |

Die Datei hat dazu eine Zeile `_comment` mit einer Notiz; Komet überspringt sie. Ändere eine Farbe, speichere und drück unter Komet › Stil auf **Stile neu laden**.

## style.json

| Schlüssel | Bedeutung |
|---|---|
| `id` | Eindeutiger Name; ohne ihn der Name des Ordners |
| `name` | Steht in der Stilauswahl: Text oder Sprachschlüssel (`domain:schlüssel`); ohne ihn die ID |
| `author` | Freier Text |
| `base` | Ausgangsstil: `pragmatic` (Standard) oder eine andere ID |
| `colors` | Farben als `#RRGGBB` oder `#RRGGBBAA`, Schlüssel unten |
| `fonts` | Schriftfamilien, Schlüssel unten; `""` nimmt die Schrift des Spiels |
| `fontFiles` | `.ttf`- oder `.otf`-Dateien, die Komet vor dem Zeichnen anmeldet |
| `sizes` | Größen in GUI-Pixeln vor der GUI-Skalierung des Spiels, Schlüssel unten |
| `logo` | PNG, GIF oder WebP für den Kopf |
| `background` | `blur` (Standard), `none` oder eine Shader-Datei |

- Nennt `base` die eigene ID des Stils, meint Komet die Fassung, die es davor gefunden hat. So baut ein Ordner `pragmatic/` mit `"base": "pragmatic"` Pragmatic selbst um.
- Ein Ausgangsstil darf selbst wieder einen Ausgangsstil haben, bis zu 8 Stufen tief. Fehlt einer oder dreht sich die Kette im Kreis, steht das im Log.
- Unbekannte Schlüssel in `colors`, `fonts` und `sizes` sowie ungültige Farben stehen im Log, damit ein Tippfehler auffällt. Unbekannte Schlüssel auf oberster Ebene überspringt Komet ohne Meldung; da hilft die Zeile `$schema`.

### Farben

| Schlüssel | Wofür |
|---|---|
| `backdrop` | Abdunklung der Welt unter Shader oder Unschärfe |
| `backdropPlain` | Abdunklung, wenn weder Shader noch Unschärfe zu sehen sind |
| `panel` | Flächen |
| `band` | Kopf, Fuß, Abschnittsbänder, Knöpfe |
| `row` | Hintergrund der Zeilen (reserviert; Pragmatic zeichnet keinen) |
| `line` | Haarlinien, Umrisse, Kästchen |
| `text`, `soft`, `faint` | Haupt-, Neben- und gedimmter Text |
| `accent` | Aktive Bedienelemente, Unterstrich des Reiters, Aufblitzen |
| `select` | Hintergrund der ausgewählten Zeile |
| `selectText` | Text und Eckmarken der ausgewählten Zeile |
| `title` | Überschriften, Knopfbeschriftungen |
| `changed` | Nicht übernommene Änderungen, ihre Anzahl |
| `good`, `bad` | Gut und schlecht im HUD; im Menü `good` für „Kopiert“ und für Mods, die mit Komet sprechen |
| `glow` | Leuchten unter der Maus |
| `cell`, `chip`, `bubble` | Unbenutzt, Wert-Chips, Blase am Regler |

`panel`, `band` und `row` werden mit Komet › Stil › **Deckkraft der Flächen** dichter oder durchsichtiger.

### Schriften

Vier Schlüssel: `text` (Zeilen, Hinweise), `heading` (Zeilentitel, Reiter, Bedienelemente), `title` (Seitentitel, Schriftzug KOMET) und `numbers` (der große Wert in den Details).

- Pragmatic bringt **Saira Semi Condensed** mit (SIL Open Font License) und nennt die Familien `Saira SemiCondensed` und `Saira SemiCondensed Medium`.
- Eine Familie in `fonts` ohne passende Datei in `fontFiles` muss auf dem Rechner des Spielers installiert sein.
- Unter Linux meldet Komet Schriftdateien bei fontconfig an, unter Windows als private Schriften. Geht das nicht (etwa unter macOS), springt die Schrift des Spiels ein.
- Willst du aus Saira eine eigene Schrift machen, ändere sie in einem Schrifteditor und gib der Familie einen neuen Namen: Die OFL verbietet, den reservierten Namen „Saira“ zu behalten.

### Größen

| Schlüssel | Bedeutung |
|---|---|
| `header`, `footer` | Höhe von Kopf und Fuß |
| `logo` | Längste Seite des Logos im Kopf des Menüs; `0` blendet es dort aus |
| `row` | Zeilenhöhe |
| `gap` | Abstand zwischen den Flächen |
| `ticks` | Größe der Eckmarken an Flächen, über `0` auch an der ausgewählten Zeile; `0` zeigt keine |
| `tracking` | Buchstabenabstand des Schriftzugs KOMET und der Seitentitel |
| `scene` | Wie stark der Shader die Welt bedeckt, 0 bis 1 |
| `veil` | Wie stark Komet den Shader hinter den Flächen abdunkelt, 0 bis 1 |
| `pan`, `slide`, `decode` | `1` schaltet Kamerafahrt, hereingleitende Listen und sich aufbauende Namen ein |
| `corner`, `pad` | Reserviert: erlaubt, sie wirken aber noch nicht |

Komet › Stil › **Animationen** hat bei `pan`, `slide` und `decode` immer das letzte Wort. Steht diese Einstellung auf **Aus**, bewegt sich nichts.

## Das Logo

Der Kopf zeigt das erste vorhandene Bild aus dieser Reihe: das `logo` des Stils (auch geerbt vom Ausgangsstil), dann `ModConfig/komet/logo.png` (deins, in jedem Stil), dann Komets Symbol. Das Logo erscheint auch im Kopf von Komets HUD-Fenstern.

Komet vergrößert ein Logo nur in ganzen Schritten (doppelt, dreifach) und verkleinert es auf die Hälfte, ein Drittel und so weiter. So bleibt Pixelgrafik scharf. Dabei zählt die längere Seite; am besten zeichnest du das Logo quadratisch. Eine Logo-Datei darf höchstens 4 MiB groß sein.

## Hintergrund-Shader

`background` nennt einen Fragment-Shader in GLSL 330. Schreib nur deine Funktionen und `main()`. Komet setzt einen Vorspann davor, der alle Eingaben unten schon deklariert. Zeilennummern in Fehlermeldungen beziehen sich auf deine Datei. Die Datei darf höchstens 64 KiB groß sein.

### Eingaben

| Name | Bedeutung |
|---|---|
| `uv` | Stelle im Bild, 0 bis 1, (0,0) unten links |
| `color` | Deine Ausgabe; Alpha sagt, wie stark sie die Welt bedeckt |
| `time` | Sekunden seit dem Öffnen des Menüs |
| `fade` | 0 bis 1 beim Einblenden, schon mit `scene` malgenommen |
| `page` | Offene Seite als Abschnitt × 10 + Seite; gleitet weich nur mit `pan` |
| `resolution` | Bildgröße in Pixeln |
| `camera` | x- und y-Versatz und Zoom, gleitet je Seite (nur mit `pan`) |
| `accent` | Die Akzentfarbe des Stils |
| `scene` | Die verschwommene Welt (Sampler) |
| `world(at)` | Die verschwommene Welt an der Stelle `at`, gezählt wie `uv` |

### In einer Welt

Sobald du in einer Welt bist, gibt es mehr:

- `worldAt(at)` liefert die Weltkarte, die du erkundet hast: ein Quadrat um die Stelle, an der Komet die Karte gelesen hat (x nach Osten, y nach Süden). Alpha ist 0, wo nichts erkundet ist. Erlaubt der Server keine Karte, bleibt sie leer.
- `worldTint(kind, own)` liefert die Durchschnittsfarbe des Bodens um dich herum für 0 Wasser, 1 Sand, 2 Fels, 3 Schnee, 4 Grün. Kennt Komet für eine Art keine Farbe oder gibt es keine Weltkarte, kommt `own` zurück.
- `worldPlace`: Breitengrad, Stundenwinkel der Sonne ab Mittag und ihre Deklination (alles im Bogenmaß); w ist 1 in einer Welt.
- `worldWeather`: Bewölkung und Niederschlag (0 bis 1), Temperatur in °C / 40; w ist 1 in einer Welt.
- `planet` gleitet auf 1, sobald die Weltkarte gelesen ist, und auf 0 ohne Karte.
- `worldScale`: x ist der Planetenradius in Blöcken (die Breite der Welt ist der Umfang), y die Reichweite der Karte in Blöcken, z und w dein Abstand von der Kartenmitte.
- `worldChunk`: x und y die Lage der Kartenmitte in ihrem Chunk, z und w die Nummer dieses Chunks.
- `worldYaw`: deine Blickrichtung im Bogenmaß.

### Was Komet danach tut

- Schreib `fade` ins Alpha, um die Welt ganz zu bedecken.
- Nach deinem `main()` dunkelt Komet das Bild hinter den Flächen um `veil` ab und macht es dort etwas grauer. Dein Shader muss dafür nichts tun.
- Der Vorspann deklariert außerdem `panels`, `panelCount`, `veil`, `worldMap`, `worldKnown` und `worldTints`. Deklariere diese Namen nicht noch einmal.
- Kompiliert der Shader nicht, stehen die Fehler im Log, und die Unschärfe springt ein. **Stile neu laden** kompiliert ihn neu.

Ein vollständiges Beispiel ist der Shader von Pragmatic. Am leichtesten bekommst du ihn mit **Diesen Stil kopieren**, während Pragmatic aktiv ist: Die Kopie enthält ihn als `background.fsh`.

## Aus Code

Ruf das in `StartClientSide` auf, und nur, wenn `api.ModLoader.IsModEnabled("komet")` wahr ist (siehe „API für Mods“):

```csharp
KometStyles.Register(new KometStyle
{
    Id = "mymod-night",
    Name = "Night",
    Base = "pragmatic",
    Domain = "mymod",
    Colors = { ["accent"] = "#7A5CFF", ["select"] = "#4B33B8" },
    Sizes = { ["tracking"] = 4 },
    Logo = "logo.png",
    Background = "background.fsh" // or BackgroundSource = shaderText
});
```

- `KometStyle` hat dieselben Felder wie `style.json`: `Id`, `Name`, `Author`, `Base`, `Colors`, `Fonts`, `FontFiles`, `Sizes`, `Logo` und `Background`. Dazu kommen `BackgroundSource` (der Shader als Text; hat Vorrang vor `Background`), `Folder` und `Domain`.
- Relative Dateien sucht Komet in `Folder` (ein Ordner auf der Festplatte), sonst in den Assets von `Domain` unter `config/komet-styles/<Id>/`. Im Beispiel liegt das Logo also unter `assets/mymod/config/komet-styles/mymod-night/logo.png`. Fehlen beide, gehen nur Asset-Namen mit Doppelpunkt und absolute Pfade.
- Meldest du eine schon vergebene ID an, ersetzt das den Stil. Danach feuert `KometStyles.Changed`. Aus Code kannst du bis zu 32 Stile anmelden (`KometStyles.MaxStyles`).
- Stile aus Code verschwinden, wenn die Welt schließt. In der nächsten Welt meldest du sie neu an.
