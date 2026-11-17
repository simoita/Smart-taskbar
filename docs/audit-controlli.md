# Audit dei controlli di Nebula

Data audit: 17 novembre 2026. Ambito: Studio (`SettingsWindow` e tutte le pagine), pagina Icone di sistema e controlli della dock che modificano la taskbar.

## Riproduzione

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/audit-ui-controls.ps1 `
  -Output artifacts/selftest/control-audit.txt
```

Risultato registrato:

```text
=== Studio (SettingsWindow + all pages) ===
settings_bindings: 74
settings_editors_twoway: 59
event_attributes: 43
unique_handlers: 30
missing_handlers: 0
settings_with_editors_on_multiple_pages: 0
same_page_repeated_bindings: 15

=== System icons page ===
event_attributes: 5
unique_handlers: 5
missing_handlers: 0

RESULT: PASS
```

Le 15 ripetizioni nello stesso pagina sono **un solo editor più una etichetta di valore in sola lettura** (per esempio lo slider `GlassOpacity` e il testo `70%`). Non sono due controlli che modificano la stessa impostazione:

| Pagina | Impostazione | Editor | Etichetta valore |
|---|---|---:|---:|
| Stile | Vetro | 1 | 1 |
| Stile | Raggio | 1 | 1 |
| Stile | Altezza dock | 1 | 1 |
| Stile | Dimensione icone | 1 | 1 |
| Stile | Spaziatura | 1 | 1 |
| Dock | Distanza dal bordo | 1 | 1 |
| Dock | Spaziatura zone | 1 | 1 |
| Dock | Scala app | 1 | 1 |
| Dock | Scala sistema | 1 | 1 |
| Dock | Scala azioni | 1 | 1 |
| Dock | Opacità ridotte | 1 | 1 |
| Dock | Opacità chiuse | 1 | 1 |
| Schede | Altezza barra | 1 | 1 |
| Ricerca | Larghezza pannello | 1 | 1 |
| Animazioni | Velocità | 1 | 1 |

Nessuna impostazione con editor compare su due pagine. Il file completo con righe e handler è in `artifacts/selftest/control-audit.txt`.

## Effetto di ogni gruppo di controlli

### Barra del titolo e navigazione
- Riduci a icona: `WindowState = Minimized`; Chiudi: chiude lo Studio; trascinamento: `DragMove`.
- Dieci voci di navigazione: `ShowPage` rende visibile una sola pagina e aggiorna titolo/sottotitolo.
- `SizeChanged` del Chrome: aggiorna il clip arrotondato della finestra.

### Stile
- Card dei temi: imposta `ThemeId`, aggiorna `ThemeEngine`, anteprima live e salvataggio.
- Cinque pulsanti superficie: impostano `SurfaceStyle`, indipendente da `TabsStyle`.
- Vetro, raggio, altezza dock, dimensione icone, spaziatura: binding `Settings.*` usati da `ThemeEngine`, `DockViewModel` e `DockWindow`.
- Glow, grana, linea luminosa: binding usati direttamente dai layer della dock.
- Accenti: `AccentHex`, `SetAccent`/`ApplyAccentInput`, aggiornamento del tema.
- Icona di un collegamento: `SetShortcutIcon` scrive `IconLocation` nel `.lnk` tramite WScript.Shell.

### Dock
- Alto/basso, monitor, auto-hide, distanza bordo, schermo intero: consumati da `DockWindow` e `RefreshScreenGeometry`.
- Sostituisci taskbar, riserva spazio, barra a tutta larghezza: consumati da `NativeTaskbarController`, `TaskbarAppBar` e layout full-width.
- Ordine zone, visibilità zone, divisori, spaziature e scale: `ApplyZoneLayout` ricostruisce davvero le zone.
- Indicatori apri/riduci/chiuse e stile indicatore: `DockItemViewModel`.
- App fissate: aggiunta/rimozione/ripristino; icona per app in `CustomIcons`/`CustomIconStyles`.
- La vecchia lista duplicata delle icone nella pagina Stile è stata rimossa: l'editor per-app esiste solo qui.

### Schede
- Abilita schede: `TabGroupService.IsEnabled` e pulsanti della zona azioni.
- Stile, altezza, posizione: `TabBarWindow`.
- Crea/chiudi gruppi: `App.ShowGroupPicker` / `CloseAllTabGroups`.
- `TabsStyle` non modifica più `SurfaceStyle`: sono due impostazioni separate.

### Widget
- Widget nella taskbar: aggiunta, rimozione e frecce modificano `TaskbarWidgets`; la dock usa la stessa lista nell'ordine salvato.
- Orologio: data, secondi, formato 24h; la data della taskbar usa `ShowDate`.
- Pannello widget: orologio grande, monitor sistema e batteria sono etichette esplicite del **pannello**, non della taskbar.
- Catalogo pannello: `PanelWidgets`; pulsante Apri pannello: `App.ShowWidgets`.
- I vecchi toggle globali di orologio/statistiche/batteria sono stati rimossi dal gestore taskbar per evitare ambiguità.

### Ricerca, controlli, animazioni
- Ricerca: visibilità del pulsante, inclusione finestre, larghezza pannello; Apri ricerca chiama il launcher.
- Controlli: visibilità, modalità compatta, apertura pannello.
- Animazioni: abilitazione, velocità, ingrandimento hover e impulso di avvio; tutti letti da `DockWindow`/`DockWindow.xaml.cs`.
- `HoverMagnify` non è più un controllo morto: `OnDockItemEnter/Leave` anima la scala dell'icona.

### Comportamento e info
- Avvio con Windows: `StartupManager.Apply`.
- Ripristina taskbar: mostra la shell nativa, rimuove l'AppBar e salva `ReplaceWindowsTaskbar=false`.
- Apri cartella impostazioni: apre la directory reale dello store.
- Controlla/installa aggiornamento: `UpdateCoordinator`.
- Ripristina tutto: `SettingsStore.ResetAll` + `App.ApplyAll`; Esci: chiusura con ripristino.

## Controlli della pagina Icone di sistema
- Applica tutto / ripristina tutto: `SystemIconsViewModel.ApplyAllAsync` / `RestoreAsync`, con backup reversibile.
- Scegli icona per voce / ripristina voce: operazione sulla singola superficie.
- Apri backup: apre la cartella del backup.
- Ricerca e filtri: `ApplyFilter` su `Items`; conteggi aggiornati.

## Duplicati eliminati
1. **Icone app fissate**: la lista nella pagina Stile duplicava la gestione della pagina Dock; ora esiste solo nella pagina Dock.
2. **Orologio/statistiche/batteria**: i toggle globali non controllavano più la taskbar dopo l'introduzione dei widget. Ora la taskbar è gestita dalla lista widget; il pannello ha toggle espliciti e separati.
3. **Stile schede / stile superficie**: rimossa l'accoppiamento; cambiare le schede non cambia più la superficie della dock.

## Limiti dell'audit
L'audit statico verifica binding, handler e duplicati; il selftest verifica a runtime taskbar, AppBar, widget e layout. I dialoghi esterni (scelta file icona, picker app, download aggiornamenti) sono cablati e compilati ma non vengono aperti automaticamente dal selftest per non modificare il profilo utente. I test runtime sono nel report `artifacts/selftest/<timestamp>/selftest-report.txt`.
