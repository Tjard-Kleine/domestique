# Domestique – Umsetzungsplan

Oct 6, 2026 · @Tjard

## Kurzfassung

Die stabilste Kombination für Windows ist: **C# mit .NET (LTS) + WPF-Overlay, Trainer über Bluetooth LE mit dem Standardprotokoll FTMS**. Damit läuft die App ohne Zusatzhardware mit praktisch jedem aktuellen Smarttrainer.

- **Trainer:** FTMS über Bluetooth LE (Daten lesen, ERG, Steigungssimulation). Cycling Power Service nur als Lese-Fallback.
- **Puls:** separater Brustgurt über den Heart Rate Service, nicht über den Trainer.
- **Strecken:** GPX-Dateien, geglättet und in feste Abschnitte zerlegt.
- **Trainingspläne:** ZWO-Format (Zwift-Workout-XML), weil es dafür tausende fertige Workouts gibt.
- **Overlay:** randloses, halbtransparentes WPF-Fenster, immer im Vordergrund, optional klick-durchlässig.
- **Ziel-Ressourcen:** unter 1 % CPU, unter 100 MB RAM, UI-Update höchstens 1–2-mal pro Sekunde.

**Wichtige Korrektur zur Einschätzung im Chat:** Im ERG-Modus regelt der Trainer die Watt selbst. Deine App schickt nur die Zielleistung, eine eigene Regelschleife ist nicht nötig. Das macht ERG deutlich einfacher als zuerst gesagt.

**Aufwand (grobe Schätzung):** etwa 60–100 Stunden bis zur vollständigen Version. Die größte Unsicherheit steckt in Phase 1 (Bluetooth-Verbindung), danach wird es planbar.

## Anforderungs-Check

Stand 7. Oktober: Vier von fünf Anforderungen deckt der Plan ab. Das Einstellungsrad mit Bibliothek fehlte, ebenso die Fahrzeit in der Anzeige. Beides ist jetzt als Phase 9 ergänzt, die Architektur bleibt bei drei Schichten.

| Anforderung | Umsetzung | Phase | Status |
| --- | --- | --- | --- |
| Telemetrie anzeigen | Watt (3-s-Mittel), Trittfrequenz, Puls, Zielwatt, Speed, Distanz, Steigung, Fahrzeit | 1, 2, 5, 9 | Fahrzeit ergänzt |
| Karte mit Positionspunkt | Mini-Karte und Höhenprofil, der Punkt wandert mit der virtuellen Distanz | 6 | abgedeckt |
| Steigung aus Position, daraus Geschwindigkeit | `Route.At(Distanz)` liefert die Steigung, `RiderPhysics` die Geschwindigkeit, auch im ERG-Modus | 5 | abgedeckt |
| Trainingspläne steuern den Trainer | ZWO-Workout → WorkoutPlayer → Modus-Wächter → Set Target Power am Trainer | 3, 4 | abgedeckt |
| Einstellungsrad: Bluetooth, Farbe, Transparenz und mehr | Vorher nur `settings.json` von Hand, kein Fenster | 9 | neu |
| Trainingspläne und Strecken einfügen | Vorher nur „Datei öffnen“, keine Bibliothek | 9 | neu, mit Import und Drag & Drop |

**Dabei außerdem angepasst:**

- Gewicht und CdA sind zur Laufzeit änderbar (Phase 5: `set` statt `init`, `RouteSim` gibt die Physik heraus).
- Beim Wechsel des Trainers wird der Modus-Wächter zurückgesetzt, sonst hätte der neue Trainer die aktuellen Zielwerte nie bekommen.
- Kaputte Farbwerte in `settings.json` fallen auf Standardfarben zurück, Importe werden vor dem Kopieren geprüft. Ein Tippfehler legt die App also nicht lahm.

**Bewusst nicht enthalten:** Workouts nur im ZWO-Format, weil es das verbreitetste ist. ERG- und MRC-Dateien wären mit demselben Datenmodell in ein bis zwei Stunden nachrüstbar. Tastenkürzel sind fest belegt (Übersicht in Phase 9). Eine geänderte FTP gilt ab dem nächsten geladenen Workout.

## Protokollanalyse

FTMS über Bluetooth LE gewinnt: offen dokumentiert, ohne Zusatzhardware, und es deckt Lesen, ERG und Steigungssimulation in einem Protokoll ab. ANT+ FE-C ist technisch gleichwertig und funkt robuster, braucht aber einen USB-Stick.

| Protokoll | Transport | Liefert | Steuert | Rolle in der App |
| --- | --- | --- | --- | --- |
| FTMS (Service 0x1826) | Bluetooth LE | Watt, Trittfrequenz, Speed, Distanz | ERG, Steigung, Widerstandsstufe | **Hauptprotokoll** |
| Cycling Power (0x1818) | Bluetooth LE | Watt, Trittfrequenz (aus Kurbel-Umdrehungen) | nichts | Lese-Fallback, falls FTMS-Daten fehlen |
| Heart Rate (0x180D) | Bluetooth LE | Puls | nichts | Brustgurt, separates Gerät |
| ANT+ FE-C | ANT+-USB-Stick | wie FTMS | ERG, Steigung | Optionale Ausbaustufe bei Funkproblemen |
| Herstellerprotokolle (z. B. alte Wahoo- oder Tacx-Erweiterungen) | Bluetooth LE | wie FTMS | ja | Nur, wenn dein Trainer kein FTMS kann |

**Warum FTMS stabil genug ist:** Zwift und Kinomap steuern Trainer über genau diesen Weg, nämlich den Befehl „Set Indoor Bike Simulation Parameters“ (Opcode 0x11) am Fitness Machine Control Point ([Zwift-Forum](https://forums.zwift.com/t/zwift-documentation-for-interfacing-with-smart-trainers/20521)). Wenn deine App dieselben Bytes schickt, verhält sich der Trainer wie bei Zwift.

**Die drei FTMS-Regeln, an denen die meisten Eigenbauten scheitern:**

1. Zuerst Indications auf dem Control Point abonnieren, *dann* schreiben. Sonst siehst du keine Antworten und manche Trainer lehnen Befehle ab.
2. Vor jedem Steuerbefehl einmal „Request Control“ (0x00) senden und auf die Antwort 0x80 mit Ergebnis 0x01 (Erfolg) warten.
3. Nur eine App darf steuern. Zwift, die Hersteller-App am Handy und deine App können sich gegenseitig die Kontrolle wegnehmen.

**Ein echter Stolperstein im Datenformat:** Das Feld „Resistance Level“ in Indoor Bike Data ist laut FTMS 1.0 zwei Bytes lang, und so liest es auch das große Open-Source-Projekt qdomyos-zwift ([Doku](https://deepwiki.com/cagnulein/qdomyos-zwift/3.2-data-flow)). Die neuere Bluetooth-Feldtabelle führt es dagegen als ein Byte ([Feldtabelle](https://docs.embassy.dev/trouble-host/0.2.3/default/prelude/characteristic/constant.INDOOR_BIKE_DATA.html)). Liest du die falsche Länge, sind alle folgenden Felder verschoben, inklusive Watt. Der Parser prüft deshalb die Paketlänge gegen beide Varianten (siehe FTMS-Referenz).

**ANT+ als Plan B:** Für .NET gibt es mit SmallEarthTech.AntPlus eine gepflegte Bibliothek, die Fitness Equipment (FE-C) unterstützt ([NuGet](https://www.nuget.org/packages/smallearthtech.antplus)). Lohnt sich erst, wenn Bluetooth bei dir trotz der Maßnahmen im Stabilitäts-Review abbricht.

**Vorab prüfen:** Mit der kostenlosen Handy-App nRF Connect deinen Trainer scannen. Taucht „Fitness Machine (0x1826)“ auf, ist alles gut. Falls nicht: Trainer-Firmware über die Hersteller-App aktualisieren und erneut prüfen.

## Technologievergleich

C# auf .NET 10 mit WPF ist die stabilste Wahl. .NET 10 ist die aktuelle LTS-Version mit Support bis November 2028, während .NET 8 schon am 10. November 2026 ausläuft ([Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)). Die Bluetooth-Schnittstelle von Windows ist direkt aus .NET nutzbar, ohne Zusatzpakete.

| Option | Bluetooth | Overlay | Urteil |
| --- | --- | --- | --- |
| **C# .NET 10 + WPF** | Native Windows-API, mit Verbindungshaltung und Indications | Randlos, halbtransparent, immer oben, klick-durchlässig möglich | **Empfehlung** |
| C# .NET 10 + WinForms | Gleich gut | Transparenz nur fürs ganze Fenster oder eine Farbe, wirkt grob | Leichter, aber hässlicheres Overlay |
| C# + WinUI 3 | Gleich gut | Transparenz und „immer oben“ umständlicher, zusätzliche Laufzeit | Mehr Aufwand ohne Gewinn |
| Python + Bleak + Qt | Bleak setzt unter Windows auf dieselbe API auf | Möglich | Gut zum Ausprobieren, für ein dauerhaft laufendes Overlay schwerer und fragiler |
| Browser (Web Bluetooth + Picture-in-Picture-Fenster) | Über Chrome | Picture-in-Picture bleibt oben | Kreativ, aber an Chrome gebunden und schwer zu debuggen |

**Projekt-Setup, das sicher funktioniert:**

| IDE | Kosten | WPF-Unterstützung | Urteil |
| --- | --- | --- | --- |
| **JetBrains Rider** (IntelliJ-Plattform) | Kostenlos für private Projekte, alternativ Studentenlizenz | XAML-Vorschau, Vervollständigung, Debugger | **Empfehlung**, bedient sich wie IntelliJ |
| VS Code + C# Dev Kit | Kostenlos für Einzelpersonen | Keine XAML-Vorschau, XAML nur als Text | Geht, beim Overlay-Layout aber mühsamer |

- Rider-Lizenz: beim ersten Start „Non-commercial use“ wählen und mit JetBrains-Konto aktivieren. Sie läuft ein Jahr und verlängert sich automatisch, wenn du Rider genutzt hast. Anonyme Nutzungsstatistik lässt sich dabei nicht abschalten, mit einer der kostenlosen Lizenzen wie der Studentenlizenz schon ([JetBrains](https://www.jetbrains.com/help/rider/2026.2/Register.html)). Willst du die App später verkaufen, brauchst du eine kommerzielle Lizenz.
- VS Code: nur das Original von Microsoft verwenden. Abwandlungen wie Cursor bringen eine eigene C#-Erweiterung mit, die das neue Solution-Format von .NET 10 (.slnx) laut [Cursor-Forum](https://forum.cursor.com/t/support-for-slnx-and-slnf-files/161831) noch nicht lädt.
- Das Projekt wird in beiden Fällen per Kommandozeile angelegt (Phase 0). Damit ist die Struktur IDE-unabhängig und du kannst jederzeit wechseln.
- In den Projektdateien von Ble und App das Ziel `net10.0-windows10.0.19041.0` setzen. Erst dieses Windows-spezifische Ziel schaltet die Windows-Runtime-APIs frei, also auch `Windows.Devices.Bluetooth` ([Microsoft](https://learn.microsoft.com/en-nz/windows/apps/desktop/modernize/winrt-apis-desktop-apps)). Die Version 19041 deckt Windows 10 ab 2004 und Windows 11 ab.
- Keine Fremdbibliothek für Bluetooth. Das spart Abhängigkeiten, die veralten können.

**Warum nicht Python, obwohl du es kannst:** Der Prototyp wäre schneller, aber du würdest das Overlay später ohnehin neu bauen. Die C#-Lernkurve kostet dich vielleicht ein bis zwei Abende, die Bluetooth-Logik ist in beiden Sprachen gleich.

## Architektur

Die App besteht aus drei Schichten: Bluetooth (Windows-spezifisch), Core (reine Logik) und App (WPF-Oberfläche).

&#91;embedded content: Architektur · Hardware und drei Schichten\]

Nur der Modus-Wächter darf Befehle an den Trainer geben, Overlay und Recorder lesen ausschließlich den Session-Zustand. Weil Core keine Windows-Abhängigkeit hat, lässt sich die ganze Logik per Unit-Test und mit einem FakeTrainer prüfen.

## Schritt-für-Schritt-Plan

Zehn Phasen, jede endet mit einem testbaren Ergebnis. Erst wenn das „Fertig, wenn“ erfüllt ist, geht es weiter. Die Stundenangaben sind grobe Schätzungen für jemanden mit Python-Erfahrung.

**So arbeitest du mit den Phasen:**

- Pfade wie `Core/…` meinen `src/Domestique.Core/…`, `Ble/…` und `App/…` entsprechend, `Tests/…` meint `tests/Domestique.Tests/…`. Fehlende Ordner legst du einfach mit an.
- Code-Blöcke, deren erste Zeile mit `// Datei:` beginnt, übernimmst du komplett. Steht dort „ergänzen“, fügst du den Code an der genannten Markierung ein. Markierungen im Code beginnen mit `// ▸`.
- Nach jedem Schritt `dotnet build`, nach jeder Phase `dotnet test` und ein Commit: `git add .; git commit -m "Phase N"; git push`.

### Phase 0 – Vorbereitung (2–3 h)

**Ziel:** Das leere Projekt baut, startet und liegt auf GitHub.

- [x] **0.1** Trainer-Firmware über die Hersteller-App aktualisieren. Danach die Hersteller-App am Handy komplett schließen, sonst belegt sie die Bluetooth-Verbindung.
- [x] **0.2** nRF Connect (kostenlos) aufs Handy laden, „Scan“ tippen, deinen Trainer wählen und „Connect“. Unter „Fitness Machine“ bei „Indoor Bike Data“ das Abo-Symbol (mehrere Pfeile nach unten) antippen, ein paar Sekunden treten und fünf der angezeigten Hex-Werte notieren, z. B. `44-00-A4-0B-B4-00-C8-00`. Das sind deine Testdaten für Phase 1. Danach in nRF Connect trennen.
- [x] **0.3** Werkzeuge in PowerShell installieren, danach ein neues Terminal öffnen.

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
winget install JetBrains.Toolbox     # danach Rider in der Toolbox installieren
# statt Rider: winget install Microsoft.VisualStudioCode, dann Erweiterung "C# Dev Kit"
dotnet --version                     # muss mit 10 beginnen
git config --global user.name "Dein Name"
git config --global user.email "deine@mail.de"
```

- [x] **0.4** Projekte anlegen. In einen Ordner deiner Wahl wechseln, z. B. `cd C:\dev`, dann:

```powershell
mkdir domestique; cd domestique
git init
dotnet new gitignore
dotnet new sln -n Domestique
dotnet new classlib -n Domestique.Core -o src/Domestique.Core
dotnet new classlib -n Domestique.Ble -o src/Domestique.Ble
dotnet new wpf -n Domestique.App -o src/Domestique.App
dotnet new xunit -n Domestique.Tests -o tests/Domestique.Tests
Remove-Item src/Domestique.Core/Class1.cs, src/Domestique.Ble/Class1.cs
```

- [x] **0.5** Windows-Ziel setzen: `src/Domestique.Ble/Domestique.Ble.csproj` und `src/Domestique.App/Domestique.App.csproj` im Editor öffnen und jeweils die Zeile mit `TargetFramework` durch diese ersetzen. Das muss **vor** 0.6 passieren, sonst lehnt .NET den Verweis von App auf Ble ab.

```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
```

- [x] **0.6** Projekte verknüpfen, bauen und testen. Erwartet: „Build succeeded“ und ein bestandener Beispieltest.

```powershell
dotnet sln add src/Domestique.Core/Domestique.Core.csproj src/Domestique.Ble/Domestique.Ble.csproj src/Domestique.App/Domestique.App.csproj tests/Domestique.Tests/Domestique.Tests.csproj
dotnet add src/Domestique.Ble/Domestique.Ble.csproj reference src/Domestique.Core/Domestique.Core.csproj
dotnet add src/Domestique.App/Domestique.App.csproj reference src/Domestique.Core/Domestique.Core.csproj src/Domestique.Ble/Domestique.Ble.csproj
dotnet add tests/Domestique.Tests/Domestique.Tests.csproj reference src/Domestique.Core/Domestique.Core.csproj
dotnet build
dotnet test
```

- [x] **0.7** In der IDE öffnen und einmal starten. Rider: „Open“, `Domestique.slnx` wählen, oben rechts „Domestique.App“ starten. VS Code: Ordner öffnen, F5, dann „C#“ und Domestique.App wählen. Es erscheint ein leeres Fenster.
- [x] **0.8** Auf github.com ein neues Repository `domestique` anlegen und dabei **keine** README, .gitignore oder Lizenz ankreuzen. Dann hochladen (Benutzernamen einsetzen):

```powershell
git add .
git commit -m "Projektgeruest"
git branch -M main
git remote add origin https://github.com/DEIN-NAME/domestique.git
git push -u origin main
```

- [x] **0.9** Lizenz ergänzen: auf GitHub „Add file“, „Create new file“, Dateiname `LICENSE`, „Choose a license template“, MIT wählen und speichern. Lokal danach `git pull`.

**Fertig, wenn:** das leere Fenster aus der IDE startet, `dotnet test` grün ist, der Code auf GitHub liegt und nRF Connect den Service 0x1826 gezeigt hat.

### Phase 1 – Verbinden und Lesen (8–15 h)

**Ziel:** Ein Knopf „Verbinden“, darunter live Watt, Trittfrequenz und Speed.

**Neue Dateien:** `Core/Protocol/BleUuids.cs`, `Core/Protocol/IndoorBikeData.cs`, `Core/SessionState.cs`, `Core/PowerAverager.cs`, `Core/RawLog.cs`, `Ble/BufferExtensions.cs`, `Ble/BleScan.cs`, `Ble/BleTrainer.cs`, `Tests/IndoorBikeDataTests.cs`. Geändert: `App/MainWindow.xaml` und `App/MainWindow.xaml.cs`.

- [x] **1.1** UUIDs als Konstanten. Windows erwartet 128-Bit-UUIDs, die Methode baut sie aus den kurzen 16-Bit-Nummern der Spezifikation.

```csharp
// Datei: src/Domestique.Core/Protocol/BleUuids.cs
namespace Domestique.Core.Protocol;

public static class BleUuids
{
    public static Guid FromShort(ushort id) => Guid.Parse($"0000{id:X4}-0000-1000-8000-00805F9B34FB");

    public static readonly Guid FitnessMachineService = FromShort(0x1826);
    public static readonly Guid IndoorBikeData        = FromShort(0x2AD2);
    public static readonly Guid SupportedPowerRange   = FromShort(0x2AD8);
    public static readonly Guid ControlPoint          = FromShort(0x2AD9);
    public static readonly Guid MachineStatus         = FromShort(0x2ADA);
    public static readonly Guid HeartRateService      = FromShort(0x180D);
    public static readonly Guid HeartRateMeasurement  = FromShort(0x2A37);
}
```

- [x] **1.2** Parser für Indoor Bike Data. Er liest nur die Felder, deren Bit gesetzt ist, und erkennt die Länge des Resistance-Felds an der Paketlänge (siehe FTMS-Referenz).

```csharp
// Datei: src/Domestique.Core/Protocol/IndoorBikeData.cs
using System.Buffers.Binary;

namespace Domestique.Core.Protocol;

public sealed record IndoorBikeData(double? SpeedKmh, double? CadenceRpm, int? PowerW, int? HeartRateBpm);

public static class IndoorBikeDataParser
{
    public static IndoorBikeData? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        ushort f = BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (Length(f, resistanceBytes: 2) == d.Length) return Read(d, f, 2);
        if ((f & 0x0020) != 0 && Length(f, resistanceBytes: 1) == d.Length) return Read(d, f, 1);
        if (d.Length > Length(f, resistanceBytes: 2)) return Read(d, f, 2);   // angehängte Herstellerbytes ignorieren (Van Rysel D100)
        return null;                                   // zu kurz: nicht raten
    }

    private static int Length(ushort f, int resistanceBytes)
    {
        int n = 2;                                     // Flags
        if ((f & 0x0001) == 0) n += 2;                 // Bit 0 = 0: Speed vorhanden
        if ((f & 0x0002) != 0) n += 2;                 // Average Speed
        if ((f & 0x0004) != 0) n += 2;                 // Cadence
        if ((f & 0x0008) != 0) n += 2;                 // Average Cadence
        if ((f & 0x0010) != 0) n += 3;                 // Total Distance
        if ((f & 0x0020) != 0) n += resistanceBytes;   // Resistance Level
        if ((f & 0x0040) != 0) n += 2;                 // Power
        if ((f & 0x0080) != 0) n += 2;                 // Average Power
        if ((f & 0x0100) != 0) n += 5;                 // Energie
        if ((f & 0x0200) != 0) n += 1;                 // Heart Rate
        if ((f & 0x0400) != 0) n += 1;                 // MET
        if ((f & 0x0800) != 0) n += 2;                 // Elapsed Time
        if ((f & 0x1000) != 0) n += 2;                 // Remaining Time
        return n;
    }

    private static IndoorBikeData Read(ReadOnlySpan<byte> d, ushort f, int resistanceBytes)
    {
        int i = 2;
        double? speed = null, cadence = null;
        int? power = null, heartRate = null;
        if ((f & 0x0001) == 0) { speed = BinaryPrimitives.ReadUInt16LittleEndian(d[i..]) / 100.0; i += 2; }
        if ((f & 0x0002) != 0) i += 2;
        if ((f & 0x0004) != 0) { cadence = BinaryPrimitives.ReadUInt16LittleEndian(d[i..]) / 2.0; i += 2; }
        if ((f & 0x0008) != 0) i += 2;
        if ((f & 0x0010) != 0) i += 3;
        if ((f & 0x0020) != 0) i += resistanceBytes;
        if ((f & 0x0040) != 0) { power = BinaryPrimitives.ReadInt16LittleEndian(d[i..]); i += 2; }
        if ((f & 0x0080) != 0) i += 2;
        if ((f & 0x0100) != 0) i += 5;
        if ((f & 0x0200) != 0) heartRate = d[i];
        return new IndoorBikeData(speed, cadence, power, heartRate);
    }
}
```

- [x] **1.3** Test schreiben, `Tests/UnitTest1.cs` löschen, dann `dotnet test`. Für jedes deiner fünf Pakete aus 0.2 einen weiteren `[Fact]` nach demselben Muster ergänzen und prüfen, ob die Werte plausibel sind.

```csharp
// Datei: tests/Domestique.Tests/IndoorBikeDataTests.cs
using Domestique.Core.Protocol;
using Xunit;

namespace Domestique.Tests;

public class IndoorBikeDataTests
{
    [Fact]
    public void Reads_speed_cadence_power()
    {
        byte[] packet = [0x44, 0x00, 0xA4, 0x0B, 0xB4, 0x00, 0xC8, 0x00];
        var d = IndoorBikeDataParser.Parse(packet);
        Assert.NotNull(d);
        Assert.Equal(29.80, d.SpeedKmh!.Value, 2);
        Assert.Equal(90.0, d.CadenceRpm);
        Assert.Equal(200, d.PowerW);
    }

    [Fact]
    public void Ignores_appended_vendor_bytes()                  // echtes Paket vom Van Rysel D100
    {
        var d = IndoorBikeDataParser.Parse(Convert.FromHexString("40008C020900E101000000001C00E803"));
        Assert.NotNull(d);
        Assert.Equal(6.52, d.SpeedKmh!.Value, 2);
        Assert.Equal(9, d.PowerW);
    }

    [Fact]
    public void Rejects_wrong_length() =>
        Assert.Null(IndoorBikeDataParser.Parse([0x44, 0x00, 0xA4]));
}
```

- [x] **1.4** Session-Zustand: ein unveränderlicher Schnappschuss, der atomar ausgetauscht wird (Review #6). Die Felder für spätere Phasen sind schon angelegt.

```csharp
// Datei: src/Domestique.Core/SessionState.cs
namespace Domestique.Core;

public sealed record SessionSnapshot
{
    public int PowerW { get; init; }
    public int Power3sW { get; init; }
    public double CadenceRpm { get; init; }
    public int? HeartRateBpm { get; init; }
    public double TrainerSpeedKmh { get; init; }
    public long LastPacketTimestamp { get; init; }   // Stopwatch.GetTimestamp()
    public bool Connected { get; init; }
    public bool StrapConnected { get; init; }        // ab Phase 8
    public double VirtualSpeedKmh { get; init; }     // ab Phase 5
    public double DistanceM { get; init; }           // ab Phase 5
    public double GradePercent { get; init; }        // ab Phase 5
}

public sealed class SessionState
{
    private SessionSnapshot _current = new();
    public SessionSnapshot Current => Volatile.Read(ref _current);

    // Ändert den Zustand atomar, auch wenn Bluetooth und Oberfläche gleichzeitig schreiben.
    public void Update(Func<SessionSnapshot, SessionSnapshot> change)
    {
        SessionSnapshot before, after;
        do { before = Current; after = change(before); }
        while (Interlocked.CompareExchange(ref _current, after, before) != before);
    }
}
```

- [x] **1.5** 3-Sekunden-Mittel und Rohdaten-Log. Das Log landet unter `%LOCALAPPDATA%\Domestique\raw.log` und ist dein wichtigstes Werkzeug bei der Fehlersuche.

```csharp
// Datei: src/Domestique.Core/PowerAverager.cs
using System.Diagnostics;

namespace Domestique.Core;

public sealed class PowerAverager(TimeSpan window)
{
    private readonly Queue<(long Ts, int W)> _samples = new();

    public int Add(long timestamp, int watts)
    {
        _samples.Enqueue((timestamp, watts));
        while (Stopwatch.GetElapsedTime(_samples.Peek().Ts, timestamp) > window) _samples.Dequeue();
        return (int)Math.Round(_samples.Average(s => s.W));
    }
}
```

```csharp
// Datei: src/Domestique.Core/RawLog.cs
namespace Domestique.Core;

public static class RawLog
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Domestique", "raw.log");
    private static readonly object Gate = new();

    public static void Write(string channel, byte[] bytes) => Note($"{channel} {Convert.ToHexString(bytes)}");

    public static void Note(string text)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
    }
}
```

- [x] **1.6** Bluetooth-Hilfen: Windows-Puffer in Bytes umwandeln und nach einem Service suchen.

```csharp
// Datei: src/Domestique.Ble/BufferExtensions.cs
using Windows.Storage.Streams;

namespace Domestique.Ble;

internal static class BufferExtensions
{
    public static byte[] ToBytes(this IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }
}
```

```csharp
// Datei: src/Domestique.Ble/BleScan.cs
using Windows.Devices.Bluetooth.Advertisement;

namespace Domestique.Ble;

public static class BleScan
{
    // Liefert die Adresse des ersten Geräts, das den Service anbietet, oder null nach Ablauf der Zeit.
    public static async Task<ulong?> FindFirstAsync(Guid serviceUuid, TimeSpan timeout)
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(serviceUuid);
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Received += (_, e) => found.TrySetResult(e.BluetoothAddress);
        watcher.Start();
        try
        {
            var first = await Task.WhenAny(found.Task, Task.Delay(timeout));
            return first == found.Task ? found.Task.Result : (ulong?)null;
        }
        finally { watcher.Stop(); }
    }
}
```

- [x] **1.7** Die Trainer-Verbindung. Ablauf: Gerät öffnen, Verbindung dauerhaft halten, FTMS-Service suchen, Indoor Bike Data abonnieren. Die beiden Kommentare mit „Phase 3“ und „Phase 8“ sind Markierungen für später.

```csharp
// Datei: src/Domestique.Ble/BleTrainer.cs
using System.Diagnostics;
using Domestique.Core;
using Domestique.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Domestique.Ble;

public sealed class BleTrainer(SessionState state) : IAsyncDisposable
{
    private readonly PowerAverager _avg = new(TimeSpan.FromSeconds(3));
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _ftms;
    private GattCharacteristic? _bikeData;
    // ▸ Phase 3 und 8: weitere Felder hier

    public async Task ConnectAsync(ulong address)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException("Trainer nicht erreichbar.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;                         // Windows hält die Verbindung

        var services = await _device.GetGattServicesForUuidAsync(BleUuids.FitnessMachineService, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"FTMS-Service nicht gefunden ({services.Status}).");
        _ftms = services.Services[0];

        _bikeData = await GetCharacteristicAsync(BleUuids.IndoorBikeData)
            ?? throw new InvalidOperationException("Indoor Bike Data fehlt.");
        _bikeData.ValueChanged += OnBikeData;
        var result = await _bikeData.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (result != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Abo fehlgeschlagen ({result}).");

        state.Update(s => s with { Connected = true });
        // ▸ Phase 3: Steuerung hier
    }

    private async Task<GattCharacteristic?> GetCharacteristicAsync(Guid uuid)
    {
        var r = await _ftms!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0 ? r.Characteristics[0] : null;
    }

    private void OnBikeData(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToBytes();
        RawLog.Write("2AD2", bytes);
        var d = IndoorBikeDataParser.Parse(bytes);
        if (d is null) { RawLog.Note("2AD2 nicht lesbar"); return; }

        long now = Stopwatch.GetTimestamp();
        int? avg = d.PowerW is int w ? _avg.Add(now, w) : null;
        state.Update(s => s with
        {
            PowerW = d.PowerW ?? s.PowerW,
            Power3sW = avg ?? s.Power3sW,
            CadenceRpm = d.CadenceRpm ?? s.CadenceRpm,
            HeartRateBpm = s.StrapConnected ? s.HeartRateBpm : d.HeartRateBpm ?? s.HeartRateBpm,
            TrainerSpeedKmh = d.SpeedKmh ?? s.TrainerSpeedKmh,
            LastPacketTimestamp = now,
        });
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        state.Update(s => s with { Connected = connected });
        // ▸ Phase 8: Wiederverbinden hier
    }

    // ▸ Phase 3 und 8: weitere Methoden hier

    public ValueTask DisposeAsync()
    {
        if (_bikeData is not null) _bikeData.ValueChanged -= OnBikeData;
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        // ▸ Phase 3: Status-Abo lösen
        _ftms?.Dispose();
        _session?.Dispose();
        _device?.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [x] **1.8** Testoberfläche: In `App/MainWindow.xaml` das `<Grid>` samt Inhalt durch den ersten Block ersetzen, `App/MainWindow.xaml.cs` komplett durch den zweiten.

```xml
<StackPanel Margin="16">
    <Button Content="Verbinden" Click="Connect_Click" Width="120" HorizontalAlignment="Left"/>
    <TextBlock x:Name="StatusText" Margin="0,12,0,0"/>
    <TextBlock x:Name="ValuesText" Margin="0,8,0,0" FontSize="20"/>
</StackPanel>
```

```csharp
// Datei: src/Domestique.App/MainWindow.xaml.cs (Testversion, wird in Phase 2 ersetzt)
using System.Windows;
using System.Windows.Threading;
using Domestique.Ble;
using Domestique.Core;
using Domestique.Core.Protocol;

namespace Domestique.App;

public partial class MainWindow : Window
{
    private readonly SessionState _state = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private BleTrainer? _trainer;

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer.Tick += (_, _) => Render(_state.Current);
        _uiTimer.Start();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Suche Trainer …";
            ulong? address = await BleScan.FindFirstAsync(BleUuids.FitnessMachineService, TimeSpan.FromSeconds(15));
            if (address is null) { StatusText.Text = "Kein FTMS-Trainer gefunden."; return; }
            StatusText.Text = "Verbinde …";
            _trainer = new BleTrainer(_state);
            await _trainer.ConnectAsync(address.Value);
            StatusText.Text = "Verbunden.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + ex.Message;
        }
    }

    private void Render(SessionSnapshot s) =>
        ValuesText.Text = $"{s.Power3sW} W   {s.CadenceRpm:0} U/min   {s.TrainerSpeedKmh:0.0} km/h   "
                        + (s.Connected ? "verbunden" : "getrennt");

    protected override async void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        if (_trainer is not null) await _trainer.DisposeAsync();
        base.OnClosed(e);
    }
}
```

- [x] **1.9** Testfahrt: App starten, „Verbinden“ klicken, 30 Minuten treten. Die Watt sollten bei lockerem Treten grob zwischen 80 und 200 liegen und sofort auf härteres Treten reagieren. Im Log steht jede Sekunde eine Zeile mit `2AD2`.
- [x] **1.10** Falls „Kein FTMS-Trainer gefunden“: Hersteller-App am Handy und Zwift schließen, Trainer kurz vom Strom nehmen, Bluetooth in den Windows-Einstellungen prüfen. Hilft das nicht, nennt dein Trainer den Service nicht in seinem Funksignal. Dann in `BleScan` die Zeile mit `ServiceUuids.Add` entfernen und im `Received`-Handler nur Geräte annehmen, deren `e.Advertisement.LocalName` den Namen deines Trainers enthält (so, wie nRF Connect ihn anzeigt). Steht im Log „2AD2 nicht lesbar“, die Hex-Zeile mit der FTMS-Referenz abgleichen.

**Fertig, wenn:** 30 Minuten ohne „getrennt“ laufen und die Werte plausibel auf dein Treten reagieren.

### Phase 2 – Overlay (6–10 h)

**Ziel:** Das Testfenster wird zum kleinen, halbtransparenten Overlay unten rechts, das sich beim Start selbst mit dem Trainer verbindet.

**Neue Dateien:** `App/Native.cs`, `App/AppSettings.cs`. Komplett ersetzt: `App/MainWindow.xaml` und `App/MainWindow.xaml.cs`.

- [x] **2.1** Windows-Funktionen, die WPF nicht direkt anbietet: Klicks durchlassen, „immer oben“ erzwingen, globale Tastenkürzel und Standby verhindern.

```csharp
// Datei: src/Domestique.App/Native.cs
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Domestique.App;

internal static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1;
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    // An = Mausklicks gehen durch das Overlay hindurch an YouTube.
    public static void SetClickThrough(Window w, bool on)
    {
        IntPtr h = Handle(w);
        int style = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
    }

    // Holt das Fenster wieder nach ganz oben, falls ein Vollbild-Browser sich davorgeschoben hat.
    public static void ReassertTopmost(Window w) =>
        SetWindowPos(Handle(w), HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
```

- [x] **2.2** Einstellungen als JSON unter `%APPDATA%\Domestique\settings.json`. Die Werte für spätere Phasen sind schon enthalten. FTP und Gewicht trägst du in dieser Datei von Hand ein, sobald sie nach dem ersten Beenden existiert.

```csharp
// Datei: src/Domestique.App/AppSettings.cs
using System.IO;
using System.Text.Json;

namespace Domestique.App;

public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public ulong? TrainerAddress { get; set; }
    public ulong? StrapAddress { get; set; }       // Phase 8
    public int FtpW { get; set; } = 250;            // Phase 4
    public double RiderKg { get; set; } = 75;       // Phase 5
    public double BikeKg { get; set; } = 8;         // Phase 5
    public double Difficulty { get; set; } = 0.5;   // Phase 5: 0.5 = halbe Steigung am Trainer

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Domestique", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }                     // erster Start oder kaputte Datei
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
```

- [x] **2.3** Das Overlay-Fenster: `App/MainWindow.xaml` komplett ersetzen.

```xml
<!-- Datei: src/Domestique.App/MainWindow.xaml -->
<Window x:Class="Domestique.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Domestique" Width="260" SizeToContent="Height"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        Loaded="Window_Loaded" MouseLeftButtonDown="Window_MouseLeftButtonDown">
    <Border Background="#99000000" CornerRadius="10" Padding="12">
        <Border.ContextMenu>
            <ContextMenu>
                <!-- ▸ Phase 4, 5, 7: weitere Menüpunkte hier -->
                <MenuItem Header="Beenden" Click="Exit_Click"/>
            </ContextMenu>
        </Border.ContextMenu>
        <StackPanel>
            <TextBlock x:Name="PowerText" Text="– W" Foreground="White" FontSize="36" FontWeight="Bold"/>
            <TextBlock x:Name="DetailText" Foreground="#DDFFFFFF" FontSize="14"/>
            <!-- ▸ Phase 4–6: weitere Zeilen hier -->
            <TextBlock x:Name="StatusText" Foreground="#AAFFFFFF" FontSize="11" Margin="0,4,0,0"/>
        </StackPanel>
    </Border>
</Window>
```

- [x] **2.4** Die Logik des Fensters: `App/MainWindow.xaml.cs` komplett ersetzen. Diese Datei ist das Herzstück der App, die Phasen 3 bis 8 ergänzen sie an den Markierungen.

```csharp
// Datei: src/Domestique.App/MainWindow.xaml.cs
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Domestique.Ble;
using Domestique.Core;
using Domestique.Core.Protocol;
// ▸ Phase 3–7: weitere usings hier

namespace Domestique.App;

public partial class MainWindow : Window
{
    private const int HotkeyClickThrough = 1;                 // Strg+Alt+D
    // ▸ Phase 3–4: weitere Hotkey-Nummern hier

    private readonly SessionState _state = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private BleTrainer? _trainer;
    private bool _clickThrough;
    private string _message = "Suche Trainer …";
    // ▸ Phase 3–8: weitere Felder hier

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer.Tick += (_, _) => Render(_state.Current);
        _topmostTimer.Tick += (_, _) => Native.ReassertTopmost(this);      // Review #7
        SizeChanged += (_, e) =>                                            // unterer Rand bleibt stehen
        {
            if (e.HeightChanged && e.PreviousSize.Height > 0) Top -= e.NewSize.Height - e.PreviousSize.Height;
        };
        // ▸ Phase 3–7: weitere Initialisierung hier
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = Native.Handle(this);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        Native.RegisterHotKey(hwnd, HotkeyClickThrough, Native.MOD_CONTROL | Native.MOD_ALT, 0x44);
        // ▸ Phase 3–4: weitere Hotkeys registrieren
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Left is double left && _settings.Top is double top) { Left = left; Top = top; }
        else PlaceBottomRight();
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);   // Review #9
        _uiTimer.Start();
        _topmostTimer.Start();
        await ConnectTrainerAsync();
        // ▸ Phase 5, 7, 8: hier starten
    }

    private async Task ConnectTrainerAsync()
    {
        try
        {
            ulong? address = _settings.TrainerAddress
                ?? await BleScan.FindFirstAsync(BleUuids.FitnessMachineService, TimeSpan.FromSeconds(15));
            if (address is null) { _message = "Kein Trainer gefunden"; return; }
            _message = "Verbinde …";
            _trainer = new BleTrainer(_state);
            await _trainer.ConnectAsync(address.Value);
            _settings.TrainerAddress = address;
            _settings.Save();
            // ▸ Phase 3: nach dem Verbinden
        }
        catch (Exception ex)
        {
            _message = "Fehler: " + ex.Message;
            _settings.TrainerAddress = null;                  // beim nächsten Start neu suchen
            _settings.Save();
        }
    }

    private void PlaceBottomRight()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private void Render(SessionSnapshot s)
    {
        bool stale = Stopwatch.GetElapsedTime(s.LastPacketTimestamp) > TimeSpan.FromSeconds(3);
        PowerText.Text = stale ? "– W" : $"{s.Power3sW} W";
        DetailText.Text = $"{s.CadenceRpm:0} U/min" + (s.HeartRateBpm is int hr ? $"   {hr} bpm" : "");
        StatusText.Text = !s.Connected ? _message : stale ? "keine Daten" : "";
        // ▸ Phase 3–5: weitere Anzeigen hier
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY) { OnHotkey(wParam.ToInt32()); handled = true; }
        return IntPtr.Zero;
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyClickThrough:
                _clickThrough = !_clickThrough;
                Native.SetClickThrough(this, _clickThrough);
                break;
            // ▸ Phase 3–4: weitere Hotkeys hier
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ▸ Phase 4–8: weitere Methoden hier

    protected override void OnClosing(CancelEventArgs e)
    {
        var hwnd = Native.Handle(this);
        for (int id = 1; id <= 10; id++) Native.UnregisterHotKey(hwnd, id);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        // ▸ Phase 7: Fahrt exportieren
        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        _topmostTimer.Stop();
        if (_trainer is not null) await _trainer.DisposeAsync();
        // ▸ Phase 8: Pulsgurt freigeben
        base.OnClosed(e);
    }
}
```

- [x] **2.5** Starten: Das Overlay erscheint unten rechts und verbindet sich selbst. Mit gedrückter linker Maustaste verschieben, per Rechtsklick beenden.
- [x] **2.6** Vollbild-Test: YouTube in Chrome im Vollbild abspielen, das Overlay muss sichtbar bleiben. Strg+Alt+D schaltet Klick-durch um. An heißt, Klicks gehen an YouTube. Aus heißt, du kannst das Overlay wieder verschieben und beenden. Danach dasselbe in Edge und Firefox.
- [x] **2.7** Last prüfen: Task-Manager, Reiter „Details“, `Domestique.App.exe` suchen. Die CPU-Spalte muss unter 1 liegen.

**Fertig, wenn:** YouTube im Vollbild läuft, das Overlay sichtbar bleibt, Strg+Alt+D funktioniert und die CPU-Last unter 1 % liegt.

### Phase 3 – Steuern: ERG von Hand (4–8 h)

**Ziel:** Mit Strg+Alt+Bild↑ und Strg+Alt+Bild↓ stellst du die Zielwatt in 10-W-Schritten, und der Trainer hält sie.

**Neue Dateien:** `Core/Protocol/FtmsCommands.cs`, `Core/Control/ModeGuard.cs`, `Ble/ControlPointClient.cs`, `Tests/ControlTests.cs`. Ergänzt: `Ble/BleTrainer.cs`, `App/MainWindow.xaml.cs`.

- [x] **3.1** Befehle als Bytes bauen und Antworten lesen. Die Einheiten stehen in der FTMS-Referenz.

```
// Datei: src/Domestique.Core/Protocol/FtmsCommands.cs
namespace Domestique.Core.Protocol;

public static class FtmsCommands
{
    public static byte[] RequestControl() => [0x00];
    public static byte[] Start() => [0x07];
    public static byte[] Stop() => [0x08, 0x01]; //first - opcode ,second - parameter
    public static byte[] Pause() => [0x08, 0x02];

    public static byte[] SetTargetPower(int watts)
    {
        short w = (short)Math.Clamp(watts, 0, short.MaxValue);
        return [0x05, (byte)w, (byte)(w >> 8)];
    }

    public static byte[] SetSimulation(double gradePercent, double crr = 0.004, double cwKgPerM = 0.51)
    {
        short grade = (short)Math.Round(gradePercent * 100);     // Einheit 0,01 %
        return [0x11, 0x00, 0x00,                                 // Wind 0
                (byte)grade, (byte)(grade >> 8),
                (byte)Math.Round(crr * 10000),                    // Einheit 0,0001
                (byte)Math.Round(cwKgPerM * 100)];                // Einheit 0,01 kg/m
    }
}

public readonly record struct ControlResponse(byte RequestOpCode, byte Result)
{
    public bool Success => Result == 0x01;

    public static ControlResponse? TryParse(byte[] d) =>
        d.Length >= 3 && d[0] == 0x80 ? new ControlResponse(d[1], d[2]) : (ControlResponse?)null;
}
```

- [x] **3.2** Der Modus-Wächter (Review #1). Nur hier entstehen Zielbefehle, und immer nur für einen Modus.

```csharp
// Datei: src/Domestique.Core/Control/ModeGuard.cs
using Domestique.Core.Protocol;

namespace Domestique.Core.Control;

public enum ControlMode { Free, Erg, Sim }

public sealed class ModeGuard(Action<byte[]> sendTarget)
{
    private double? _lastGrade;
    public ControlMode Mode { get; private set; } = ControlMode.Free;
    public int? TargetPowerW { get; private set; }
    public (int Min, int Max) PowerLimits { get; set; } = (0, 2000);

    public void SetErg(int watts)
    {
        watts = Math.Clamp(watts, PowerLimits.Min, PowerLimits.Max);
        Mode = ControlMode.Erg;
        _lastGrade = null;
        if (TargetPowerW == watts) return;               // nichts Neues: nichts senden
        TargetPowerW = watts;
        sendTarget(FtmsCommands.SetTargetPower(watts));
    }

    public void EndErg()
    {
        if (Mode != ControlMode.Erg) return;
        Mode = ControlMode.Free;
        TargetPowerW = null;
        SetGrade(0);                                      // zurück auf flache Straße
    }

    public void SetGrade(double gradePercent)
    {
        if (Mode == ControlMode.Erg) return;              // im ERG nie eine Steigung senden
        Mode = ControlMode.Sim;
        if (_lastGrade is double last && Math.Abs(last - gradePercent) < 0.1) return;
        _lastGrade = gradePercent;
        sendTarget(FtmsCommands.SetSimulation(gradePercent));
    }
}
```

- [x] **3.3** Tests: die Bytes gegen den echten Zwift-Mitschnitt und der Modus-Wächter gegen Review #1. Dann `dotnet test`.

```csharp
// Datei: tests/Domestique.Tests/ControlTests.cs
using Domestique.Core.Control;
using Domestique.Core.Protocol;
using Xunit;

namespace Domestique.Tests;

public class ControlTests
{
    [Fact]
    public void Simulation_matches_zwift_capture() =>
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00, 0x60, 0x01, 0x28, 0x33 }, FtmsCommands.SetSimulation(3.52));

    [Fact]
    public void Negative_grade() =>
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00, 0x06, 0xFF, 0x28, 0x33 }, FtmsCommands.SetSimulation(-2.5));

    [Fact]
    public void Target_power() =>
        Assert.Equal(new byte[] { 0x05, 0xC8, 0x00 }, FtmsCommands.SetTargetPower(200));

    [Fact]
    public void Erg_blocks_grade()
    {
        var sent = new List<byte[]>();
        var guard = new ModeGuard(sent.Add);
        guard.SetErg(200);
        guard.SetGrade(5);                                // muss ignoriert werden
        Assert.Single(sent);
        Assert.Equal(0x05, sent[0][0]);
    }
}
```

- [ ] **3.4** Die Befehlsschlange (Review #2 und #3). Einmalige Befehle warten auf ihre Antwort. Bei Zielwerten ersetzt der neueste alle wartenden, damit sich nichts staut.

```csharp
// Datei: src/Domestique.Ble/ControlPointClient.cs
using Domestique.Core;
using Domestique.Core.Protocol;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Domestique.Ble;

public sealed class ControlPointClient
{
    private readonly GattCharacteristic _cp;
    private readonly SemaphoreSlim _gate = new(1, 1);            // immer nur ein Befehl unterwegs
    private TaskCompletionSource<ControlResponse>? _pending;
    private byte[]? _queuedTarget;
    private byte[]? _lastTarget;
    private int _pumpRunning;

    public ControlPointClient(GattCharacteristic cp)
    {
        _cp = cp;
        _cp.ValueChanged += (_, a) =>
        {
            var bytes = a.CharacteristicValue.ToBytes();
            RawLog.Write("2AD9", bytes);
            if (ControlResponse.TryParse(bytes) is { } r) _pending?.TrySetResult(r);
        };
    }

    public async Task SubscribeAsync()
    {
        var s = await _cp.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Indicate);
        if (s != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Control Point nicht abonnierbar ({s}).");
    }

    // Ein Befehl, dann bis zu 2 s auf die Antwort warten. null = keine Antwort.
    public async Task<ControlResponse?> SendAsync(byte[] command)
    {
        await _gate.WaitAsync();
        try
        {
            var pending = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = pending;
            var writer = new DataWriter();
            writer.WriteBytes(command);
            var result = await _cp.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
            if (result.Status != GattCommunicationStatus.Success) return null;
            var done = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            return done == pending.Task ? pending.Task.Result : (ControlResponse?)null;
        }
        finally { _gate.Release(); }
    }

    // Bei „Kontrolle nicht erlaubt“ (0x05) einmal neu anfordern und wiederholen.
    public async Task<bool> SendWithControlAsync(byte[] command)
    {
        var r = await SendAsync(command);
        if (r?.Result == 0x05)
        {
            await SendAsync(FtmsCommands.RequestControl());
            r = await SendAsync(command);
        }
        return r?.Success == true;
    }

    // Für Zielwerte (Watt, Steigung): der neueste gewinnt.
    public void SetTarget(byte[] command)
    {
        Volatile.Write(ref _lastTarget, command);
        Volatile.Write(ref _queuedTarget, command);
        if (Interlocked.Exchange(ref _pumpRunning, 1) == 0) _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            while (Interlocked.Exchange(ref _queuedTarget, null) is { } next)
            {
                try { await SendWithControlAsync(next); }
                catch (Exception ex) { RawLog.Note("Control Point: " + ex.Message); }
            }
            Volatile.Write(ref _pumpRunning, 0);
            if (Volatile.Read(ref _queuedTarget) is null || Interlocked.Exchange(ref _pumpRunning, 1) != 0) return;
        }
    }

    // Nach Kontrollverlust oder Reconnect: Kontrolle holen, starten, letzten Zielwert erneut senden.
    public async Task RegainControlAsync()
    {
        await SendAsync(FtmsCommands.RequestControl());
        await SendAsync(FtmsCommands.Start());
        if (Volatile.Read(ref _lastTarget) is { } t) SetTarget(t);
    }
}
```

- [ ] **3.5** `Ble/BleTrainer.cs` ergänzen, jeweils an der genannten Markierung.

```csharp
// Ergänzen in Ble/BleTrainer.cs

// an „▸ Phase 3 und 8: weitere Felder hier“
public ControlPointClient? Control { get; private set; }
public (int Min, int Max)? PowerRange { get; private set; }
private GattCharacteristic? _status;

// an „▸ Phase 3: Steuerung hier“ (in ConnectAsync)
if (await GetCharacteristicAsync(BleUuids.ControlPoint) is { } cp)
{
    Control = new ControlPointClient(cp);
    await Control.SubscribeAsync();                               // 1. erst abonnieren
    await Control.SendAsync(FtmsCommands.RequestControl());       // 2. dann Kontrolle holen
    await Control.SendAsync(FtmsCommands.Start());
}
if (await GetCharacteristicAsync(BleUuids.MachineStatus) is { } st)
{
    _status = st;
    _status.ValueChanged += OnStatus;
    await _status.WriteClientCharacteristicConfigurationDescriptorAsync(
        GattClientCharacteristicConfigurationDescriptorValue.Notify);
}
if (await GetCharacteristicAsync(BleUuids.SupportedPowerRange) is { } pr)
{
    var read = await pr.ReadValueAsync(BluetoothCacheMode.Uncached);
    if (read.Status == GattCommunicationStatus.Success)
    {
        var b = read.Value.ToBytes();
        if (b.Length >= 4) PowerRange = (BitConverter.ToInt16(b, 0), BitConverter.ToInt16(b, 2));
    }
}

// an „▸ Phase 3 und 8: weitere Methoden hier“
private void OnStatus(GattCharacteristic sender, GattValueChangedEventArgs args)
{
    var b = args.CharacteristicValue.ToBytes();
    RawLog.Write("2ADA", b);
    if (b.Length > 0 && b[0] == 0xFF && Control is not null)      // 0xFF = Kontrolle verloren
        _ = Control.RegainControlAsync();
}

// an „▸ Phase 3: Status-Abo lösen“ (in DisposeAsync)
if (_status is not null) _status.ValueChanged -= OnStatus;
```

- [ ] **3.6** `App/MainWindow.xaml.cs` ergänzen. Bild↑ und Bild↓ statt Pfeiltasten, weil Strg+Alt+Pfeil bei manchen Grafiktreibern den Bildschirm dreht.

```csharp
// Ergänzen in App/MainWindow.xaml.cs

// an „weitere usings hier“
using Domestique.Core.Control;

// an „weitere Hotkey-Nummern hier“
private const int HotkeyPowerUp = 2, HotkeyPowerDown = 3;     // Strg+Alt+Bild↑ / Bild↓

// an „weitere Felder hier“
private readonly ModeGuard _guard;

// an „weitere Initialisierung hier“ (im Konstruktor)
_guard = new ModeGuard(command => _trainer?.Control?.SetTarget(command));

// an „weitere Hotkeys registrieren“
Native.RegisterHotKey(hwnd, HotkeyPowerUp, Native.MOD_CONTROL | Native.MOD_ALT, 0x21);
Native.RegisterHotKey(hwnd, HotkeyPowerDown, Native.MOD_CONTROL | Native.MOD_ALT, 0x22);

// an „nach dem Verbinden“
if (_trainer.PowerRange is { } range) _guard.PowerLimits = range;
_guard.SetGrade(0);                                            // Startzustand: flache Straße

// an „weitere Anzeigen hier“ (in Render)
if (_guard.TargetPowerW is int target) DetailText.Text += $"   Ziel {target} W";

// an „weitere Hotkeys hier“ (im switch)
case HotkeyPowerUp:   _guard.SetErg((_guard.TargetPowerW ?? 150) + 10); break;
case HotkeyPowerDown: _guard.SetErg((_guard.TargetPowerW ?? 150) - 10); break;
```

- [ ] **3.7** Test: aufs kleine Kettenblatt schalten, Strg+Alt+Bild↑ dreimal drücken. Im Overlay steht „Ziel 180 W“, im Log stehen Zeilen mit `2AD9 800501` (Erfolg). Dann einmal mit 80 und einmal mit 95 Umdrehungen treten: Die Watt bleiben in beiden Fällen nahe am Ziel. Reagiert ein Tastenkürzel nicht, ist es von einem anderen Programm belegt. Dann einen anderen Tastencode wählen.

**Fertig, wenn:** der Trainer die Zielwatt hält, egal ob du mit 80 oder 95 Umdrehungen trittst.

### Phase 4 – Trainingspläne (8–12 h)

**Ziel:** Rechtsklick, „Workout laden …“, eine .zwo-Datei wählen, und die App fährt sie im ERG-Modus ab.

**Neue Dateien:** `Core/Workouts/Workout.cs`, `Core/Workouts/ZwoParser.cs`, `Core/Workouts/WorkoutPlayer.cs`, `Tests/ZwoParserTests.cs`. Ergänzt: `App/MainWindow.xaml`, `App/MainWindow.xaml.cs`.

- [ ] **4.1** Datenmodell: Ein Workout ist eine Liste von Abschnitten. Leistungen sind Anteile der FTP, 0.88 heißt 88 %.

```csharp
// Datei: src/Domestique.Core/Workouts/Workout.cs
namespace Domestique.Core.Workouts;

public sealed record Segment(TimeSpan Duration, double StartFtp, double EndFtp, bool FreeRide = false);

public sealed record Workout(string Name, IReadOnlyList<Segment> Segments)
{
    public TimeSpan TotalDuration => TimeSpan.FromSeconds(Segments.Sum(s => s.Duration.TotalSeconds));
}
```

- [ ] **4.2** ZWO-Dateien lesen. Unbekannte Elemente werden übersprungen und im Log vermerkt.

```csharp
// Datei: src/Domestique.Core/Workouts/ZwoParser.cs
using System.Globalization;
using System.Xml.Linq;

namespace Domestique.Core.Workouts;

public static class ZwoParser
{
    public static Workout Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("Leere Datei.");
        string name = root.Element("name")?.Value ?? "Workout";
        var segments = new List<Segment>();

        foreach (var e in root.Element("workout")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            switch (e.Name.LocalName)
            {
                case "Warmup": case "Cooldown": case "Ramp":
                    segments.Add(new(Sec(e, "Duration"), Num(e, "PowerLow"), Num(e, "PowerHigh")));
                    break;
                case "SteadyState":
                    segments.Add(new(Sec(e, "Duration"), Num(e, "Power"), Num(e, "Power")));
                    break;
                case "IntervalsT":
                    for (int i = 0; i < (int)Num(e, "Repeat"); i++)
                    {
                        segments.Add(new(Sec(e, "OnDuration"), Num(e, "OnPower"), Num(e, "OnPower")));
                        segments.Add(new(Sec(e, "OffDuration"), Num(e, "OffPower"), Num(e, "OffPower")));
                    }
                    break;
                case "FreeRide":
                    segments.Add(new(Sec(e, "Duration"), 0, 0, FreeRide: true));
                    break;
                default:
                    RawLog.Note($"ZWO: <{e.Name.LocalName}> übersprungen");
                    break;
            }
        }
        return new Workout(name, segments);
    }

    private static double Num(XElement e, string attribute) => double.Parse(
        e.Attribute(attribute)?.Value ?? throw new FormatException($"{e.Name.LocalName}: {attribute} fehlt."),
        CultureInfo.InvariantCulture);

    private static TimeSpan Sec(XElement e, string attribute) => TimeSpan.FromSeconds(Num(e, attribute));
}
```

- [ ] **4.3** Test mit einem kleinen Beispiel-Workout, dann `dotnet test`.

```csharp
// Datei: tests/Domestique.Tests/ZwoParserTests.cs
using Domestique.Core.Workouts;
using Xunit;

namespace Domestique.Tests;

public class ZwoParserTests
{
    [Fact]
    public void Expands_intervals()
    {
        const string xml = """
            <workout_file><name>Test</name><workout>
              <Warmup Duration="600" PowerLow="0.5" PowerHigh="0.75"/>
              <IntervalsT Repeat="3" OnDuration="60" OffDuration="60" OnPower="1.2" OffPower="0.5"/>
              <SteadyState Duration="300" Power="0.88"/>
            </workout></workout_file>
            """;
        var workout = ZwoParser.Parse(xml);
        Assert.Equal(8, workout.Segments.Count);                     // 1 + 3 × 2 + 1
        Assert.Equal(TimeSpan.FromMinutes(21), workout.TotalDuration);
    }
}
```

- [ ] **4.4** Der Player rechnet jede Sekunde die Zielwatt aus. Bei Rampen steigt der Wert gleichmäßig, bei FreeRide liefert er null.

```csharp
// Datei: src/Domestique.Core/Workouts/WorkoutPlayer.cs
namespace Domestique.Core.Workouts;

public sealed class WorkoutPlayer(Workout workout, int ftpW)
{
    public TimeSpan Elapsed { get; private set; }
    public bool Paused { get; set; }
    public bool Finished => Elapsed >= workout.TotalDuration;
    public int SegmentCount => workout.Segments.Count;

    public (int? TargetW, int SegmentIndex, TimeSpan SegmentLeft) Tick(TimeSpan dt)
    {
        if (!Paused && !Finished) Elapsed += dt;
        var t = Elapsed;
        for (int i = 0; i < workout.Segments.Count; i++)
        {
            var s = workout.Segments[i];
            if (t < s.Duration)
            {
                double share = s.StartFtp + (s.EndFtp - s.StartFtp) * (t / s.Duration);
                int? target = s.FreeRide ? null : (int)Math.Round(share * ftpW);
                return (target, i, s.Duration - t);
            }
            t -= s.Duration;
        }
        return (null, workout.Segments.Count, TimeSpan.Zero);
    }

    public void SkipSegment()
    {
        var end = TimeSpan.Zero;
        foreach (var s in workout.Segments)
        {
            end += s.Duration;
            if (end > Elapsed) { Elapsed = end; return; }
        }
    }
}
```

- [ ] **4.5** `App/MainWindow.xaml` ergänzen.

```xml
<!-- an „▸ Phase 4, 5, 7: weitere Menüpunkte hier“ -->
<MenuItem Header="Workout laden …" Click="LoadWorkout_Click"/>

<!-- an „▸ Phase 4–6: weitere Zeilen hier“ -->
<TextBlock x:Name="WorkoutText" Foreground="White" FontSize="13" Margin="0,4,0,0"/>
```

- [ ] **4.6** `App/MainWindow.xaml.cs` ergänzen. Pause schickt dem Trainer „Pause“, damit der Widerstand nicht hochgeht, wenn du aufhörst zu treten (Review #11).

```csharp
// Ergänzen in App/MainWindow.xaml.cs

// an „weitere usings hier“
using Domestique.Core.Workouts;

// an „weitere Hotkey-Nummern hier“
private const int HotkeyPause = 4, HotkeySkip = 5;            // Strg+Alt+P / Strg+Alt+N

// an „weitere Felder hier“
private WorkoutPlayer? _player;
private readonly DispatcherTimer _workoutTimer = new() { Interval = TimeSpan.FromSeconds(1) };
private long _lastWorkoutTick;

// an „weitere Initialisierung hier“ (im Konstruktor)
_workoutTimer.Tick += (_, _) => WorkoutTick();

// an „weitere Hotkeys registrieren“
Native.RegisterHotKey(hwnd, HotkeyPause, Native.MOD_CONTROL | Native.MOD_ALT, 0x50);
Native.RegisterHotKey(hwnd, HotkeySkip, Native.MOD_CONTROL | Native.MOD_ALT, 0x4E);

// an „weitere Hotkeys hier“ (im switch)
case HotkeyPause when _player is not null:
    _player.Paused = !_player.Paused;
    if (_trainer?.Control is { } c)
        _ = _player.Paused ? c.SendWithControlAsync(FtmsCommands.Pause()) : c.RegainControlAsync();
    break;
case HotkeySkip:
    _player?.SkipSegment();
    break;

// an „weitere Methoden hier“
private void LoadWorkout_Click(object sender, RoutedEventArgs e)
{
    var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Zwift-Workout (*.zwo)|*.zwo" };
    if (dialog.ShowDialog() != true) return;
    try
    {
        _player = new WorkoutPlayer(ZwoParser.Parse(File.ReadAllText(dialog.FileName)), _settings.FtpW);
        _lastWorkoutTick = Stopwatch.GetTimestamp();
        _workoutTimer.Start();
    }
    catch (Exception ex) { MessageBox.Show(ex.Message, "Workout nicht lesbar"); }
}

private void WorkoutTick()
{
    if (_player is null) return;
    long now = Stopwatch.GetTimestamp();
    var dt = Stopwatch.GetElapsedTime(_lastWorkoutTick, now);      // echte Zeit, nicht der Timer-Takt
    _lastWorkoutTick = now;

    var (target, index, left) = _player.Tick(dt);
    if (_player.Finished)
    {
        _guard.EndErg();
        _workoutTimer.Stop();
        WorkoutText.Text = "Workout fertig";
        return;
    }
    if (target is int w) _guard.SetErg(w); else _guard.EndErg();
    WorkoutText.Text = $"Intervall {index + 1}/{_player.SegmentCount} · noch {(int)left.TotalMinutes}:{left.Seconds:00}"
                     + (target is int t ? $" · {t} W" : " · frei") + (_player.Paused ? " · Pause" : "");
}
```

- [ ] **4.7** Deine FTP eintragen: App einmal schließen, dann in `%APPDATA%\Domestique\settings.json` den Wert bei `FtpW` ändern.
- [ ] **4.8** Ein Workout besorgen. Viele Workout-Seiten bieten .zwo-Dateien zum Download an. Zum ersten Test reicht auch das Beispiel aus 4.3, als `test.zwo` gespeichert.
- [ ] **4.9** Test: Workout laden und fahren. Strg+Alt+P pausiert und setzt fort, Strg+Alt+N springt zum nächsten Intervall. Bei Rampen steigt die Zielleistung im Overlay sekundenweise.

**Fertig, wenn:** ein heruntergeladenes 45-Minuten-Workout komplett durchläuft, inklusive Pause und Fortsetzen.

### Phase 5 – Strecken und Simulation (10–15 h)

**Ziel:** Rechtsklick, „Strecke laden …“, eine GPX-Datei wählen. Der Trainer simuliert die Steigung, das Overlay zeigt Geschwindigkeit, Kilometer und Prozent. Ohne Strecke rechnet die App mit flacher Straße, damit Speed und Distanz immer stimmen.

**Neue Dateien:** `Core/Physics/RiderPhysics.cs`, `Core/Routes/Route.cs`, `Core/Routes/GpxLoader.cs`, `Core/Routes/RouteSim.cs`, `Tests/PhysicsTests.cs`, `Tests/GpxLoaderTests.cs`. Ergänzt: `App/MainWindow.xaml`, `App/MainWindow.xaml.cs`.

- [ ] **5.1** Die Physik aus „Strecken & Physik“. Die Mindestgeschwindigkeit von 0,5 m/s in der Leistungsbilanz sorgt dafür, dass man aus dem Stand losrollt, auch bergab ohne Treten.

```csharp
// Datei: src/Domestique.Core/Physics/RiderPhysics.cs
namespace Domestique.Core.Physics;

public sealed class RiderPhysics
{
    private const double G = 9.81;
    public double MassKg { get; set; } = 83;       // Fahrer + Rad
    public double Crr { get; init; } = 0.004;
    public double CdA { get; set; } = 0.32;
    public double Rho { get; init; } = 1.225;
    public double Efficiency { get; init; } = 0.97;
    public double SpeedMps { get; private set; }

    public double Force(double v, double gradePercent)
    {
        double theta = Math.Atan(gradePercent / 100);
        return MassKg * G * Math.Sin(theta)
             + Crr * MassKg * G * Math.Cos(theta)
             + 0.5 * Rho * CdA * v * v;
    }

    // Rechnet dt Sekunden weiter und gibt die gefahrene Strecke in Metern zurück.
    public double Step(double powerW, double gradePercent, double dt)
    {
        double v = SpeedMps;
        double vEff = Math.Max(v, 0.5);
        double energy = 0.5 * MassKg * v * v + dt * (powerW * Efficiency - Force(v, gradePercent) * vEff);
        SpeedMps = Math.Sqrt(2 * Math.Max(0, energy) / MassKg);
        return SpeedMps * dt;
    }
}
```

- [ ] **5.2** Physik-Test mit den Kontrollwerten, dann `dotnet test`.

```csharp
// Datei: tests/Domestique.Tests/PhysicsTests.cs
using Domestique.Core.Physics;
using Xunit;

namespace Domestique.Tests;

public class PhysicsTests
{
    [Theory]
    [InlineData(200, 0, 33.9)]
    [InlineData(250, 5, 17.9)]
    public void Reaches_expected_speed(double watts, double grade, double expectedKmh)
    {
        var physics = new RiderPhysics();
        for (int i = 0; i < 2400; i++) physics.Step(watts, grade, 0.25);   // 10 Minuten fahren
        Assert.InRange(physics.SpeedMps * 3.6, expectedKmh - 0.3, expectedKmh + 0.3);
    }
}
```

- [ ] **5.3** Streckenmodell und GPX-Verarbeitung in den fünf Schritten aus „Strecken & Physik“.

```csharp
// Datei: src/Domestique.Core/Routes/Route.cs
namespace Domestique.Core.Routes;

public sealed record RoutePoint(double DistanceM, double Lat, double Lon, double ElevationM, double GradePercent);

public sealed class Route(string name, IReadOnlyList<RoutePoint> points)
{
    public string Name { get; } = name;
    public IReadOnlyList<RoutePoint> Points { get; } = points;
    public double LengthM => Points[^1].DistanceM;

    // Die Punkte liegen im festen 10-m-Raster, daher reicht eine Division.
    public RoutePoint At(double distanceM) =>
        Points[Math.Clamp((int)(distanceM / GpxLoader.StepM), 0, Points.Count - 1)];
}
```

```csharp
// Datei: src/Domestique.Core/Routes/GpxLoader.cs
using System.Globalization;
using System.Xml.Linq;

namespace Domestique.Core.Routes;

public static class GpxLoader
{
    public const double StepM = 10;                 // Rasterabstand
    private const double SmoothM = 100;             // Glättungsfenster
    private const double MinGrade = -10, MaxGrade = 20;

    public static Route Load(string xml, string name)
    {
        // 1. Punkte lesen (Track oder Route, Namespace egal)
        var raw = XDocument.Parse(xml).Descendants()
            .Where(e => e.Name.LocalName is "trkpt" or "rtept")
            .Select(e => (Lat: Num(e.Attribute("lat")?.Value), Lon: Num(e.Attribute("lon")?.Value),
                          Ele: Num(e.Elements().FirstOrDefault(c => c.Name.LocalName == "ele")?.Value)))
            .ToList();
        if (raw.Count < 2) throw new FormatException("GPX enthält keine Strecke.");

        // 2. aufsummierte Distanz
        var dist = new double[raw.Count];
        for (int i = 1; i < raw.Count; i++)
            dist[i] = dist[i - 1] + Haversine(raw[i - 1].Lat, raw[i - 1].Lon, raw[i].Lat, raw[i].Lon);

        // 3. auf 10-m-Raster umrechnen
        var grid = new List<(double D, double Lat, double Lon, double Ele)>();
        int j = 0;
        for (double d = 0; d <= dist[^1]; d += StepM)
        {
            while (j < raw.Count - 2 && dist[j + 1] < d) j++;
            double span = dist[j + 1] - dist[j];
            double f = span > 0 ? (d - dist[j]) / span : 0;
            var (a, b) = (raw[j], raw[j + 1]);
            grid.Add((d, a.Lat + (b.Lat - a.Lat) * f, a.Lon + (b.Lon - a.Lon) * f, a.Ele + (b.Ele - a.Ele) * f));
        }
        if (grid.Count < 2) throw new FormatException("Strecke ist kürzer als 20 m.");

        // 4. Höhe glätten (gleitendes Mittel über ±50 m)
        int half = (int)(SmoothM / StepM / 2);
        var smooth = new double[grid.Count];
        for (int i = 0; i < grid.Count; i++)
        {
            int from = Math.Max(0, i - half), to = Math.Min(grid.Count - 1, i + half);
            double sum = 0;
            for (int k = from; k <= to; k++) sum += grid[k].Ele;
            smooth[i] = sum / (to - from + 1);
        }

        // 5. Steigung pro Abschnitt, begrenzt
        var points = new List<RoutePoint>(grid.Count);
        for (int i = 0; i < grid.Count; i++)
        {
            int next = Math.Min(i + 1, grid.Count - 1), prev = next - 1;
            double grade = (smooth[next] - smooth[prev]) / StepM * 100;
            points.Add(new RoutePoint(grid[i].D, grid[i].Lat, grid[i].Lon, smooth[i], Math.Clamp(grade, MinGrade, MaxGrade)));
        }
        return new Route(name, points);
    }

    private static double Num(string? s) => double.Parse(
        s ?? throw new FormatException("GPX-Punkt ohne lat, lon oder Höhe."), CultureInfo.InvariantCulture);

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000, Rad = Math.PI / 180;
        double dLat = (lat2 - lat1) * Rad, dLon = (lon2 - lon1) * Rad;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(h));
    }
}
```

- [ ] **5.4** GPX-Test mit einer künstlichen Strecke: 1 km nach Norden, 50 m Höhenunterschied, also 5 %.

```csharp
// Datei: tests/Domestique.Tests/GpxLoaderTests.cs
using Domestique.Core.Routes;
using Xunit;

namespace Domestique.Tests;

public class GpxLoaderTests
{
    [Fact]
    public void One_kilometer_at_five_percent()
    {
        const string gpx = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>
              <trkpt lat="51.0" lon="7.0"><ele>100</ele></trkpt>
              <trkpt lat="51.0089932" lon="7.0"><ele>150</ele></trkpt>
            </trkseg></trk></gpx>
            """;
        var route = GpxLoader.Load(gpx, "Test");
        Assert.InRange(route.LengthM, 990, 1000);
        Assert.InRange(route.At(500).GradePercent, 4.9, 5.1);
    }
}
```

- [ ] **5.5** Die Simulation auf der Strecke und der Begrenzer für Steigungswechsel am Trainer (höchstens 1 Prozentpunkt pro Sekunde).

```csharp
// Datei: src/Domestique.Core/Routes/RouteSim.cs
using Domestique.Core.Physics;

namespace Domestique.Core.Routes;

public sealed class RouteSim(RiderPhysics physics)
{
    public Route? Route { get; private set; }
    public double DistanceM { get; private set; }
    public RiderPhysics Physics => physics;                               // für Phase 9
    public double SpeedMps => physics.SpeedMps;
    public double GradePercent => Route?.At(DistanceM).GradePercent ?? 0;   // ohne Strecke: flach
    public bool Finished => Route is not null && DistanceM >= Route.LengthM;

    public void Load(Route route) { Route = route; DistanceM = 0; }

    public void Step(double powerW, double dt)
    {
        if (Finished) return;
        DistanceM += physics.Step(powerW, GradePercent, dt);
    }
}

public sealed class GradeLimiter
{
    private double _current;

    public double Next(double wanted)
    {
        _current += Math.Clamp(wanted - _current, -1.0, 1.0);
        return _current;
    }
}
```

- [ ] **5.6** `App/MainWindow.xaml` ergänzen.

```xml
<!-- an „▸ Phase 4, 5, 7: weitere Menüpunkte hier“ -->
<MenuItem Header="Strecke laden …" Click="LoadRoute_Click"/>

<!-- an „▸ Phase 4–6: weitere Zeilen hier“, unter WorkoutText -->
<TextBlock x:Name="RouteText" Foreground="#DDFFFFFF" FontSize="13" Margin="0,4,0,0"/>
```

- [ ] **5.7** `App/MainWindow.xaml.cs` ergänzen. Die Physik läuft 4-mal pro Sekunde, die Steigung geht einmal pro Sekunde an den Trainer.

```csharp
// Ergänzen in App/MainWindow.xaml.cs

// an „weitere usings hier“
using Domestique.Core.Physics;
using Domestique.Core.Routes;

// an „weitere Felder hier“
private readonly RouteSim _sim;
private readonly GradeLimiter _limiter = new();
private readonly DispatcherTimer _physicsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
private long _lastPhysicsTick;
private int _physicsTicks;

// an „weitere Initialisierung hier“ (im Konstruktor)
_sim = new RouteSim(new RiderPhysics { MassKg = _settings.RiderKg + _settings.BikeKg });
_physicsTimer.Tick += (_, _) => PhysicsTick();

// an „▸ Phase 5, 7, 8: hier starten“ (in Window_Loaded)
_lastPhysicsTick = Stopwatch.GetTimestamp();
_physicsTimer.Start();

// an „weitere Anzeigen hier“ (in Render)
RouteText.Text = $"{s.VirtualSpeedKmh:0.0} km/h   {s.DistanceM / 1000:0.0} km"
    + (_sim.Route is { } r ? $" / {r.LengthM / 1000:0.0} km   {s.GradePercent:+0.0;-0.0;0.0} %" : "");

// an „weitere Methoden hier“
private void LoadRoute_Click(object sender, RoutedEventArgs e)
{
    var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GPX-Strecke (*.gpx)|*.gpx" };
    if (dialog.ShowDialog() != true) return;
    try
    {
        var route = GpxLoader.Load(File.ReadAllText(dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName));
        _sim.Load(route);
        // ▸ Phase 6: Karte anzeigen
    }
    catch (Exception ex) { MessageBox.Show(ex.Message, "GPX nicht lesbar"); }
}

private void PhysicsTick()
{
    long now = Stopwatch.GetTimestamp();
    double dt = Stopwatch.GetElapsedTime(_lastPhysicsTick, now).TotalSeconds;
    _lastPhysicsTick = now;

    var s = _state.Current;
    bool stale = Stopwatch.GetElapsedTime(s.LastPacketTimestamp) > TimeSpan.FromSeconds(3);
    _sim.Step(stale ? 0 : s.PowerW, dt);                               // Review #5
    _state.Update(x => x with
    {
        VirtualSpeedKmh = _sim.SpeedMps * 3.6,
        DistanceM = _sim.DistanceM,
        GradePercent = _sim.GradePercent,
    });

    if (++_physicsTicks % 4 != 0) return;                              // ab hier einmal pro Sekunde
    if (_sim.Route is not null)
        _guard.SetGrade(_limiter.Next(_sim.GradePercent * _settings.Difficulty));   // im ERG ignoriert
    // ▸ Phase 6: Punkte bewegen
}
```

- [ ] **5.8** Gewicht und Schwierigkeit in `settings.json` eintragen: `RiderKg`, `BikeKg` und `Difficulty` (0.5 heißt halbe Steigung am Trainer, 1 die volle).
- [ ] **5.9** GPX besorgen: Strecke bei komoot oder Strava als Route öffnen und als GPX exportieren. Für Ironman-Kurse verlinkt die Rennseite oft eine fertige Route.
- [ ] **5.10** Test: Strecke laden. Die Gesamtlänge im Overlay mit der Quelle vergleichen. Bergauf muss das Treten schwerer werden. Danach ein ERG-Workout und eine Strecke gleichzeitig laden: Der Trainer bleibt im ERG, nur Speed und Kilometer kommen von der Strecke (Review #1).

**Fertig, wenn:** eine Ironman-Strecke lädt, die Gesamtdistanz auf 1 % zur Quelle passt und bergauf spürbar schwerer wird.

### Phase 6 – Mini-Karte und Höhenprofil (4–6 h)

**Ziel:** Unter den Zahlen erscheinen eine 90 Pixel große Streckenkarte und ein Höhenprofil, jeweils mit einem Punkt für deine Position.

**Neue Datei:** `App/RouteGeometry.cs`. Ergänzt: `App/MainWindow.xaml`, `App/MainWindow.xaml.cs`.

- [ ] **6.1** Die Linien werden einmal beim Laden berechnet und eingefroren. Danach muss WPF sie nie wieder neu zeichnen, pro Sekunde wandert nur der Punkt. Jeder zehnte Rasterpunkt reicht, also alle 100 m.

```csharp
// Datei: src/Domestique.App/RouteGeometry.cs
using System.Windows;
using System.Windows.Media;
using Domestique.Core.Routes;

namespace Domestique.App;

internal static class RouteGeometry
{
    // Draufsicht: Längengrade werden mit cos(Breite) gestaucht, damit die Form stimmt.
    public static PointCollection Map(Route r, double w, double h, out Func<RoutePoint, Point> project)
    {
        double k = Math.Cos(r.Points.Average(p => p.Lat) * Math.PI / 180);
        double minX = r.Points.Min(p => p.Lon * k), maxX = r.Points.Max(p => p.Lon * k);
        double minY = r.Points.Min(p => p.Lat), maxY = r.Points.Max(p => p.Lat);
        double scale = Math.Min(w / Math.Max(maxX - minX, 1e-9), h / Math.Max(maxY - minY, 1e-9));
        project = p => new Point((p.Lon * k - minX) * scale, h - (p.Lat - minY) * scale);
        return Frozen(r, project);
    }

    // Höhenprofil: Distanz nach rechts, Höhe nach oben.
    public static PointCollection Profile(Route r, double w, double h, out Func<RoutePoint, Point> project)
    {
        double minE = r.Points.Min(p => p.ElevationM), maxE = r.Points.Max(p => p.ElevationM);
        double range = Math.Max(maxE - minE, 1), length = r.LengthM;
        project = p => new Point(p.DistanceM / length * w, h - (p.ElevationM - minE) / range * h);
        return Frozen(r, project);
    }

    private static PointCollection Frozen(Route r, Func<RoutePoint, Point> project)
    {
        var points = new PointCollection(r.Points.Where((_, i) => i % 10 == 0).Select(project));
        points.Freeze();
        return points;
    }
}
```

- [ ] **6.2** `App/MainWindow.xaml` ergänzen. Das Panel bleibt unsichtbar, bis eine Strecke geladen ist.

```xml
<!-- an „▸ Phase 4–6: weitere Zeilen hier“, unter RouteText -->
<Grid x:Name="RoutePanel" Visibility="Collapsed" Margin="0,8,0,0">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <Canvas Width="90" Height="90">
        <Polyline x:Name="MapLine" Stroke="#CCFFFFFF" StrokeThickness="1.5"/>
        <Ellipse x:Name="MapDot" Width="8" Height="8" Fill="#FFFF5A36"/>
    </Canvas>
    <Canvas Grid.Column="1" Width="130" Height="90" Margin="8,0,0,0">
        <Polyline x:Name="ProfileLine" Stroke="#CCFFFFFF" StrokeThickness="1.5"/>
        <Ellipse x:Name="ProfileDot" Width="8" Height="8" Fill="#FFFF5A36"/>
    </Canvas>
</Grid>
```

- [ ] **6.3** `App/MainWindow.xaml.cs` ergänzen und die beiden Markierungen aus Phase 5 ersetzen.

```csharp
// Ergänzen in App/MainWindow.xaml.cs

// an „weitere Felder hier“
private Func<RoutePoint, Point>? _mapProject, _profileProject;

// statt „▸ Phase 6: Karte anzeigen“ (in LoadRoute_Click)
ShowRoute(route);

// statt „▸ Phase 6: Punkte bewegen“ (in PhysicsTick)
MoveDots(_sim.DistanceM);

// an „weitere Methoden hier“
private void ShowRoute(Route route)
{
    MapLine.Points = RouteGeometry.Map(route, 90, 90, out _mapProject);
    ProfileLine.Points = RouteGeometry.Profile(route, 130, 90, out _profileProject);
    RoutePanel.Visibility = Visibility.Visible;
    MoveDots(0);
}

private void MoveDots(double distanceM)
{
    if (_sim.Route is not { } route || _mapProject is null || _profileProject is null) return;
    var p = route.At(distanceM);
    var m = _mapProject(p);
    Canvas.SetLeft(MapDot, m.X - 4);
    Canvas.SetTop(MapDot, m.Y - 4);
    var q = _profileProject(p);
    Canvas.SetLeft(ProfileDot, q.X - 4);
    Canvas.SetTop(ProfileDot, q.Y - 4);
}
```

- [ ] **6.4** Test: Strecke laden. Das Overlay wächst nach oben, die Karte zeigt die Form der Strecke, beide Punkte starten links bzw. am Startpunkt und wandern beim Fahren mit.
- [ ] **6.5** Last prüfen wie in 2.7, mit geladener Ironman-Strecke und laufendem YouTube-Video.

**Fertig, wenn:** Karte und Profil laufen und die CPU-Last weiter unter 1 % bleibt.

### Phase 7 – Aufzeichnung und Export (4–8 h)

**Ziel:** Jede Fahrt wird sekündlich als CSV gesichert, auch wenn die App abstürzt. Beim Beenden entsteht automatisch eine TCX-Datei für Strava.

**Neue Dateien:** `Core/Recording/Recorder.cs`, `Core/Recording/TcxWriter.cs`. Ergänzt: `App/MainWindow.xaml`, `App/MainWindow.xaml.cs`.

- [ ] **7.1** Der Recorder schreibt jede Zeile sofort auf die Platte. Zahlen immer mit Punkt als Dezimaltrenner, damit andere Programme die Datei lesen können.

```csharp
// Datei: src/Domestique.Core/Recording/Recorder.cs
namespace Domestique.Core.Recording;

public sealed record Sample(DateTime TimeUtc, int PowerW, double CadenceRpm, int? HeartRateBpm, double SpeedMps, double DistanceM);

public sealed class Recorder : IDisposable
{
    private readonly List<Sample> _samples = [];
    private readonly StreamWriter _csv;
    public string CsvPath { get; }
    public IReadOnlyList<Sample> Samples => _samples;

    public Recorder(string csvPath)
    {
        CsvPath = csvPath;
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        _csv = new StreamWriter(csvPath) { AutoFlush = true };
        _csv.WriteLine("time_utc;power_w;cadence_rpm;hr_bpm;speed_mps;distance_m");
    }

    public void Add(Sample s)
    {
        _samples.Add(s);
        _csv.WriteLine(FormattableString.Invariant(
            $"{s.TimeUtc:O};{s.PowerW};{s.CadenceRpm:0};{s.HeartRateBpm};{s.SpeedMps:0.00};{s.DistanceM:0.0}"));
    }

    public void Dispose() => _csv.Dispose();
}
```

- [ ] **7.2** TCX-Export. Ohne GPS-Position erkennt Strava die Fahrt als Indoor-Aktivität.

```csharp
// Datei: src/Domestique.Core/Recording/TcxWriter.cs
using System.Globalization;
using System.Xml.Linq;

namespace Domestique.Core.Recording;

public static class TcxWriter
{
    private static readonly XNamespace Ns = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Ext = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static void Write(string path, IReadOnlyList<Sample> samples)
    {
        Sample first = samples[0], last = samples[^1];
        string start = first.TimeUtc.ToString(TimeFormat, CultureInfo.InvariantCulture);
        new XDocument(
            new XElement(Ns + "TrainingCenterDatabase",
                new XAttribute(XNamespace.Xmlns + "ns3", Ext),
                new XElement(Ns + "Activities",
                    new XElement(Ns + "Activity", new XAttribute("Sport", "Biking"),
                        new XElement(Ns + "Id", start),
                        new XElement(Ns + "Lap", new XAttribute("StartTime", start),
                            new XElement(Ns + "TotalTimeSeconds", Inv((last.TimeUtc - first.TimeUtc).TotalSeconds, "0")),
                            new XElement(Ns + "DistanceMeters", Inv(last.DistanceM, "0.0")),
                            new XElement(Ns + "Calories", 0),
                            new XElement(Ns + "Intensity", "Active"),
                            new XElement(Ns + "TriggerMethod", "Manual"),
                            new XElement(Ns + "Track", samples.Select(Trackpoint)))))))
            .Save(path);
    }

    private static XElement Trackpoint(Sample s) => new(Ns + "Trackpoint",
        new XElement(Ns + "Time", s.TimeUtc.ToString(TimeFormat, CultureInfo.InvariantCulture)),
        new XElement(Ns + "DistanceMeters", Inv(s.DistanceM, "0.0")),
        s.HeartRateBpm is int hr ? new XElement(Ns + "HeartRateBpm", new XElement(Ns + "Value", hr)) : null,
        new XElement(Ns + "Cadence", (int)Math.Round(s.CadenceRpm)),
        new XElement(Ns + "Extensions",
            new XElement(Ext + "TPX",
                new XElement(Ext + "Speed", Inv(s.SpeedMps, "0.00")),
                new XElement(Ext + "Watts", s.PowerW))));

    private static string Inv(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
```

- [ ] **7.3** `App/MainWindow.xaml` ergänzen.

```xml
<!-- an „▸ Phase 4, 5, 7: weitere Menüpunkte hier“ -->
<MenuItem Header="Fahrt exportieren" Click="Export_Click"/>
```

- [ ] **7.4** `App/MainWindow.xaml.cs` ergänzen. Die Aufzeichnung startet mit dem ersten Tritt. Die Zeitstempel kommen aus Startzeit plus Stopwatch, damit sie nicht springen.

```csharp
// Ergänzen in App/MainWindow.xaml.cs

// an „weitere usings hier“
using Domestique.Core.Recording;

// an „weitere Felder hier“
private Recorder? _recorder;
private DateTime _rideStartUtc;
private long _rideStartTimestamp;
private double _rideStartDistanceM;
private readonly DispatcherTimer _recordTimer = new() { Interval = TimeSpan.FromSeconds(1) };

// an „weitere Initialisierung hier“ (im Konstruktor)
_recordTimer.Tick += (_, _) => RecordTick();

// an „▸ Phase 5, 7, 8: hier starten“ (in Window_Loaded)
_recordTimer.Start();

// an „▸ Phase 7: Fahrt exportieren“ (in OnClosing)
ExportRide();

// an „weitere Methoden hier“
private void RecordTick()
{
    var s = _state.Current;
    bool stale = Stopwatch.GetElapsedTime(s.LastPacketTimestamp) > TimeSpan.FromSeconds(3);
    int power = stale ? 0 : s.PowerW;
    if (_recorder is null)
    {
        if (power <= 0) return;                               // startet mit dem ersten Tritt
        _rideStartUtc = DateTime.UtcNow;
        _rideStartTimestamp = Stopwatch.GetTimestamp();
        _rideStartDistanceM = s.DistanceM;
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Domestique");
        _recorder = new Recorder(Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm}.csv"));
    }
    var time = _rideStartUtc + Stopwatch.GetElapsedTime(_rideStartTimestamp);
    _recorder.Add(new Sample(time, power, s.CadenceRpm, s.HeartRateBpm,
                             s.VirtualSpeedKmh / 3.6, s.DistanceM - _rideStartDistanceM));
}

private string? ExportRide()
{
    if (_recorder is null || _recorder.Samples.Count < 2) return null;
    string tcx = Path.ChangeExtension(_recorder.CsvPath, ".tcx");
    TcxWriter.Write(tcx, _recorder.Samples);
    _recorder.Dispose();
    _recorder = null;                                         // nächste Fahrt beginnt neu
    return tcx;
}

private void Export_Click(object sender, RoutedEventArgs e)
{
    if (ExportRide() is { } file) MessageBox.Show("Gespeichert: " + file, "Domestique");
}
```

- [ ] **7.5** Test: 10 Minuten fahren, Rechtsklick, „Fahrt exportieren“. In `Dokumente\Domestique` liegen jetzt eine CSV- und eine TCX-Datei.
- [ ] **7.6** Bei Strava über das Plus-Symbol „Aktivität hochladen“ wählen, „Datei“, die TCX-Datei auswählen. Danach als Typ „Virtuelle Radfahrt“ einstellen.

**Fertig, wenn:** Strava die Datei annimmt und Dauer, Distanz, Watt und Trittfrequenz stimmen.

### Phase 8 – Puls und Härtung (6–10 h)

**Ziel:** Puls vom Brustgurt, automatisches Wiederverbinden nach Funkabbrüchen und ein bestandener Dauertest.

**Neue Dateien:** `Core/Protocol/HeartRateParser.cs`, `Ble/BleHeartRate.cs`, `Tests/HeartRateParserTests.cs`. Ergänzt: `Ble/BleTrainer.cs`, `App/MainWindow.xaml.cs`.

- [ ] **8.1** Pulswerte lesen. Bit 0 im ersten Byte sagt, ob der Puls ein oder zwei Bytes lang ist.

```csharp
// Datei: src/Domestique.Core/Protocol/HeartRateParser.cs
namespace Domestique.Core.Protocol;

public static class HeartRateParser
{
    public static int? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        if ((d[0] & 0x01) == 0) return d[1];          // 1 Byte
        if (d.Length < 3) return null;
        return d[1] | d[2] << 8;                      // 2 Byte
    }
}
```

```csharp
// Datei: tests/Domestique.Tests/HeartRateParserTests.cs
using Domestique.Core.Protocol;
using Xunit;

namespace Domestique.Tests;

public class HeartRateParserTests
{
    [Theory]
    [InlineData(new byte[] { 0x00, 0x48 }, 72)]
    [InlineData(new byte[] { 0x01, 0x2C, 0x01 }, 300)]
    public void Reads_heart_rate(byte[] packet, int expected) =>
        Assert.Equal(expected, HeartRateParser.Parse(packet));
}
```

- [ ] **8.2** Die Verbindung zum Brustgurt nach demselben Muster wie beim Trainer, inklusive Neu-Abo nach einem Abbruch.

```csharp
// Datei: src/Domestique.Ble/BleHeartRate.cs
using Domestique.Core;
using Domestique.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Domestique.Ble;

public sealed class BleHeartRate(SessionState state) : IAsyncDisposable
{
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _measurement;
    private bool _wasDisconnected;

    public async Task ConnectAsync(ulong address)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException("Pulsgurt nicht erreichbar.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        var services = await _device.GetGattServicesForUuidAsync(BleUuids.HeartRateService, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException("Heart-Rate-Service fehlt.");
        _service = services.Services[0];

        var chars = await _service.GetCharacteristicsForUuidAsync(BleUuids.HeartRateMeasurement, BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
            throw new InvalidOperationException("Pulsmessung fehlt.");
        _measurement = chars.Characteristics[0];
        _measurement.ValueChanged += OnValue;
        await SubscribeAsync();
    }

    private async Task SubscribeAsync() =>
        await _measurement!.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);

    private void OnValue(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (HeartRateParser.Parse(args.CharacteristicValue.ToBytes()) is int bpm)
            state.Update(s => s with { HeartRateBpm = bpm, StrapConnected = true });
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _wasDisconnected = true;
            state.Update(s => s with { StrapConnected = false });
        }
        else if (_wasDisconnected)
        {
            _wasDisconnected = false;
            _ = SubscribeAsync();                         // Abo nach dem Reconnect neu schreiben
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_measurement is not null) _measurement.ValueChanged -= OnValue;
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _service?.Dispose();
        _session?.Dispose();
        _device?.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **8.3** Wiederverbinden beim Trainer (Review #4). Nach einem Abbruch hält Windows die Verbindung zwar selbst, aber die Abos und die Kontrolle sind weg und müssen neu gesetzt werden.

```csharp
// Ergänzen in Ble/BleTrainer.cs

// an „▸ Phase 3 und 8: weitere Felder hier“
private bool _wasDisconnected;

// an „▸ Phase 8: Wiederverbinden hier“ (in OnConnectionStatusChanged)
if (!connected) { _wasDisconnected = true; RawLog.Note("Trainer getrennt"); }
else if (_wasDisconnected) { _wasDisconnected = false; _ = ResubscribeAsync(); }

// an „▸ Phase 3 und 8: weitere Methoden hier“
private async Task ResubscribeAsync()
{
    try
    {
        RawLog.Note("Trainer wieder da, abonniere neu");
        await _bikeData!.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (_status is not null)
            await _status.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (Control is not null)
        {
            await Control.SubscribeAsync();
            await Control.RegainControlAsync();          // Kontrolle, Start, letzter Zielwert
        }
    }
    catch (Exception ex) { RawLog.Note("Neu-Abo fehlgeschlagen: " + ex.Message); }
}
```

- [ ] **8.4** `App/MainWindow.xaml.cs` ergänzen. Ohne Gurt läuft alles wie bisher weiter.

```csharp
// Ergänzen in App/MainWindow.xaml.cs

// an „weitere Felder hier“
private BleHeartRate? _strap;

// an „▸ Phase 5, 7, 8: hier starten“ (in Window_Loaded, als letzte Zeile)
await ConnectStrapAsync();

// an „▸ Phase 8: Pulsgurt freigeben“ (in OnClosed)
if (_strap is not null) await _strap.DisposeAsync();

// an „weitere Methoden hier“
private async Task ConnectStrapAsync()
{
    try
    {
        ulong? address = _settings.StrapAddress
            ?? await BleScan.FindFirstAsync(BleUuids.HeartRateService, TimeSpan.FromSeconds(10));
        if (address is null) return;                    // kein Gurt in der Nähe
        _strap = new BleHeartRate(_state);
        await _strap.ConnectAsync(address.Value);
        _settings.StrapAddress = address;
        _settings.Save();
    }
    catch (Exception ex)
    {
        RawLog.Note("Pulsgurt: " + ex.Message);
        _settings.StrapAddress = null;
        _settings.Save();
    }
}
```

- [ ] **8.5** Energiesparen des Bluetooth-Adapters abschalten (Review #9): Geräte-Manager, „Bluetooth“ aufklappen, deinen Adapter doppelklicken, Reiter „Energieverwaltung“, den Haken bei „Computer kann das Gerät ausschalten, um Energie zu sparen“ entfernen.
- [ ] **8.6** Den Abnahmetest aus dem Stabilitäts-Review vollständig durchgehen. Bei jedem Fehler zuerst ins Log schauen.
- [ ] **8.7** Version markieren: `git tag v0.1; git push --tags`.

**Fertig, wenn:** du den Trainer während der Fahrt aus- und wieder einschaltest und die App sich selbst erholt, inklusive Zielwatt.

### Phase 9 – Einstellungen und Bibliothek (6–10 h)

**Ziel:** Ein Zahnrad im Overlay öffnet ein Einstellungsfenster mit vier Reitern. Darstellung: Farben, Transparenz, Größe, Karte an oder aus. Fahrer: FTP, Gewicht, CdA, Steigungsfaktor. Bluetooth: Trainer und Pulsgurt suchen und zuordnen. Bibliothek: Workouts und Strecken importieren, laden, löschen. Dateien lassen sich außerdem direkt aufs Overlay ziehen.

**Neue Dateien:** `Core/Library/LibraryStore.cs`, `App/SettingsWindow.xaml`, `App/SettingsWindow.xaml.cs`, `Tests/LibraryStoreTests.cs`. Ergänzt: `Core/Control/ModeGuard.cs`, `Ble/BleScan.cs`, `App/AppSettings.cs`, `App/MainWindow.xaml`, `App/MainWindow.xaml.cs`.

- [ ] **9.1** Die Bibliothek liegt unter `%APPDATA%\Domestique\Workouts` und `\Routes`. Jede Datei wird vor dem Kopieren geprüft, damit nur lesbare Dateien darin landen.

```csharp
// Datei: src/Domestique.Core/Library/LibraryStore.cs
using Domestique.Core.Routes;
using Domestique.Core.Workouts;

namespace Domestique.Core.Library;

public sealed class LibraryStore
{
    public string WorkoutsDir { get; }
    public string RoutesDir { get; }

    public LibraryStore(string root)
    {
        WorkoutsDir = Path.Combine(root, "Workouts");
        RoutesDir = Path.Combine(root, "Routes");
        Directory.CreateDirectory(WorkoutsDir);
        Directory.CreateDirectory(RoutesDir);
    }

    public IReadOnlyList<string> Workouts => Directory.GetFiles(WorkoutsDir, "*.zwo").Order().ToList();
    public IReadOnlyList<string> Routes => Directory.GetFiles(RoutesDir, "*.gpx").Order().ToList();

    public string Import(string sourcePath)
    {
        string text = File.ReadAllText(sourcePath);
        string folder;
        switch (Path.GetExtension(sourcePath).ToLowerInvariant())
        {
            case ".zwo": ZwoParser.Parse(text); folder = WorkoutsDir; break;      // wirft bei kaputter Datei
            case ".gpx": GpxLoader.Load(text, "Prüfung"); folder = RoutesDir; break;
            default: throw new FormatException("Nur .zwo- und .gpx-Dateien werden unterstützt.");
        }
        string target = Path.Combine(folder, Path.GetFileName(sourcePath));
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, target, overwrite: true);
        return target;
    }
}
```

- [ ] **9.2** Test: Eine gute Datei wird übernommen, eine kaputte abgelehnt.

```csharp
// Datei: tests/Domestique.Tests/LibraryStoreTests.cs
using Domestique.Core.Library;
using Xunit;

namespace Domestique.Tests;

public class LibraryStoreTests
{
    [Fact]
    public void Imports_valid_and_rejects_broken_workouts()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string good = Path.Combine(root, "gut.zwo"), bad = Path.Combine(root, "kaputt.zwo");
        File.WriteAllText(good, "<workout_file><workout><SteadyState Duration=\"60\" Power=\"0.8\"/></workout></workout_file>");
        File.WriteAllText(bad, "<workout_file><workout><SteadyState Power=\"0.8\"/></workout></workout_file>");
        var store = new LibraryStore(Path.Combine(root, "lib"));

        store.Import(good);
        Assert.Throws<FormatException>(() => store.Import(bad));
        Assert.Single(store.Workouts);
    }
}
```

- [ ] **9.3** Zwei kleine Ergänzungen in Core und Ble: Der Modus-Wächter lässt sich zurücksetzen, und der Scan liefert eine Liste statt nur des ersten Geräts. Mit `filter = null` zeigt er alle Bluetooth-Geräte, das hilft bei Trainern, die den Service nicht ankündigen (siehe 1.10).

```csharp
// Ergänzen in Core/Control/ModeGuard.cs (in der Klasse)
public void Reset()
{
    Mode = ControlMode.Free;
    TargetPowerW = null;
    _lastGrade = null;
}

// Ergänzen in Ble/BleScan.cs (in der Klasse)
public static async Task<IReadOnlyList<(ulong Address, string Name)>> ScanAllAsync(Guid? filter, TimeSpan duration)
{
    var found = new Dictionary<ulong, string>();
    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
    if (filter is Guid f) watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(f);
    watcher.Received += (_, e) =>
    {
        lock (found)
        {
            if (!found.TryGetValue(e.BluetoothAddress, out var known) || string.IsNullOrEmpty(known))
                found[e.BluetoothAddress] = e.Advertisement.LocalName;
        }
    };
    watcher.Start();
    await Task.Delay(duration);
    watcher.Stop();
    lock (found)
        return found.Select(kv => (kv.Key, string.IsNullOrEmpty(kv.Value) ? "Unbenannt" : kv.Value)).ToList();
}
```

- [ ] **9.4** Neue Einstellungen in `App/AppSettings.cs`, zu den anderen Eigenschaften. Fehlen sie in einer alten `settings.json`, gelten die Standardwerte.

```csharp
// Ergänzen in App/AppSettings.cs
public string BackgroundColor { get; set; } = "#000000";
public string TextColor { get; set; } = "#FFFFFF";
public string AccentColor { get; set; } = "#FF5A36";
public double BackgroundOpacity { get; set; } = 0.6;
public double Scale { get; set; } = 1.0;
public bool ShowMap { get; set; } = true;
public double CdA { get; set; } = 0.32;
```

- [ ] **9.5** Das Einstellungsfenster anlegen. Rider: Rechtsklick auf Domestique.App, „Add“, „WPF Window“, Name `SettingsWindow`. VS Code: beide Dateien von Hand anlegen. Dann den Inhalt komplett ersetzen.

```xml
<!-- Datei: src/Domestique.App/SettingsWindow.xaml -->
<Window x:Class="Domestique.App.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Domestique – Einstellungen" Width="480" Height="580"
        WindowStartupLocation="CenterScreen" Topmost="True">
    <DockPanel Margin="12">
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Übernehmen" Width="110" Margin="0,0,8,0" Click="Apply_Click"/>
            <Button Content="Schließen" Width="110" Click="Close_Click"/>
        </StackPanel>
        <TabControl>
            <TabItem Header="Darstellung">
                <StackPanel Margin="12">
                    <TextBlock Text="Hintergrundfarbe (#RRGGBB)"/>
                    <TextBox x:Name="BackgroundBox" Margin="0,4,0,10"/>
                    <TextBlock Text="Textfarbe"/>
                    <TextBox x:Name="TextColorBox" Margin="0,4,0,10"/>
                    <TextBlock Text="Akzentfarbe (Positionspunkt)"/>
                    <TextBox x:Name="AccentBox" Margin="0,4,0,10"/>
                    <TextBlock Text="{Binding Value, ElementName=OpacitySlider, StringFormat='Deckkraft des Hintergrunds: {0:0} %'}"/>
                    <Slider x:Name="OpacitySlider" Minimum="0" Maximum="100" Margin="0,4,0,10"/>
                    <TextBlock Text="{Binding Value, ElementName=ScaleSlider, StringFormat='Größe: {0:0.00}-fach'}"/>
                    <Slider x:Name="ScaleSlider" Minimum="0.7" Maximum="1.6" Margin="0,4,0,10"/>
                    <CheckBox x:Name="ShowMapBox" Content="Karte und Höhenprofil anzeigen" Margin="0,0,0,10"/>
                    <Button Content="Position zurücksetzen" Width="160" HorizontalAlignment="Left" Click="ResetPosition_Click"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Fahrer">
                <StackPanel Margin="12">
                    <TextBlock Text="FTP in Watt (gilt ab dem nächsten Workout)"/>
                    <TextBox x:Name="FtpBox" Margin="0,4,0,10"/>
                    <TextBlock Text="Fahrergewicht in kg"/>
                    <TextBox x:Name="RiderBox" Margin="0,4,0,10"/>
                    <TextBlock Text="Radgewicht in kg"/>
                    <TextBox x:Name="BikeBox" Margin="0,4,0,10"/>
                    <TextBlock Text="CdA in m² (etwa 0,32 Oberlenker, 0,25 Zeitfahrhaltung)"/>
                    <TextBox x:Name="CdaBox" Margin="0,4,0,10"/>
                    <TextBlock Text="{Binding Value, ElementName=DifficultySlider, StringFormat='Steigung am Trainer: {0:0} % der echten'}"/>
                    <Slider x:Name="DifficultySlider" Minimum="0" Maximum="100" Margin="0,4,0,10"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Bluetooth">
                <DockPanel Margin="12">
                    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal">
                        <Button Content="Trainer suchen" Width="120" Click="ScanTrainer_Click"/>
                        <Button Content="Pulsgurt suchen" Width="120" Margin="8,0,0,0" Click="ScanStrap_Click"/>
                        <CheckBox x:Name="AllDevicesBox" Content="alle Geräte" VerticalAlignment="Center" Margin="8,0,0,0"/>
                    </StackPanel>
                    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
                        <Button Content="Als Trainer verwenden" Width="160" Click="UseAsTrainer_Click"/>
                        <Button Content="Als Pulsgurt verwenden" Width="160" Margin="8,0,0,0" Click="UseAsStrap_Click"/>
                    </StackPanel>
                    <TextBlock x:Name="BleInfo" DockPanel.Dock="Bottom" TextWrapping="Wrap" Margin="0,8,0,0"/>
                    <ListBox x:Name="DeviceList" Margin="0,8,0,0" DisplayMemberPath="Label"/>
                </DockPanel>
            </TabItem>
            <TabItem Header="Bibliothek">
                <DockPanel Margin="12">
                    <TextBlock DockPanel.Dock="Top" TextWrapping="Wrap" Margin="0,0,0,8"
                               Text="Dateien kannst du auch direkt auf das Overlay ziehen."/>
                    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
                        <Button Content="Importieren …" Width="110" Click="Import_Click"/>
                        <Button Content="Laden" Width="110" Margin="8,0,0,0" Click="LoadSelected_Click"/>
                        <Button Content="Löschen" Width="110" Margin="8,0,0,0" Click="Delete_Click"/>
                    </StackPanel>
                    <ListBox x:Name="LibraryList" DisplayMemberPath="Label"/>
                </DockPanel>
            </TabItem>
        </TabControl>
    </DockPanel>
</Window>
```

- [ ] **9.6** Die Logik des Einstellungsfensters. Nichts wird übernommen, solange ein Feld ungültig ist.

```csharp
// Datei: src/Domestique.App/SettingsWindow.xaml.cs
using System.IO;
using System.Windows;
using System.Windows.Media;
using Domestique.Ble;
using Domestique.Core.Library;
using Domestique.Core.Protocol;

namespace Domestique.App;

public sealed record DeviceItem(ulong Address, string Label);
public sealed record LibraryItem(string FilePath, string Label);

public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private readonly LibraryStore _library;

    public SettingsWindow(MainWindow main, AppSettings settings, LibraryStore library)
    {
        InitializeComponent();
        _main = main;
        _s = settings;
        _library = library;
        Owner = main;

        BackgroundBox.Text = _s.BackgroundColor;
        TextColorBox.Text = _s.TextColor;
        AccentBox.Text = _s.AccentColor;
        OpacitySlider.Value = _s.BackgroundOpacity * 100;
        ScaleSlider.Value = _s.Scale;
        ShowMapBox.IsChecked = _s.ShowMap;
        FtpBox.Text = _s.FtpW.ToString();
        RiderBox.Text = _s.RiderKg.ToString();
        BikeBox.Text = _s.BikeKg.ToString();
        CdaBox.Text = _s.CdA.ToString();
        DifficultySlider.Value = _s.Difficulty * 100;
        RefreshLibrary();
    }

    // ---------- Darstellung und Fahrer ----------

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!IsColor(BackgroundBox.Text) || !IsColor(TextColorBox.Text) || !IsColor(AccentBox.Text))
        {
            MessageBox.Show(this, "Farben bitte als #RRGGBB angeben, z. B. #FF5A36.", "Einstellungen");
            return;
        }
        if (!int.TryParse(FtpBox.Text, out int ftp) || !double.TryParse(RiderBox.Text, out double rider)
            || !double.TryParse(BikeBox.Text, out double bike) || !double.TryParse(CdaBox.Text, out double cda))
        {
            MessageBox.Show(this, "Bei FTP, Gewicht und CdA bitte nur Zahlen eintragen.", "Einstellungen");
            return;
        }
        _s.BackgroundColor = BackgroundBox.Text;
        _s.TextColor = TextColorBox.Text;
        _s.AccentColor = AccentBox.Text;
        _s.BackgroundOpacity = OpacitySlider.Value / 100;
        _s.Scale = ScaleSlider.Value;
        _s.ShowMap = ShowMapBox.IsChecked == true;
        _s.FtpW = ftp;
        _s.RiderKg = rider;
        _s.BikeKg = bike;
        _s.CdA = cda;
        _s.Difficulty = DifficultySlider.Value / 100;
        _s.Save();
        _main.ApplySettings();                                   // wirkt sofort, ohne Neustart
    }

    private static bool IsColor(string text)
    {
        try { return ColorConverter.ConvertFromString(text) is Color; }
        catch { return false; }
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => _main.ResetPosition();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Bluetooth ----------

    private async void ScanTrainer_Click(object sender, RoutedEventArgs e) => await ScanAsync(BleUuids.FitnessMachineService);

    private async void ScanStrap_Click(object sender, RoutedEventArgs e) => await ScanAsync(BleUuids.HeartRateService);

    private async Task ScanAsync(Guid service)
    {
        BleInfo.Text = "Suche 8 Sekunden …";
        DeviceList.ItemsSource = null;
        Guid? filter = AllDevicesBox.IsChecked == true ? null : service;
        var devices = await BleScan.ScanAllAsync(filter, TimeSpan.FromSeconds(8));
        DeviceList.ItemsSource = devices.Select(d => new DeviceItem(d.Address, $"{d.Name}   ({d.Address:X12})")).ToList();
        BleInfo.Text = devices.Count == 0
            ? "Nichts gefunden. Hersteller-App und Zwift geschlossen? Ein gerade verbundenes Gerät funkt meist nicht und taucht deshalb nicht auf."
            : "Gerät auswählen und unten zuordnen.";
    }

    private async void UseAsTrainer_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem device) return;
        _s.TrainerAddress = device.Address;
        _s.Save();
        BleInfo.Text = "Verbinde Trainer …";
        await _main.ReconnectTrainerAsync();
        BleInfo.Text = "Trainer gespeichert.";
    }

    private async void UseAsStrap_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceItem device) return;
        _s.StrapAddress = device.Address;
        _s.Save();
        BleInfo.Text = "Verbinde Pulsgurt …";
        await _main.ReconnectStrapAsync();
        BleInfo.Text = "Pulsgurt gespeichert.";
    }

    // ---------- Bibliothek ----------

    private void RefreshLibrary() =>
        LibraryList.ItemsSource = _library.Workouts
            .Select(f => new LibraryItem(f, "Workout · " + Path.GetFileNameWithoutExtension(f)))
            .Concat(_library.Routes.Select(f => new LibraryItem(f, "Strecke · " + Path.GetFileNameWithoutExtension(f))))
            .ToList();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Workouts und Strecken (*.zwo;*.gpx)|*.zwo;*.gpx",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            try { _library.Import(file); }
            catch (Exception ex) { MessageBox.Show(this, $"{Path.GetFileName(file)}: {ex.Message}", "Import fehlgeschlagen"); }
        }
        RefreshLibrary();
    }

    private void LoadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is LibraryItem item) _main.LoadFile(item.FilePath);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not LibraryItem item) return;
        if (MessageBox.Show(this, item.Label + " löschen?", "Bibliothek", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        File.Delete(item.FilePath);
        RefreshLibrary();
    }
}
```

- [ ] **9.7** `App/MainWindow.xaml` ergänzen: das Zahnrad, Drag & Drop und einen Namen für den Rahmen, damit sich die Farben setzen lassen.

```xml
<!-- im <Window ...>-Tag zusätzlich -->
AllowDrop="True" Drop="Window_Drop"

<!-- im <Border ...>-Tag zusätzlich -->
x:Name="Card"

<!-- an „▸ Phase 4, 5, 7: weitere Menüpunkte hier“ -->
<MenuItem Header="Einstellungen …" Click="SettingsMenu_Click"/>

<!-- direkt unter <StackPanel> als erste Zeile; ZIndex legt es über die Watt-Zeile -->
<TextBlock Text="&#xE713;" FontFamily="Segoe MDL2 Assets" FontSize="14" Foreground="#AAFFFFFF"
           HorizontalAlignment="Right" Margin="0,0,0,-16" Panel.ZIndex="1" Cursor="Hand"
           ToolTip="Einstellungen (Strg+Alt+O)" MouseLeftButtonDown="Gear_Click"/>
```

- [ ] **9.8** `App/MainWindow.xaml.cs` ergänzen. Zwei Stellen aus früheren Phasen werden dabei ersetzt, das steht jeweils dabei.

```csharp
// Ergänzen in App/MainWindow.xaml.cs

// an „weitere usings hier“
using System.Windows.Media;
using Domestique.Core.Library;

// an „weitere Hotkey-Nummern hier“
private const int HotkeySettings = 6;                       // Strg+Alt+O

// an „weitere Felder hier“
private readonly LibraryStore _library = new(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Domestique"));
private SettingsWindow? _settingsWindow;

// an „weitere Hotkeys registrieren“
Native.RegisterHotKey(hwnd, HotkeySettings, Native.MOD_CONTROL | Native.MOD_ALT, 0x4F);

// in Window_Loaded als allererste Zeile (Farben und Größe vor dem Positionieren)
ApplySettings();

// an „weitere Anzeigen hier“ (in Render): Fahrzeit
if (_recorder is not null)
{
    var t = Stopwatch.GetElapsedTime(_rideStartTimestamp);
    DetailText.Text += $"   {(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
}

// an „weitere Hotkeys hier“ (im switch)
case HotkeySettings: OpenSettings(); break;

// in ShowRoute (Phase 6) die Zeile „RoutePanel.Visibility = Visibility.Visible;“ ersetzen durch:
ApplySettings();

// LoadWorkout_Click und LoadRoute_Click (Phase 4 und 5) komplett ersetzen durch:
private void LoadWorkout_Click(object sender, RoutedEventArgs e) => PickAndLoad("Zwift-Workout (*.zwo)|*.zwo");
private void LoadRoute_Click(object sender, RoutedEventArgs e) => PickAndLoad("GPX-Strecke (*.gpx)|*.gpx");

// an „weitere Methoden hier“
private void PickAndLoad(string filter)
{
    var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
    if (dialog.ShowDialog() == true) ImportAndLoad(dialog.FileName);
}

private void ImportAndLoad(string path)
{
    try { LoadFile(_library.Import(path)); }                 // landet automatisch in der Bibliothek
    catch (Exception ex) { MessageBox.Show(ex.Message, "Datei nicht lesbar"); }
}

public void LoadFile(string path)
{
    try
    {
        if (path.EndsWith(".zwo", StringComparison.OrdinalIgnoreCase))
        {
            _player = new WorkoutPlayer(ZwoParser.Parse(File.ReadAllText(path)), _settings.FtpW);
            _lastWorkoutTick = Stopwatch.GetTimestamp();
            _workoutTimer.Start();
        }
        else if (path.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
        {
            var route = GpxLoader.Load(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
            _sim.Load(route);
            ShowRoute(route);
        }
    }
    catch (Exception ex) { MessageBox.Show(ex.Message, "Datei nicht lesbar"); }
}

private void Window_Drop(object sender, DragEventArgs e)
{
    if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        foreach (var file in files) ImportAndLoad(file);
}

private void Gear_Click(object sender, MouseButtonEventArgs e)
{
    e.Handled = true;                                         // sonst startet das Verschieben
    OpenSettings();
}

private void SettingsMenu_Click(object sender, RoutedEventArgs e) => OpenSettings();

private void OpenSettings()
{
    if (_clickThrough) { _clickThrough = false; Native.SetClickThrough(this, false); }
    if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; }
    _settingsWindow = new SettingsWindow(this, _settings, _library);
    _settingsWindow.Show();
}

public void ApplySettings()
{
    var background = ParseColor(_settings.BackgroundColor, Colors.Black);
    background.A = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, 0, 1) * 255);
    Card.Background = new SolidColorBrush(background);

    var text = new SolidColorBrush(ParseColor(_settings.TextColor, Colors.White));
    foreach (var block in new[] { PowerText, DetailText, WorkoutText, RouteText, StatusText }) block.Foreground = text;
    MapLine.Stroke = ProfileLine.Stroke = text;
    MapDot.Fill = ProfileDot.Fill = new SolidColorBrush(ParseColor(_settings.AccentColor, Colors.OrangeRed));

    double scale = Math.Clamp(_settings.Scale, 0.5, 2);
    Card.LayoutTransform = new ScaleTransform(scale, scale);
    Width = 260 * scale;
    RoutePanel.Visibility = _settings.ShowMap && _sim.Route is not null ? Visibility.Visible : Visibility.Collapsed;

    _sim.Physics.MassKg = _settings.RiderKg + _settings.BikeKg;
    _sim.Physics.CdA = _settings.CdA;
}

private static Color ParseColor(string text, Color fallback)
{
    try { return ColorConverter.ConvertFromString(text) is Color c ? c : fallback; }
    catch { return fallback; }                                // Tippfehler in settings.json
}

public void ResetPosition()
{
    _settings.Left = _settings.Top = null;
    PlaceBottomRight();
}

public async Task ReconnectTrainerAsync()
{
    if (_trainer is not null) { await _trainer.DisposeAsync(); _trainer = null; }
    _state.Update(s => s with { Connected = false });
    _guard.Reset();                                           // neuer Trainer kennt die alten Zielwerte nicht
    _message = "Verbinde neu …";
    await ConnectTrainerAsync();
}

public async Task ReconnectStrapAsync()
{
    if (_strap is not null) { await _strap.DisposeAsync(); _strap = null; }
    _state.Update(s => s with { StrapConnected = false });
    await ConnectStrapAsync();
}
```

- [ ] **9.9** Test:
  - Zahnrad klicken, das Fenster öffnet sich. Strg+Alt+O öffnet es auch bei aktivem Klick-durch.
  - Hintergrund `#102040`, Deckkraft 30 %, Größe 1,3, „Übernehmen“: Das Overlay ändert sich sofort und bleibt nach einem Neustart so.
  - Als Farbe „rot“ eintragen: Es erscheint ein Hinweis, nichts wird übernommen.
  - Pulsgurt suchen und zuordnen. Der Puls erscheint im Overlay.
  - Eine .zwo- und eine .gpx-Datei importieren, eine kaputte Datei wird abgelehnt. „Laden“ startet sie, „Löschen“ entfernt sie.
  - Eine Datei aus dem Explorer aufs Overlay ziehen: Sie wird importiert und geladen.
  - Auf einer Strecke das Fahrergewicht um 10 kg erhöhen: Bergauf wird die virtuelle Geschwindigkeit sofort kleiner.

**Tastenkürzel im Überblick**

| Kürzel | Funktion |
| --- | --- |
| Strg+Alt+D | Klick-durch an oder aus |
| Strg+Alt+Bild↑ / Bild↓ | Zielwatt plus oder minus 10 W |
| Strg+Alt+P | Workout pausieren oder fortsetzen |
| Strg+Alt+N | nächstes Intervall |
| Strg+Alt+O | Einstellungen öffnen |

**Fertig, wenn:** alle Einstellungen ohne Neustart wirken, nach einem Neustart erhalten bleiben und Workouts und Strecken über die Bibliothek oder per Drag & Drop geladen werden. Danach `git tag v0.2; git push --tags`.

## FTMS-Referenz

Alles, was du zum Programmieren brauchst, auf einen Blick. Alle Mehrbyte-Werte sind Little-Endian. Die 16-Bit-UUIDs werden unter Windows zu `0000XXXX-0000-1000-8000-00805F9B34FB`.

**Characteristics**

| Characteristic | UUID | Zugriff | Zweck |
| --- | --- | --- | --- |
| Fitness Machine Feature | 0x2ACC | Read | Welche Funktionen der Trainer kann |
| Indoor Bike Data | 0x2AD2 | Notify | Messwerte, typisch einmal pro Sekunde |
| Supported Resistance Level Range | 0x2AD6 | Read | Erlaubte Widerstandsstufen |
| Supported Power Range | 0x2AD8 | Read | Minimum, Maximum und Schrittweite für ERG |
| Fitness Machine Control Point | 0x2AD9 | Write + Indicate | Befehle und Antworten |
| Fitness Machine Status | 0x2ADA | Notify | Meldungen, z. B. Kontrolle verloren |

**Control-Point-Befehle**

| Opcode | Befehl | Parameter |
| --- | --- | --- |
| 0x00 | Request Control | keine |
| 0x01 | Reset | keine |
| 0x05 | Set Target Power (ERG) | sint16, 1 W |
| 0x07 | Start or Resume | keine |
| 0x08 | Stop or Pause | uint8: 0x01 Stop, 0x02 Pause |
| 0x11 | Set Indoor Bike Simulation Parameters | Wind sint16 (0,001 m/s), Steigung sint16 (0,01 %), Crr uint8 (0,0001), Cw uint8 (0,01 kg/m) |
| 0x80 | Antwort des Trainers | Befehls-Opcode + Ergebnis: 0x01 Erfolg, 0x02 nicht unterstützt, 0x03 ungültiger Parameter, 0x04 fehlgeschlagen, 0x05 Kontrolle nicht erlaubt |

**Echtes Beispiel aus einem Zwift-Mitschnitt:** `11 00 00 60 01 28 33` bedeutet Opcode 0x11, Wind 0, Steigung 0x0160 = 352 also 3,52 %, Crr 0x28 = 40 also 0,0040, Cw 0x33 = 51 also 0,51 kg/m ([Zwift-Forum](https://forums.zwift.com/t/zwift-documentation-for-interfacing-with-smart-trainers/20521)). Diese Crr- und Cw-Werte kannst du direkt übernehmen.

**Indoor Bike Data (0x2AD2)**

Die ersten 2 Bytes sind Flags. Danach folgen nur die Felder, deren Bit gesetzt ist, in genau dieser Reihenfolge ([Feldtabelle](https://docs.embassy.dev/trouble-host/0.2.3/default/prelude/characteristic/constant.INDOOR_BIKE_DATA.html)).

| Bit | Feld | Typ | Einheit |
| --- | --- | --- | --- |
| 0 | Instantaneous Speed, **vorhanden wenn Bit 0 = 0** | uint16 | 0,01 km/h |
| 1 | Average Speed | uint16 | 0,01 km/h |
| 2 | Instantaneous Cadence | uint16 | 0,5 U/min |
| 3 | Average Cadence | uint16 | 0,5 U/min |
| 4 | Total Distance | uint24 | 1 m |
| 5 | Resistance Level | 2 Bytes (FTMS 1.0) oder 1 Byte (neuere Tabelle) | Stufe |
| 6 | Instantaneous Power | sint16 | 1 W |
| 7 | Average Power | sint16 | 1 W |
| 8 | Energie: gesamt, pro Stunde, pro Minute | uint16 + uint16 + uint8 | kcal |
| 9 | Heart Rate | uint8 | Schläge/min |
| 10 | Metabolic Equivalent | uint8 | 0,1 MET |
| 11 | Elapsed Time | uint16 | 1 s |
| 12 | Remaining Time | uint16 | 1 s |

**Beispiel (konstruiert):** `44 00 A4 0B B4 00 C8 00`. Flags 0x0044 setzen Bit 2 und 6, Bit 0 ist 0. Also: Speed 0x0BA4 = 2980 = 29,80 km/h, Trittfrequenz 0x00B4 = 180 = 90 U/min, Leistung 0x00C8 = 200 W.

**Robuster Parser in drei Regeln:**

1. Ist Bit 5 gesetzt, die erwartete Paketlänge einmal mit 2 und einmal mit 1 Byte für Resistance Level ausrechnen. Die Variante nehmen, die zur tatsächlichen Länge passt, und sich pro Gerät merken.
2. Ist das Paket zu kurz, verwerfen und als Hex loggen, nie raten. Ist es länger als angekündigt, die angekündigten Felder vorne lesen und den Rest ignorieren. Der Van Rysel D100 setzt zum Beispiel nur Bit 6, schickt aber 16 statt 6 Bytes.
3. Manche Trainer verteilen Werte auf mehrere Pakete (Bit 0 = 1, dann fehlt der Speed). Deshalb pro Feld den letzten bekannten Wert halten statt alles zu überschreiben.

## Strecken & Physik

Die Strecke liefert nur die Steigung, die Geschwindigkeit rechnet deine App selbst aus Watt und Physik. Trainergefühl und virtuelle Geschwindigkeit sind damit zwei getrennte Stellschrauben, und genau das macht den Sim-Modus stabil.

**GPX-Verarbeitung in fünf Schritten**

1. Punkte mit Breite, Länge und Höhe aus `trkpt` oder `rtept` lesen (GPX-1.1-Namespace beachten). Datei ohne Höhendaten mit klarer Fehlermeldung ablehnen.
2. Abstände zwischen Punkten mit der Haversine-Formel berechnen (Erdradius 6.371.000 m) und aufsummieren.
3. Auf feste 10-m-Abschnitte umrechnen (linear interpolieren). Danach ist jede Strecke gleich aufgebaut, egal wie dicht die Originalpunkte waren.
4. Höhe mit gleitendem Mittel über etwa 100 m glätten. Ohne Glättung erzeugt GPS-Rauschen Steigungsspitzen von 20 % und mehr auf wenigen Metern.
5. Steigung pro Abschnitt = Höhendifferenz / Strecke, begrenzen auf −10 % bis +20 % (einstellbar).

**Geschwindigkeitsmodell**

Leistungsbilanz mit Steigungswinkel θ = arctan(Steigung):

```latex
P \cdot \eta = v \cdot \left( m g \sin\theta + C_{rr}\, m g \cos\theta + \tfrac{1}{2}\, \rho\, C_dA\, (v + v_w)^2 \right)
```

Nicht nach v auflösen, sondern über die Bewegungsenergie integrieren. Das ist stabil bei Stillstand und ergibt realistisches Beschleunigen und Ausrollen:

```latex
E_{neu} = \max\left(0,\; E + \Delta t \cdot (P \eta - F_{ges}\, v)\right), \qquad v = \sqrt{2E / m}
```

Rechentakt: 4-mal pro Sekunde (Δt = 0,25 s). Die Position auf der Strecke rückt pro Takt um v × Δt vor.

| Parameter | Startwert | Hinweis |
| --- | --- | --- |
| m (Fahrer + Rad) | z. B. 75 + 8 kg | in den Einstellungen |
| g | 9,81 m/s² |  |
| Crr | 0,004 | wie Zwift |
| ρ (Luftdichte) | 1,225 kg/m³ | Meereshöhe, 15 °C |
| CdA | 0,32 m² | Richtwert Oberlenker, Zeitfahrhaltung niedriger |
| η (Antrieb) | 0,97 | Kettenverluste |

**Zwei Kontrollwerte für deine Unit-Tests** (mit den Werten oben selbst nachgerechnet): 200 W auf flacher Strecke ergeben etwa 33,9 km/h. 250 W bei 5 % Steigung ergeben etwa 17,9 km/h. Weicht deine Implementierung deutlich ab, steckt ein Fehler drin.

**An den Trainer geht separat:** Steigung mal Schwierigkeitsfaktor, dazu Crr 0,0040 und Cw 0,51 kg/m wie bei Zwift. Steigungswechsel auf höchstens 1 Prozentpunkt pro Sekunde begrenzen, damit der Widerstand nicht ruckartig springt.

## Stabilitäts-Review

Ergebnis der Gegenprüfung: Das Gesamtsystem ist stabil, aber nur mit drei Bausteinen, die im ersten Entwurf fehlten oder nur angedeutet waren. Ein **Modus-Wächter** (immer genau ein Steuermodus), eine **Befehlsschlange nach „neuester Wert gewinnt“** und ein **vollständiges Wiederverbinden** inklusive neuer Abos. Alle Funde sind unten aufgelistet und in den Phasenplan eingearbeitet.

| # | Risiko | Was passiert | Gegenmaßnahme | Phase |
| --- | --- | --- | --- | --- |
| 1 | Zwei Steuermodi gleichzeitig | Workout sendet Watt, Strecke sendet Steigung, der Trainer springt zwischen ERG und Sim | Modus-Wächter erlaubt genau einen Modus. Bei ERG plus Strecke wird nie 0x11 gesendet | 3, 5 |
| 2 | Befehlsstau | Trainer antwortet langsam, die Schlange wächst, Ziele kommen verspätet an | Wartende Zielbefehle ersetzen statt anhängen | 3 |
| 3 | Kontrolle verloren | Zwift oder Hersteller-App übernimmt, Befehle werden mit 0x05 abgelehnt | Fitness Machine Status (0x2ADA) abonnieren, dann Request Control neu senden | 3 |
| 4 | Verbindungsabbruch | Nach dem Reconnect kommen keine Daten, weil die Abos weg sind | Bei jedem Reconnect Abos neu schreiben, Request Control, Start, letzten Zielwert senden | 8 |
| 5 | Veraltete Watt | Keine Pakete mehr, die Physik rechnet mit dem alten Wert weiter | Nach 3 s ohne Paket Leistung = 0 und Warnung im Overlay | 5 |
| 6 | Threading | Bluetooth-Ereignisse kommen auf Hintergrund-Threads, direkter UI-Zugriff stürzt ab | Zustand als unveränderlichen Schnappschuss austauschen, die UI liest ihn per Timer | 1, 2 |
| 7 | Overlay hinter dem Vollbild | Der Browser schiebt sein Vollbildfenster nach vorn | „Immer oben“ alle paar Sekunden neu setzen, in Chrome, Edge und Firefox testen | 2 |
| 8 | Hotkey-Kollision | Leertaste und Pfeiltasten steuern YouTube | Nur Strg+Alt-Kombinationen verwenden | 2 |
| 9 | Energiesparen | PC oder Bluetooth-Adapter schläft nach einer Weile ein | Während einer Session Standby per `SetThreadExecutionState` verhindern, im Geräte-Manager Energiesparen des Adapters abschalten | 8 |
| 10 | Schwacher Funk | Aussetzer, wenn der PC weit weg steht oder 2,4-GHz-WLAN stört | USB-Bluetooth-Stick am Verlängerungskabel nahe am Trainer. Letzte Stufe: ANT+ | 1 |
| 11 | ERG bei Pause | Hörst du auf zu treten, steigt der Widerstand und das Wiederanfahren wird schwer | Bei Pause 0x08 senden, beim Fortsetzen 0x07 und Zielwert erneut | 4 |
| 12 | Zwei Geschwindigkeiten | Trainer meldet eigenen Speed, die App rechnet einen anderen | Anzeige und Aufzeichnung nutzen nur Physik-Geschwindigkeit und -Distanz | 5, 7 |

**Weitere Grundregeln, die quer durch alle Phasen gelten:**

- Intern nur SI-Einheiten (m, s, W). Umgerechnet wird erst bei Anzeige und Export.
- Zeitmessung mit `Stopwatch`, nie mit der Uhrzeit. Die Uhrzeit kann springen.
- Gerät aus dem UI-Thread öffnen, weil Windows dabei eine Zustimmung abfragen kann. Beim Beenden alle Bluetooth-Objekte freigeben (`Dispose`), sonst bleibt der Trainer für andere Apps belegt ([Microsoft](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)).
- Windows arbeitet Bluetooth-Anfragen nacheinander ab, jede kann bei Funkproblemen bis zu 7 s hängen. Deshalb nie mehrere Befehle parallel abschicken.
- Ein `FakeTrainer` mit derselben Schnittstelle wie der echte erzeugt Testdaten. So entwickelst du Overlay, Workouts und Strecken auch ohne Fahrrad.

**Abnahmetest für die fertige App:**

- [ ] Zwei Stunden mit YouTube im Vollbild, Overlay sichtbar, CPU unter 1 %.
- [ ] Trainer während eines ERG-Intervalls aus- und einschalten: App verbindet neu und setzt das Ziel wieder.
- [ ] Hersteller-App am Handy öffnen und Kontrolle übernehmen lassen: App erkennt den Verlust und holt sich die Kontrolle zurück.
- [ ] Workout mit FreeRide-Abschnitt auf einer Strecke: Modus wechselt sauber zwischen ERG und Sim.
- [ ] Export bei Strava hochladen: Distanz und Dauer stimmen.

## Quellen und Referenzprojekte

**Zum Abschauen beim Programmieren**

- [qdomyos-zwift, ftmsbike.cpp](https://github.com/cagnulein/qdomyos-zwift/blob/569036d8/src/devices/ftmsbike/ftmsbike.cpp): Open-Source-App mit fertigem FTMS-Parser und Steuerlogik (C++, gut lesbar). Ablauf beim Start dort ebenfalls Request Control, dann Start ([Erklärung](https://deepwiki.com/cagnulein/qdomyos-zwift/3.2-data-flow)).
- [Microsoft Bluetooth-LE-Beispiel](https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/BluetoothLE): offizielles C#-Beispiel für Scannen, Verbinden und Abonnieren.

**Belege für die Angaben in diesem Plan**

- [Microsoft: Bluetooth GATT Client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client): Verbindungsaufbau, MaintainConnection, Abos, 7-s-Timeout, Dispose.
- [Microsoft: Windows-Runtime-APIs in Desktop-Apps](https://learn.microsoft.com/en-nz/windows/apps/desktop/modernize/winrt-apis-desktop-apps): Windows-spezifisches Projektziel für .NET 10.
- [Microsoft: .NET-Support-Policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): .NET 10 LTS bis 14. November 2028.
- [JetBrains: Rider registrieren](https://www.jetbrains.com/help/rider/2026.2/Register.html): kostenlose Lizenz für private Nutzung, Laufzeit, Telemetrie.
- [Feldtabelle Indoor Bike Data](https://docs.embassy.dev/trouble-host/0.2.3/default/prelude/characteristic/constant.INDOOR_BIKE_DATA.html): Bits, Typen und Einheiten.
- [Zwift-Forum: Mitschnitt der Sim-Befehle](https://forums.zwift.com/t/zwift-documentation-for-interfacing-with-smart-trainers/20521): Opcode 0x11, Crr und Cw.
- [SmallEarthTech.AntPlus auf NuGet](https://www.nuget.org/packages/smallearthtech.antplus): ANT+-Bibliothek für Plan B.

**Nicht extern belegt, sondern eigene Rechnung oder Erfahrungswert:** Stundenschätzungen, die Kontrollwerte der Physik, CdA 0,32 m², die Glättungslänge von 100 m und die Steigungsgrenzen. Diese Werte vor dem ersten echten Training am eigenen Setup prüfen.
