# Nebula Taskbar Studio

Dock e pannello di personalizzazione per Windows, scritto in WPF su .NET 8. Nebula sostituisce la taskbar con una dock in vetro, animazioni fluide, ricerca di app e finestre, widget, controlli rapidi, **schede delle finestre** e uno studio di impostazioni interamente in italiano.

L'obiettivo di questa revisione è la sicurezza d'uso quotidiano: anche terminando Nebula forzatamente, le finestre raggruppate e la taskbar di Windows devono tornare disponibili senza rilanciare nulla.

**Novità 1.2:** pannello **Icone di sistema** con stili applicabili a collegamenti `.lnk`, tipi file e cartelle utente del profilo, icone personali dal dialog di Windows, ICO multi-size 16–256 px, backup esatto e recovery dopo chiusura forzata. Dettagli in [docs/icone-sistema.md](docs/icone-sistema.md).

## Funzioni

- Dock personalizzabile: in alto o in basso, margine regolabile, auto-hide, dimensione e schermo di destinazione.
- **16 temi completi** (Nebula, Aurora, Neon Grid, Ember, Porcelain, Mint Glass, Sakura, Graphite, Solar, Ocean, Candy, Forest, Lavender, Midnight, Sunset, Sage) con accento personalizzato applicato dal vivo a dock, studio, widget e launcher.
- **5 preset superficie — Vetro / Minimal / Neon / Soft / Mono** applicati dal vivo a widget, controlli rapidi e ricerca, indipendenti dallo stile delle schede.
- **Tre zone dock indipendenti** (app, sistema, azioni): visibilità, ordine, scala, spaziatura e separatori si impostano separatamente.
- **11 widget** selezionabili dal catalogo e inseribili nel pannello: orologio, data, calendario, sistema, batteria, rete, uptime, disco, media, ricerca e controlli.
- **Stati icona distinti**: indicatore separato per app aperta, ridotta a icona e chiusa, con forma, dimensione, opacità e stile icona (originale, riquadro, cerchio, monocromatico, neon).
- **Icone personalizzate**: stile e forma globali per la dock, icona propria per ogni app fissata e cambio icona dei collegamenti `.lnk` del desktop tramite dialog di Windows.
- **Icone di sistema per-utente**: pagina **Studio → Icone** per applicare stili (vetro, pieno, riquadro, cerchio, duotono, mono, contorno, neon) a collegamenti `.lnk` di Desktop/Start menu/pin, tipi file sotto `HKCU\Software\Classes` e cartelle utente tramite `desktop.ini`. Icone personali `.ico/.png/.jpg/.exe`, ICO multi-size 16–256 px, backup esatto e ripristino verificato. Nessuna patch a `System32`. Le cartelle fuori dalle sei cartelle shell del profilo restano personalizzabili singolarmente da Windows: non esiste un override globale per-utente. Dettagli in [docs/icone-sistema.md](docs/icone-sistema.md).
- **Aggiornamenti da GitHub Releases** con popup **Aggiorna e riavvia**, download ZIP, verifica SHA-256 e riavvio: controllo all'avvio e ogni 15 minuti, attivo per impostazione predefinita; dettagli e flusso di pubblicazione in [docs/aggiornamenti.md](docs/aggiornamenti.md).
- App fissate e app in esecuzione, con menu contestuale per fissare/rimuovere.
- **Schede delle finestre**: raggruppa finestre esterne in una tab bar Nebula con titoli, icone, riordino, stacco e ripristino della geometria.
- **Ricerca app e finestre** con `Ctrl+Alt+Spazio`: cerca nel menu Start e nelle finestre aperte; frecce per navigare, `Invio` per aprire o portare in primo piano, `Esc` per chiudere.
- **Widget** con `Ctrl+Alt+W` o dalla dock: orologio, data, statistiche e grafico CPU, batteria quando disponibile.
- **Controlli rapidi** dalla dock o dallo studio: volume/mute reali, play/pausa e precedente/successivo del player attivo, stato rete e apertura delle impostazioni di rete, luminosità DDC/CI quando il monitor la espone.
- Ogni superficie ha un interruttore e almeno una regola grafica in **Studio → Widget / Ricerca / Controlli**.
- Avvio con Windows realmente registrato in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- Ripristino automatico della taskbar su uscita normale, crash e terminazione forzata.

## Requisiti

- Windows 10 1903 o successivo, oppure Windows 11.
- .NET 8 Desktop Runtime (la build in `dist/` è framework-dependent).
- Tenere `Nebula.exe` e `Nebula.Watchdog.exe` nella stessa cartella, insieme ai rispettivi `.dll`, `.deps.json` e `.runtimeconfig.json`.

## Avvio rapido

1. Apri la cartella `dist/`.
2. Avvia `Nebula.exe`.
3. Al primo avvio la taskbar nativa viene nascosta e compare la dock. Lo studio si apre dall'icona impostazioni della dock o dal menu della tray.
4. Per uscire in modo pulito: tray → **Esci da Nebula**, oppure **Info → Esci da Nebula** nello studio.

La taskbar originale viene ripristinata nello stato esatto precedente all'uscita normale.

Per aprire direttamente lo studio:

```powershell
.\dist\Nebula.exe --settings
```

L'argomento `--startup` è riservato alla voce di avvio automatico e non va usato a mano.

## Widget, controlli rapidi e ricerca

Le tre superfici riusano l'infrastruttura esistente: widget e controlli condividono la stessa finestra `WidgetsWindow`, la ricerca usa `LauncherWindow`. Non vengono avviati processi o finestre WPF aggiuntive e tutte leggono `%APPDATA%\Nebula\settings.json`.

| Superficie | Dalla dock | Dallo studio | Scorciatoia |
| --- | --- | --- | --- |
| Widget | icona **Widget** | **Widget → Apri pannello widget** | `Ctrl+Alt+W` |
| Controlli rapidi | icona **Controlli rapidi** | **Controlli → Apri controlli rapidi** | — |
| Ricerca | icona **Cerca** | **Ricerca → Apri ricerca** | `Ctrl+Alt+Spazio` |

Le icone compaiono solo per le superfici attive. In **Studio → Widget / Ricerca / Controlli** si trovano gli interruttori e le regole grafiche: larghezza ricerca 560–900 px, inclusione delle finestre nei risultati, layout controlli compatto 370 px, oltre ai toggle di orologio, data, statistiche e batteria.

### Preset superficie

In **Studio → Stile → PRESET SUPERFICI** si scelgono **Vetro**, **Minimal**, **Neon**, **Soft** o **Mono**. Il preset viene applicato dal vivo alle superfici già aperte, non solo alla riapertura:

- **Vetro:** card e controlli arrotondati, bordo vetro, slider da 4 px con pomello da 17 px, tipografia variable.
- **Minimal:** raggi più contenuti, bordo piatto, slider da 3 px con pomello da 13 px, testo Segoe UI.
- **Neon:** raggi intermedi, bordo con gradiente d'accento, slider da 5 px con pomello da 18 px, titoli in grassetto.
- **Soft:** raggi ampi, superfici più piene, ombre attenuate e tipografia morbida.
- **Mono:** raggi ridotti, contrasto alto, niente gradiente d'accento e controlli lineari.

Il preset cambia sfondi, bordi, raggi, padding, spessori, dimensioni dei controlli, font e peso dei titoli tramite risorse `S.*` condivise; le schede non vengono ridisegnate. Anche l'interruttore **Animazioni fluide** vale per queste superfici: disattivato ferma immediatamente le transizioni di ingresso, riattivato le ripristina. La ricerca ha un pulsante ingranaggio per aprire lo Studio senza chiuderla: le modifiche si vedono mentre la ricerca resta aperta e, chiudendo lo Studio, la ricerca torna in primo piano.

### Controlli rapidi: cosa è reale

- **Volume/mute** tramite endpoint COM Windows (`IAudioEndpointVolume`).
- **Media** tramite tasti multimediali di sistema: precedente, play/pausa, successivo.
- **Rete**: lettura degli adattatori attivi e apertura di `ms-settings:network-status`; l'attivazione/disattivazione richiede privilegi amministrativi e non viene simulata.
- **Luminosità**: DDC/CI quando il display lo supporta; altrimenti lo slider viene disabilitato e compare un avviso esplicito.

### Ricerca: cosa trova

Cerca le app installate nel menu Start e le finestre aperte, mostrando per queste ultime titolo, processo e stato **finestra aperta**. La tastiera è il percorso principale: frecce per scorrere, `Invio` per aprire, `Esc` per chiudere. Non indicizza l'intero catalogo Store/UWP, i file o il contenuto delle app.

Dettagli, misure e limiti: [docs/superfici.md](docs/superfici.md).

## Sicurezza della taskbar

Nebula avvia `Nebula.Watchdog.exe` quando nasconde la taskbar nativa. Il watchdog attende la fine del processo Nebula e, se trova uno stato pendente della stessa sessione, ripristina la taskbar **visibile**.

- **Uscita normale:** Nebula ripristina lo stato originale esatto, chiude dock, tooltip e pannelli, quindi rimuove lo stato pendente. Il watchdog non deve fare nulla.
- **Crash o `taskkill /F`:** il watchdog ripristina la taskbar visibile, senza rilanciare Nebula. La dock e i pannelli spariscono insieme al processo.
- **Auto-hide originale:** dopo un crash la taskbar viene mostrata visibile per sicurezza; l'auto-hide originale viene conservato solo nell'uscita normale.
- **Watchdog assente o non avviabile:** Nebula ripiega sull'auto-hide, mai sulla taskbar completamente nascosta.

Dettagli, test e diagnostica: [docs/ripristino-taskbar.md](docs/ripristino-taskbar.md).

## Schede delle finestre

Dalla dock usa l'icona **schede** o il menu contestuale **Raggruppa finestre in schede…**; dallo studio: **Schede → Scegli finestre…**. Seleziona almeno due finestre e premi **Crea gruppo**. La tab bar compare ancorata alla finestra attiva, sopra o sotto in base alle impostazioni.

- Clic su una scheda: la finestra va in primo piano, le altre del gruppo vengono nascoste.
- Trascina una scheda per riordinarla.
- × sulla scheda chiude la finestra; menu destro → **Stacca scheda** la rimuove dal gruppo e le restituisce la geometria originale.
- × a destra della barra chiude il gruppo e ripristina tutte le finestre.

Nello studio, pagina **Schede**: attiva/disattiva, stile **Vetro / Minimal / Neon / Soft / Mono**, altezza 34–64 px, posizione sopra/sotto, animazioni collegate a **Animazioni fluide**. Tutto viene applicato dal vivo e salvato.

Le finestre protette, elevate o UWP vengono rifiutate con un messaggio esplicito. I dettagli, le misure e i limiti sono in [docs/schede.md](docs/schede.md).

## Prestazioni

Misure finali della build 1.1 pubblicata in `dist/`, un monitor 1920×1080, preset superficie **Vetro**, animazioni attive, nessun gruppo di schede. La dock è stata misurata da un processo pulito dopo warm-up; la CPU è la media del tempo processo su una finestra di 20 s.

| Scenario | CPU (un core) | Memoria privata |
| --- | --- | --- |
| Dock sola, dopo warm-up 20 s | **1,17%** su 20 s | **98,1 MB** (98,4 MB altri 20 s dopo) |
| Dock + pannello Widget, subito | **0,47%** su 20 s | **128,6 MB** a 2 s dall'apertura; **130,0 MB** dopo 20 s |
| Dopo chiusura del pannello | — | **129,6 MB** a 5 s |
| Watchdog | ~0% | ~6,5 MB |

La dock sola resta **sotto il target di 116 MB**. Il pannello aperto no: il pavimento misurato è **128,6–130,0 MB**, quindi ancora sopra il bersaglio. Il costo è la seconda finestra WPF layered (`AllowsTransparency`) con il calendario, i grafici e la striscia dei widget; non è heap gestito incontrollato. Alla chiusura vengono fermati timer, grafico e calendario, svuotata anche la striscia widget e rilasciato l'endpoint COM; la memoria scende a **129,6 MB**, perché WPF/DWM trattengono cache native della finestra. Per scendere sotto il target con il pannello aperto servirebbe unire il pannello alla dock o rimuovere la superficie layered.

La tab bar con tre finestre raggruppate era stata misurata in una build precedente a **147,6 MB** e **0,78%** di CPU, con apertura in **16–29 ms**; non è stata rimisurata in questa revisione, ma non è stata modificata.

## Monitor e DPI

Lo studio permette di scegliere lo schermo della dock. La dock usa l'area di lavoro e il DPI dello schermo scelto, si riposiziona al cambio DPI e rileva il fullscreen sui limiti di quello schermo. Anche widget, controlli e ricerca vengono posizionati sullo schermo selezionato.

Su questa macchina è presente **un solo monitor fisico** (`\\.\DISPLAY5`, 1920×1080, primario): la selezione è stata verificata a schermo singolo, mentre il comportamento su un secondo monitor e su DPI misti non è stato osservato fisicamente.

## Build e pubblicazione

Dalla radice del progetto:

```powershell
.\.dotnet\dotnet.exe build .\src\Nebula\Nebula.csproj -c Release
.\.dotnet\dotnet.exe publish .\src\Nebula\Nebula.csproj -c Release -o .\dist
```

Il target MSBuild copia automaticamente i file del watchdog in `bin/` e in `dist/`. Prima di ripubblicare, chiudi Nebula: altrimenti l'eseguibile in uso resta bloccato.

La build è framework-dependent: sul PC di destinazione serve il .NET 8 Desktop Runtime.

## Release e aggiornamenti con GitHub

Il repository include `.github/workflows/release.yml`: al push di un tag `vMAJOR.MINOR.PATCH` GitHub Actions compila la build, crea `Nebula-win-x64.zip` e `update.json`, calcola lo SHA-256 e pubblica la Release. Le installazioni con aggiornamenti automatici ricevono il popup **Aggiorna e riavvia**.

Per pubblicare una versione dopo aver committato il codice:

```powershell
.\scripts\release.cmd -Version 1.3.0
```

Lo script aggiorna `<Version>` in `src/Nebula/Nebula.csproj`, crea commit e tag, e li invia a GitHub. Il primo setup del repository e i dettagli del workflow sono in [docs/aggiornamenti.md](docs/aggiornamenti.md).

## Verifiche di questa release

Eseguite sulla build pubblicata in `dist/`:

- Widget, controlli rapidi e ricerca aperti dalla dock e dallo studio; dock e studio non duplicano finestre o processi.
- Preset **Vetro → Soft → Mono → Vetro** commutati dal vivo; i 16 temi sono presenti nella pagina Stile e il preset viene salvato. La ricerca resta aperta con l'ingranaggio e si aggiorna dal vivo.
- Catalogo widget: **Uptime** e **Disco** attivati dal catalogo, comparsi subito nella striscia del pannello e salvati in `settings.json`; disattivandoli spariscono senza chiudere il pannello.
- Zone dock: ordine **App · Sistema · Azioni → Azioni · App · Sistema** applicato dal vivo alla dock, con valore persistito e poi ripristinato.
- Icone per-app: **File Explorer** impostato su **Neon** dal selettore nella scheda ICONE; il valore è stato salvato e applicato alla dock senza riavvio.
- Icone di sistema: applicato **Neon** e **Cerchio** a un vero `.lnk` del Desktop e verificato il cambio su Explorer; applicato **Neon** a `.zip` tramite il ProgID `CompressedFolder` e visto il cambio da icona ZIP a piastra; applicato **Pieno** a `.jpg`; caricato un `.png` personale dal dialog di Windows e applicato sia a un `.lnk` sia alla cartella **Desktop**, con anteprima Explorer visibile, poi ripristinato. L'ICO generato contiene **10 frame da 16 a 256 px** (256 in PNG).
- Ripristino icone di sistema: `.lnk` tornato al valore precedente, `.zip` tornato senza chiave `CompressedFolder\DefaultIcon`, `.jpg` tornato senza chiave `jpegfile\DefaultIcon`, `desktop.ini` di Desktop/Download tornato byte per byte (incluso `LocalizedResourceName` e `IconResource` originali), backup rimosso e cartella `icons` vuota. Applicate e ripristinate **156 voci** con **Applica a tutte** (29 collegamenti, 121 superfici di registro, 6 cartelle utente).
- Journal icone: ripristino interrotto e app riavviata; il journal incompleto ha riportato le voci originali. Uscita normale e `taskkill /F` senza processi residui né file `.tmp`.
- Aggiornatore automatico: manifest locale valido letto come versione **9.9.9**, pacchetto reale scaricato, SHA-256 verificato, aggiornamento applicato e processo riavviato automaticamente; verificato il passaggio dal PID **29884** al PID **20436** e il log `Automatic update ready: 9.9.9`. Il controllo manuale e il rifiuto di un hash errato restano verificati.
- Animazioni disattivate sulle superfici: nessuna transizione di ingresso residua; riattivate, le transizioni tornano.
- Misure finali da processo pulito con warm-up e campioni a 20 s: dock **98,1–98,4 MB** e **1,17%**; pannello Widget **128,6–130,0 MB** e **0,47%**; chiusura **129,6 MB**; la dock è sotto il target di 116 MB, il pannello aperto no.
- Ricerca della finestra **Senza titolo - Blocco note**: `Invio` l'ha portata in primo piano (foreground verificato). Ricerca di **Prompt dei comandi**: `Invio` ha avviato un nuovo processo `cmd` (conteggio 1 → 2).
- Ricerca con **Cerca finestre aperte** disattivato: la finestra non compare; riattivato: ricompare. Larghezza ricerca modificata, verificata alla riapertura e dopo il riavvio.
- Volume portato da 100% a 26% e ripristinato a 100%; mute reale controllato con probe COM esterno.
- Media play/pausa verificato sul Lettore multimediale reale (Sospendi ↔ Riproduci).
- Rete mostrata come `Ethernet · 1 Gbps`; apertura delle impostazioni di rete verificata. Luminosità non supportata dal monitor: slider disabilitato e avviso esplicito.
- Interruttori Widget, Ricerca e Controlli rapidi: pulsanti dock nascosti/ripristinati e superfici chiuse o commutate correttamente. Layout compatto applicato dal vivo (410 → 370 px).
- Animazioni disattivate: aperture e chiusure senza errori; poi riattivate.
- Persistenza dopo riavvio di **preset superficie Neon**, tema **Neon Grid**, animazioni attive, layout compatto, larghezza ricerca e inclusione finestre; il preset è stato poi riportato a Vetro.
- Uscita normale con pannello aperto: nessun processo Nebula/watchdog, `taskbar.pending` assente, taskbar nativa visibile.
- `taskkill /F` con pannello aperto: taskbar nativa visibile, nessun processo Nebula/watchdog, `taskbar.pending` assente.

## Limiti noti

- **Memoria con pannello aperto:** 128,6–130,0 MB privati, sopra il target di 116 MB; la dock sola misura 98,1–98,4 MB e dopo la chiusura si torna a 129,6 MB. Il pavimento è causato dalla seconda finestra WPF layered e dalla cache nativa; la striscia widget viene svuotata alla chiusura. Non è stato possibile scendere sotto il target con il pannello aperto senza unire il pannello alla dock o rimuovere la superficie.
- **Luminosità:** il monitor di prova non espone DDC/CI. Il controllo è correttamente disabilitato con avviso, ma non è stata osservata una variazione reale di luminosità.
- **Rete:** lettura dello stato e apertura delle impostazioni funzionano; attivare/disattivare un adattatore richiede privilegi amministrativi e non è implementato.
- **Media precedente/successivo:** i tasti vengono inviati, ma la sessione di prova aveva una sola traccia e **Avanti** era disabilitato nel player. Il cambio traccia non è stato osservato; play/pausa sì.
- **Ricerca:** copre menu Start e finestre visibili; non indicizza l'intero catalogo Store/UWP, file o contenuti.
- **Aggiornatore:** il flusso automatico è stato provato end-to-end con un manifest e un pacchetto locali; l'installazione sostituisce la build in `dist/`, quindi va pubblicata solo una build valida. Il log di copia è in `%LOCALAPPDATA%\Nebula\updates\apply-update.log` se la copia fallisce.
- **Icone personalizzate:** il selettore per-app e la persistenza dello stile sono verificati; il dialog di scelta file `.ico/.png/.jpg` e la riscrittura di un collegamento `.lnk` reale non sono stati automatizzati in questa sessione.
- Il secondo monitor fisico e i DPI misti non sono disponibili su questa macchina.
- Il test fullscreen è una finestra WinForms borderless, non un gioco esclusivo o un'app UWP protetta.
- Le finestre UWP protette o elevate non sono state osservate: il rifiuto è implementato nel selettore schede, ma il caso non è stato riprodotto.

## Documentazione

- [Widget, controlli rapidi e ricerca](docs/superfici.md)
- [Aggiornamenti](docs/aggiornamenti.md)
- [Avvio e uso](docs/avvio.md)
- [Schede delle finestre](docs/schede.md)
- [Ripristino della taskbar](docs/ripristino-taskbar.md)
