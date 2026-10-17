# JoyPro GUI (Preview)

Dieses Verzeichnis enthaelt den Startpunkt fuer eine neue, modernere JoyPro-GUI.

## Inhalte

- `JoyPro.Gui/`:
  Avalonia MVVM Desktop-App (UI-Shell + Engine Command Runner).
- `docs/GUI_STRATEGY.md`:
  UX- und Technologieentscheidung, Architektur und naechste Schritte.
- `docs/UX_BLUEPRINT.md`:
  konkrete UX-Flows, Informationsarchitektur und Qualitaetsziele.
- `docs/FEATURE_PARITY_UI_MAP.md`:
  Zuordnung aller CLI-Features auf GUI-Bereiche.
- `docs/GUI_USER_WIKI.md`:
  Schritt-fuer-Schritt Nutzerhandbuch in einfacher Sprache (Beginner-first).
- `docs/mockups/`:
  visuelle Mockups inkl. beginner-first + parity Screens.

Engine CLI Doku:
- `..\engine\docs\ENGINE_CLI_WIKI.md`:
  Vollstaendige Command-Referenz (aus `engine --help` erzeugt) mit Argument-Erklaerungen und Beispielen.

## Starten

1. Build:
   `dotnet build .\gui\JoyPro.Gui\JoyPro.Gui.csproj`
2. Run:
   `dotnet run --project .\gui\JoyPro.Gui\JoyPro.Gui.csproj`

## Aktueller Scope

- Engine-Pfad + Profil-Pfad setzen
- Quick Actions:
  - Profile Summary
  - Connected Devices
  - DB Summary
- Freies Ausfuehren von Engine-CLI Befehlen
- Ausgabe/Exit Code/Duration direkt in der GUI
