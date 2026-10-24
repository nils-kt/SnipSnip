<div align="center">

<img src="docs/images/logo.png" width="80" alt="SnipSnip-Logo">

# SnipSnip

**Ein schönes Snipping Tool für Windows: Screenshots und Bildschirmvideos, mit Editor, Zensur, Videoschnitt und Upload mit einem Klick.**

Drück <kbd>Pause</kbd>, zieh einen Bereich auf, und schon kannst du markieren, unkenntlich machen und teilen.

[**⬇ Download (neueste Version)**](../../releases/latest) · [Funktionen](#funktionen) · [Bauen](#selbst-bauen) · [Mitmachen](#mitmachen)

<br>

<img src="docs/images/hero.png" alt="SnipSnip-Aufnahmeleiste mit Bereichsauswahl, Größenanzeige und Pixel-Lupe">

</div>

---

## Funktionen

### Die Leiste oben am Bildschirm

Ein Druck auf <kbd>Pause</kbd> friert den Bildschirm ein, und oben in der Mitte erscheint die Aufnahmeleiste. Dort wählst du alles direkt aus:

- **Foto oder Video:** ganz links zwischen Bildschirmfoto und Bildschirmvideo umschalten
- **Modus:** Rechteck, Fenster, Freihand oder Vollbild/Monitor (Tasten <kbd>1</kbd>–<kbd>4</kbd>)
- **Verzögerung:** Aufnahme nach 3, 5 oder 10 Sekunden
- **Danach:** im Editor öffnen, kopieren, speichern oder **direkt hochladen** (Imgur / catbox.moe). Der Link landet dann in der Zwischenablage.
- **Präzision:** Lupe mit Pixel-Koordinaten und Farbwert. Die Pfeiltasten bewegen den Zeiger pixelgenau, <kbd>C</kbd> kopiert die Farbe als Hex-Wert.
- **Schnell:** Ein Klick ohne Ziehen nimmt das Fenster unter dem Mauszeiger auf, <kbd>Strg</kbd>+Klick im Vollbild-Modus alle Monitore.

<img src="docs/images/capture-window.png" alt="Fenster-Modus: Das Fenster unter dem Mauszeiger wird hervorgehoben">

### Editor: markieren, beschriften, zensieren

<img src="docs/images/editor.png" alt="Editor mit Rechteck, Pfeil, Textmarker, Nummern, Textlabel sowie verpixelter, weichgezeichneter und geschwärzter Information">

| Werkzeug | Taste | |
|---|:---:|---|
| Auswählen & verschieben | <kbd>V</kbd> | <kbd>Entf</kbd> löscht, Rechtsklick löscht direkt |
| Stift · Textmarker | <kbd>P</kbd> · <kbd>M</kbd> | <kbd>Umschalt</kbd> = gerade Linie |
| Linie · Pfeil | <kbd>L</kbd> · <kbd>A</kbd> | <kbd>Umschalt</kbd> = 15°-Schritte |
| Rechteck · Ellipse | <kbd>R</kbd> · <kbd>E</kbd> | <kbd>Umschalt</kbd> = Quadrat/Kreis, optional gefüllt |
| Text | <kbd>T</kbd> | optional mit farbigem Hintergrund |
| Nummerierung | <kbd>N</kbd> | setzt fortlaufend ①②③ |
| **Verpixeln · Weichzeichnen · Schwärzen** | <kbd>X</kbd> · <kbd>B</kbd> · <kbd>S</kbd> | Die Pixel werden aus dem Originalbild neu berechnet. Hinter der Zensur bleibt nichts Lesbares übrig. |
| Zuschneiden | <kbd>C</kbd> | |

Außerdem gibt es Rückgängig/Wiederholen, Zoom (<kbd>Strg</kbd>+Mausrad), Verschieben der Ansicht mit der mittleren Maustaste und Drag & Drop von Bildern. Die Freihand-Aufnahme behält Transparenz.

### Bildschirmvideos aufnehmen

In der Leiste auf **Video** umschalten und einen Bereich, ein Fenster oder einen Monitor wählen. Nach einem kurzen 3-2-1-Countdown läuft die Aufnahme. Erneut <kbd>Pause</kbd> drücken beendet sie.

<img src="docs/images/recording.png" alt="Laufende Aufnahme mit gestricheltem Rahmen, Klick-Markierung und Steuerleiste">

- **System-Ton und Mikrofon** lassen sich direkt in der Leiste zuschalten. Beide bleiben synchron zum Bild.
- **Steuerleiste** mit Laufzeit, Pause/Fortsetzen, Stopp und Verwerfen. Leiste und Rahmen sind im Video selbst unsichtbar.
- **Mauszeiger und Klicks** werden eingezeichnet. Klicks erscheinen als gelber Ring, damit Zuschauer sie sehen.
- 15, 30 oder 60 Bilder pro Sekunde, H.264-MP4 mit wählbarer Qualität.

### Video-Editor: schneiden, kürzen, als GIF exportieren

<img src="docs/images/video-editor.png" alt="Video-Editor mit Vorschau, Zeitleiste aus Vorschaubildern, entfernten Abschnitten und Export-Optionen">

| Aktion | Taste | |
|---|:---:|---|
| Abspielen / Pause | <kbd>Leertaste</kbd> | Entfernte Abschnitte werden in der Vorschau übersprungen. |
| Bild für Bild | <kbd>←</kbd> <kbd>→</kbd> | mit <kbd>Umschalt</kbd> in 1-Sekunden-Schritten |
| Anfang · Ende setzen | <kbd>A</kbd> · <kbd>E</kbd> | schneidet alles vor bzw. nach der Abspielposition weg |
| Teilen | <kbd>S</kbd> | teilt an der Abspielposition |
| Abschnitt entfernen / wiederherstellen | <kbd>Entf</kbd> | Abschnitt vorher in der Zeitleiste anklicken |

Exportiert wird als **MP4** oder **GIF** (10/15/24 fps), in 100, 75 oder 50 % Größe, wahlweise ohne Ton. **Kopieren** legt die Videodatei in die Zwischenablage, sodass du sie direkt in Discord, Teams oder den Explorer einfügen kannst. **Hochladen** schickt sie zu Imgur oder catbox.moe.

> Für Videos nutzt SnipSnip das freie [FFmpeg](https://ffmpeg.org). Fehlt es, bietet SnipSnip beim ersten Video an, es herunterzuladen: offizieller Build von [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), per SHA-256 geprüft. Alternativ: `winget install Gyan.FFmpeg`.

### Einstellungen & Benachrichtigungen

<table>
<tr>
<td width="46%"><img src="docs/images/settings.png" alt="Einstellungen: Tastenkürzel, Standard-Aktion, Speicherort, Upload-Dienst"></td>
<td>

- Tastenkürzel frei wählbar (<kbd>Pause</kbd>, <kbd>Druck</kbd>, <kbd>F9</kbd>, <kbd>Strg</kbd>+<kbd>Umschalt</kbd>+<kbd>S</kbd> …)
- Jede Aufnahme automatisch kopieren und/oder speichern
- PNG oder JPG, eigene Ordner für Bilder und Videos
- Video: Bildrate, Qualität, Ton, Mauszeiger, Klick-Markierung, Countdown
- Upload zu **Imgur** (eigene Client-ID) oder **catbox.moe** (ohne Konto)
- Upload-Verlauf mit Lösch-Links für Imgur
- Start mit Windows (im Tray)

<br>

<img src="docs/images/toast.png" alt="Benachrichtigung nach dem Upload mit Vorschaubild und Link">

</td>
</tr>
</table>

## Installation

1. Lade die passende ZIP-Datei aus den [**Releases**](../../releases/latest) herunter:
   - `SnipSnip-x.y.z-win-x64.zip`: eigenständig, keine Installation nötig (empfohlen)
   - `SnipSnip-x.y.z-win-arm64.zip`: für Windows auf ARM
   - `SnipSnip-x.y.z-win-x64-requires-dotnet10.zip`: sehr klein, benötigt die [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Entpacken und `SnipSnip.exe` starten. Die App läuft im Tray.
3. <kbd>Pause</kbd> drücken. Fertig.

> **Tipp:** Wenn du die <kbd>Druck</kbd>-Taste als Kürzel nutzen willst, schalte unter *Windows-Einstellungen › Barrierefreiheit › Tastatur* die Option „Druck-Taste zum Öffnen des Bildschirmausschnitts verwenden“ aus.

### Imgur einrichten

Anonyme Uploads zu Imgur brauchen eine kostenlose Client-ID:

1. Registriere eine Anwendung unter <https://api.imgur.com/oauth2/addclient>.
2. Wähle „Anonymous usage without user authorization“.
3. Trag die Client-ID in den SnipSnip-Einstellungen ein.

Ohne Client-ID kannst du alternativ zu **catbox.moe** hochladen.

### Kommandozeile

| Aufruf | Wirkung |
|---|---|
| `SnipSnip.exe` | Startet im Tray. Läuft SnipSnip schon, startet direkt eine neue Aufnahme (praktisch als Taskleisten-Verknüpfung). |
| `SnipSnip.exe <datei>` | Öffnet ein Bild im Editor oder ein Video im Video-Editor (auch per „Öffnen mit …“) |
| `SnipSnip.exe --capture` | Startet sofort eine Aufnahme |
| `SnipSnip.exe --tray` | Startet still im Tray (wird für den Autostart verwendet) |
| `SnipSnip.exe --render-docs <ordner>` | Rendert die README-Bilder (siehe unten) |

Einstellungen und Upload-Verlauf liegen in `%APPDATA%\SnipSnip`.

## Selbst bauen

Voraussetzungen: Windows 10/11 und das [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone <repo-url> && cd SnipSnip
dotnet build
dotnet run
```

Eine einzelne, eigenständige EXE baust du so:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

### README-Bilder neu rendern

Die Bilder in [`docs/images`](docs/images) sind **keine Screenshots**. SnipSnip rendert sie selbst aus den echten UI-Komponenten (Leiste, Overlay, Bild- und Video-Editor, Aufnahme-Leiste, Einstellungen, Toast). Der Hintergrund ist ein künstlicher Demo-Desktop mit fiktiven Daten. Nach UI-Änderungen genügt:

```bash
dotnet run -- --render-docs docs/images
```

Die CI rendert die Bilder bei jedem Push ebenfalls und hängt sie als Artefakt an. So fällt sofort auf, wenn eine UI-Komponente nicht mehr instanziiert werden kann.

### Release erstellen

Releases baut [GitHub Actions](.github/workflows/release.yml) automatisch, sobald ein Versions-Tag gepusht wird:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Die Pipeline baut die drei Varianten (x64, ARM64 und die kleine .NET-abhängige EXE), erzeugt `SHA256SUMS.txt` und veröffentlicht alles als GitHub-Release mit automatisch generierten Release-Notes. Tags mit Bindestrich (z. B. `v1.1.0-beta.1`) erscheinen als Pre-Release.

## Projektstruktur

```
App.xaml(.cs)          Tray, globales Tastenkürzel, Single-Instance, Aktionen nach der Aufnahme
Capture/               Aufnahme-Session, Overlay pro Monitor, die Leiste oben, Countdown
Editor/                Bild-Editor, Annotationen (Formen, Text, Zensur)
Recording/             Videoaufnahme (GDI → FFmpeg, WASAPI-Ton), Steuerleiste, Rahmen
Video/                 Video-Editor (Zeitleiste, Schnitt) und Export (MP4/GIF)
Core/                  Bildschirmaufnahme, FFmpeg, Bild-Export, Upload, Einstellungen, Hotkey
UI/                    Einstellungen, Upload-Verlauf, Toast, FFmpeg-Einrichtung
Rendering/             Rendert die README-Bilder
.github/workflows/     CI (Build + Render) und Release
```

## Mitmachen

Issues und Pull Requests sind willkommen. Bitte:

- vor einem PR `dotnet build` ausführen. Die CI baut und rendert alle Oberflächen.
- bei sichtbaren UI-Änderungen die README-Bilder neu rendern (`--render-docs`) und mit einchecken.
- sich am bestehenden Stil orientieren: dunkles Design, Icons als Vektor-Geometrien in `App.xaml`, UI-Texte auf Deutsch.

## Lizenz

[MIT](LICENSE)
