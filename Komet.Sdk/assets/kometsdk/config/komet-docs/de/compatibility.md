# Kompatibilität reparieren

[Probleme eingrenzen](docs-problems)

## Den Befund sichern

1. Das Problem mit denselben Spiel-, Komet- und Modversionen nachstellen.
2. Komet > Kompatibilität öffnen und Befunde aktualisieren wählen. Einzelne Einträge kopieren ihre Details;
   Kompatibilitätsbericht kopieren übernimmt alle erfassten Einträge.
3. Spielversion, Komet-Version, Modversionen, client-main.log und ein Debug-Protokoll hinzufügen.
4. Nach dem Aktivieren oder Pausieren einer Shader-Mod die Seite erneut öffnen oder aktualisieren. Der Bericht ist eine
   Momentaufnahme.

## Die Ursache einordnen

| Befund                          | Bedeutung und nächster Schritt                                                                                                                                                 |
|---------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Teilpfad-Hinweis                | Ein Feature kann Active bleiben, während ein geschützter Teilpfad das Original verwendet. Die Methodendetails zeigen den betroffenen Pfad.                                     |
| HeldOff                         | Eine Mod fordert Originalverhalten an. HeldBy und Reason nennen Anforderer und Grund. Das ist häufig beabsichtigte Zusammenarbeit. Prüfen, ob die Sperre noch nötig ist.       |
| StoodDown                       | Eine Schutzprüfung lässt das Feature zurücktreten. Die Methodendetails zeigen vorhandene Harmony-Patches oder fehlende Ziele. Ohne Details zusätzlich das Log prüfen.          |
| EngineChanged                   | Erwartete Methoden oder deren Fingerprint stimmen nicht. Auch eine andere Spielversion ist möglich. Expected und found sowie die vollständigen Methodensignaturen vergleichen. |
| Failed                          | Feature-Code hat einen Fehler gemeldet. Die Ausnahme und ihre Aufrufkette in den Details oder im Log untersuchen.                                                              |
| Externer Harmony-Patch an Komet | Eine Komet-Methode wird unter einer fremden Harmony-ID gepatcht. Das allein beweist keinen Fehler. Zielmethode, Patch-Methode und Patch-Art prüfen.                            |

Harmony-IDs sind frei gewählte Patch-Kennungen. Sie müssen nicht dem Modnamen entsprechen. Eine abweichende
Paket-Prüfsumme zeigt ebenfalls nicht, wer eine Datei verändert hat. Änderungen ohne Harmony werden mit dieser Anzeige
nicht vollständig erkannt.

## Harmony-Konflikte reparieren

Im Bericht stehen die Zielsignatur, Harmony-ID, Patch-Methode, Patch-Art und gegebenenfalls Priorität. Die Harmony-ID
zuerst im eigenen Code oder im Debug-Protokoll dem verantwortlichen Patch zuordnen.

- Prüfen, ob ein Prefix das Original überspringt oder ein Transpiler ein von Komet ersetztes Verhalten voraussetzt.
- Nach Möglichkeit KAPI (`Komet.Api`) verwenden, wo es den Fall abdeckt. KAPI ist versioniert und hält sein Versprechen
  innerhalb einer Hauptversion; interne Komet-Methoden können sich zwischen zwei beliebigen Versionen ändern.
- Wenn ein Postfix genügt, keinen ersetzenden Prefix verwenden. HarmonyBefore und HarmonyAfter ordnen Patches, lösen
  aber keinen inhaltlichen Widerspruch.
- Benötigt die Mod das Originalverhalten, nur das betroffene Feature mit `komet.Features.HoldOff` sperren. Die Sperre auf
  dem Hauptthread anlegen und freigeben, auch bei teilweise fehlgeschlagener Initialisierung.

```csharp
// in deiner KAPI-Bridge, mit der Verbindung aus Kapi.Connect(system, "1.0")
KapiHold? hold;

void Attach(KapiMod komet)
{
    hold = komet.Features.HoldOff("FrustumSweep",
        "Unser Renderpass benötigt das Original-Culling.");
}

void Detach()
{
    hold?.Dispose(); // komet.Dispose() und das Weltende lösen sie auch
    hold = null;
}
```

## Abweichende Methoden reparieren

Bei EngineChanged zuerst die unterstützte Spielversion und die Auflösung überladener Methoden prüfen. Vollständige
Parameterlisten angeben und fehlende Ziele behandeln. Den Fingerprint erst nach Prüfung und Tests des neuen Engine-Codes
aktualisieren. Den Schutz nicht durch ungeprüftes Übernehmen des gefundenen Wertes umgehen.

Bei Failed die erste eigene Stelle in der Aufrufkette korrigieren. Uninstall muss auch teilweise erzeugte Ressourcen
sicher freigeben. Komet ruft diese Bereinigung nach einer begonnenen Installation bei Fehler oder Weltende einmal auf.

## Die Korrektur prüfen

Die betroffene Szene mit beiden Mods, mit deaktiviertem Komet-Feature und nach einem Weltwechsel testen. Bei Shader-Mods
zusätzlich Aktivieren, Pausieren und erneutes Aktivieren prüfen. Danach den Bericht aktualisieren: gelöste Konflikte
müssen verschwinden oder als beabsichtigte Sperre erkennbar sein. Einen neuen Bericht und das Log zur Fehlerbehebung
beilegen.
