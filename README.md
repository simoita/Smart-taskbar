# Nebula Taskbar Studio

Dock e pannello di personalizzazione per Windows, scritto in WPF su .NET 8. Nebula sostituisce la taskbar con una dock in vetro, animazioni fluide, ricerca di app e finestre, widget, controlli rapidi, **schede delle finestre** e uno studio di impostazioni interamente in italiano.

L'obiettivo di questa revisione è la sicurezza d'uso quotidiano: anche terminando Nebula forzatamente, le finestre raggruppate e la taskbar di Windows devono tornare disponibili senza rilanciare nulla.

**Novità 1.7.10:** bordi più discreti e moderni su barra, Dockbar e pannelli: una linea da 1 px **neutra** che segue il tema (chiara sul vetro scuro, scura sui temi chiari) con l'accento solo come riflesso, smusso chiaro in alto e ombra in basso appena percettibili, così la superficie resta definita senza l'effetto cornice colorata.

**Novità 1.7.9:** barra e **Dockbar** sono ora la stessa superficie — stesso vetro, stesso bordo da 1 px, stesso smusso e stesso raggio — e condividono misure intere, quindi **bordi, separatori e spazi restano identici** cambiando altezza, dimensione delle icone, spaziatura, scala dell'isola o tema: le icone non coprono più l'indicatore né il riquadro accanto, la capsula dentro la barra è alta come la riga delle icone invece di essere una pillola più corta con un alone suo, l'isola espansa non lascia più vuoti né righe tagliate e i **temi chiari** hanno una barra chiara con testo scuro e un bordo visibile.

**Novità 1.2:** pannello **Icone di sistema** con stili applicabili a collegamenti `.lnk`, tipi file e cartelle utente del profilo, icone personali dal dialog di Windows, ICO multi-size 16–256 px, backup esatto e recovery dopo chiusura forzata. Dettagli in [docs/icone-sistema.md](docs/icone-sistema.md).

**Novità 1.7.8:** le **icone più piccole occupano meno spazio**: il riquadro di ogni icona segue la dimensione scelta, quindi passando da 36 a 30 o 24 px le icone si **avvicinano davvero** invece di restare nei vecchi spazi; anche Start si stringe con loro e la barra mantiene l'altezza scelta.

**Novità 1.7.7:** le **icone della barra** sono più piccole e proporzionate: **Piccole 24 px**, **Medie 30 px** e **Grandi 36 px**, quindi la nuova *Grande* corrisponde alla vecchia *Piccola* e i profili salvati vengono riportati da soli nella nuova scala; la **sostituzione della taskbar** segue ora il protocollo AppBar completo — risponde ad `ABN_POSCHANGED` con una nuova prenotazione, invia `ABM_ACTIVATE` e `ABM_WINDOWPOSCHANGED` alla shell e, quando Explorer si riavvia, riconosce la nuova `Shell_TrayWnd`, le rimette l'auto-hide e la nasconde subito, così lo spazio riservato non si perde più.

**Novità 1.7.6:** l'**espansione della Dockbar** è fluida e senza rimbalzi, e quando la blocchi con un clic un **bordo accentato** te lo dice subito; le **zone** della barra (app, widget, azioni) non possono più **sovrapporsi** tra loro, nemmeno con i widget più lunghi: lo spazio di ogni lato viene rispettato e ciò che non entra scorre; **Studio, ricerca, widget, Start, scelta delle finestre, icone e aggiornamenti** si aprono ora come **finestre classiche di Windows**, con barra del titolo scura, pulsanti nativi e possibilità di spostarle e ridimensionarle sullo schermo.

**Novità 1.7.5:** anche con la **Dockbar dentro la barra** ora puoi mettere **widget e pulsanti azioni a sinistra**: l'ordine scelto in Studio vale per davvero, perché le zone richieste prima delle app restano a sinistra e quelle dopo restano a destra, mentre il gruppo con Start e la capsula resta centrato.

**Novità 1.7.4:** la capsula ora si chiama **Dockbar** e con **più monitor** resta sullo schermo giusto: la sua posizione è calcolata in pixel reali, quindi anche con DPI diversi non finisce più sul monitor sbagliato, e solo la barra che la ospita le riserva lo spazio accanto a Start (le altre non mostrano più un buco vuoto); quando su uno schermo c'è un contenuto in **schermo intero**, la barra di *quello* schermo passa in **nascondimento automatico** e ricompare appena porti il puntatore sul bordo, senza toccare le barre degli altri monitor.

**Novità 1.7.3:** gli aggiornamenti ora si riavviano davvero: il Setup chiude anche una copia portatile aperta da un'altra cartella prima di sostituire i file, la nuova versione prende il posto di quella vecchia invece di uscire sulla copia già aperta, e i collegamenti del menu Start restano sulla build più recente; un errore di layout della barra non blocca più l'avvio né lascia la taskbar di Windows al suo posto, e il ripristino dopo un aggiornamento fallito rimette prima la barra di Windows e poi chiude il watchdog.

**Novità 1.7.2:** i pannelli che si aprono dalla barra — **widget e controlli rapidi**, **Start**, **ricerca** e scelta delle finestre — si possono **spostare con il mouse** trascinando la loro intestazione; passando del puntatore su un'icona con **più finestre** si apre l'**anteprima di ognuna** con il titolo, e un clic porta davanti quella scelta, come nella taskbar di Windows; i **widget** ora distinguono davvero *Espanso* da *Icona* (le scorciatoie come Ricerca e Impostazioni sono sempre icone e non vengono più considerate espanse né ingrandite al passaggio) e si **riordinano trascinandoli** sia nella barra sia in **Studio → Widget**; la **Dynamic Island dentro la taskbar** compare anche scegliendo la posizione dal pannello e resta **sopra la barra** anche quando la barra è in basso, espandendosi verso l'alto senza uscire dallo schermo.

**Novità 1.7.1:** nuova scheda **Rete** nel pannello dei controlli rapidi, raggiungibile anche dai widget **Wi-Fi**, **Bluetooth** e **Rete**: interruttori per accendere le radio, elenco delle reti Wi-Fi con segnale e stato, connessione con un clic e apertura delle impostazioni di Windows per le reti protette non ancora salvate; per il Bluetooth mostra i dispositivi associati e apre le impostazioni di Windows per associazione e connessione, perché Windows non offre un'API generica per collegare ogni profilo. Ogni widget della taskbar può ora essere **espanso** (icona, valore e barra) o ridotto a **sola icona** dal pulsante nella sua riga in **Studio → Widget**; la scelta è salvata per widget e, in modalità espansa, il widget si ingrandisce del 16% al passaggio del mouse. Aggiunto anche `--network` per aprire direttamente la scheda Rete.

**Novità 1.7.0:** la barra ora ha **33 widget** invece di 13: oltre a orologio, data, CPU, RAM, dettaglio RAM, disco, uptime, batteria, rete, volume, luminosità e media, ci sono **notifiche, impostazioni rapide, panoramica, mostra desktop, cattura schermo, appunti, emoji, blocco, Wi-Fi, Bluetooth, Esplora file, Terminale, Gestione attività, cestino**, oltre a ricerca, schede, pannello widget, controlli rapidi, impostazioni e separatore. Si aggiungono da **Studio → Widget** fino a 40 e, se non entrano nella barra, si scorrono con la **rotellina del mouse** senza tagliare le icone. Ogni widget aggiorna i propri dati in modo isolato: un sensore assente (batteria, luminosità, audio) lascia gli altri attivi e mostra `—` solo nel proprio riquadro. La **sostituzione della taskbar non si spegne più da sola**: la verifica dell'AppBar aspetta che la barra sia disposta, i fallimenti transitori vengono ritentati ogni 15 secondi (fino a 8 volte) e un problema del watchdog ripristina la taskbar di Windows solo per quella sessione, senza cancellare la scelta salvata.

**Novità 1.6.14:** l'aggiornamento non viene più annullato per lo stato della taskbar: la nuova build scrive la conferma appena arriva in fondo all'avvio, e lo stato della sostituzione (`replace`, `nativeHidden`, barre visibili, AppBar) finisce in `app.log` come riga `Update health before confirmation`. Se la barra non riesce a costruirsi, Nebula ripristina la taskbar di Windows ed esce subito, senza lasciare la propria barra sopra quella nativa e senza far attendere l'updater 60 secondi davanti a una finestra di errore. Corretto anche il confronto di versione (`1.6.13` e `1.6.13.0` sono la stessa release), che poteva impedire la scrittura della conferma.

**Novità 1.6.13:** corretto il crash che impediva alla barra di partire con la **Dynamic Island dentro la taskbar**: il gruppo delle app veniva aggiunto due volte alla barra e l'avvio si interrompeva, la taskbar di Windows tornava visibile e la nuova build non confermava mai l'aggiornamento (da qui i rollback ripetuti). Ora l'isola si aggancia al centro della barra e l'aggiornamento si conferma. Aggiunto anche `NEBULA_LOG_DIR` per tenere `app.log` fuori dal profilo durante le prove.

**Novità 1.6.12:** scegliendo **Dentro la taskbar** per la Dynamic Island, Nebula attiva da sola la sostituzione della taskbar, la barra a tutta larghezza e lo spazio AppBar: la capsula non resta più flottante in alto perché mancava un prerequisito (i profili salvati vengono corretti all'avvio). La **dimensione delle icone** (Piccole 36 / Medie 42 / Grandi 48) è ora anche in **Studio → Dock → Icone della barra**, oltre che in Semplice. In `app.log` viene scritto lo stato dell'isola (`placement`, `replace`, `fullWidth`, `reserve`, `top`) ogni volta che cambia, per capire subito perché non si aggancia.

**Novità 1.6.11:** nella ricerca funzionano le **variabili d'ambiente** come percorsi: `%appdata%`, `%localappdata%\Nebula`, `%temp%`, `%userprofile%\Downloads`, con le scorciatoie `%localdata%`, `%roaming%`, `%profile%`, `%documents%`, `%desktop%` e simili. La cartella espressa compare come primo risultato (Invio la apre in Explorer) e i file e le cartelle che contiene sono cercati anche fuori dall'indice, che esclude AppData. Inoltre la conferma di avvio dopo un aggiornamento aspetta fino a **39 secondi** invece di 14, così un avvio lento non fa più tornare indietro un aggiornamento sano.

**Novità 1.6.10:** **dimensione delle icone** scegliibile in Studio tra *Piccole (36 px)*, *Medie (42 px)* e *Grandi (48 px)*, indipendente dall'altezza della barra; al passaggio del mouse l'icona **si ingrandisce davvero** (1,35×, sollevata e sopra le altre) perché la barra non la taglia più; la **Dynamic Island nella barra** ora è una finestra *owned* della barra, quindi non può più finire dietro di essa, e ogni cambio di posizione viene scritto in `app.log` per diagnosi; il **pannello Start** si apre già grande e nella posizione finale, così l'animazione parte subito invece di mostrare un salto di dimensione.

**Novità 1.6.9:** gli **aggiornamenti sono automatici** davvero: controllo 8 secondi dopo l'avvio e poi ogni 10 minuti sul canale GitHub, con popup alla nuova versione; durante il download la finestra mostra una **barra di avanzamento** con la percentuale, **quanti MB sono stati scaricati sul totale** e la **velocità** in MB/s, e gli stessi dati compaiono anche nel pannello Studio se l'installazione parte da lì.

**Novità 1.6.8:** **icone più grandi** nella barra (40 px di base, fino a 52 dalle dimensioni esatte, e l'icona riempie quasi tutto il suo riquadro), **icone riordinabili trascinandole**: premi un'icona e trascinala sopra un'altra per scambiare la posizione — l'ordine resta anche dopo un riavvio e vale pure per il pannello Start; nell'isola espansa i **titoli delle attività lunghe vanno a capo** invece di essere tagliati con i puntini.

**Novità 1.6.7:** la **Dynamic Island dentro la barra** ora si aggancia al rettangolo reale della barra invece che al bordo del monitor: se Windows lascia alla taskbar nativa la fascia inferiore (o se l'AppBar viene spostata più in alto), la capsula resta dentro la barra e non finisce più dietro o sotto la taskbar di Windows; l'isola viene inoltre impilata subito sopra la barra, non solo resa *topmost*, quindi cliccare la barra non la copre.

**Novità 1.6.6:** tutte le app hanno la loro **icona**: Nebula chiede al sistema anche le icone delle app del Microsoft Store e dei programmi preinstallati (Calcolatrice, Impostazioni, Foto, Paint, Terminale, Sicurezza di Windows…), che prima restavano vuote perché non sono file su disco; ora vengono lette anche le icone indicate con un indice nel file (`shell32.dll,-1`), usate da molti installatori. Con la **Dynamic Island dentro la barra**, Start, isola e icone formano un gruppo **centrato**, con l'isola subito **accanto al pulsante Start** e lo spazio riservato nella barra, e l'isola non sparisce più quando si clicca la taskbar. I **menu del tasto destro** sulle icone sono stati riordinati: finestre aperte in cima con l'icona dell'app, poi riduci/raggruppa/chiudi, poi le azioni sul programma, il fissaggio e per ultima la disinstallazione.

**Novità 1.6.5:** la ricerca **trova tutti i programmi**, anche quelli che non hanno una scorciatoia nel menu Start: oltre alle scorciatoie e alle app del Microsoft Store ora legge le chiavi di disinstallazione di Windows e la cartella dei programmi per utente, quindi compaiono Chrome, Edge, Git, Python, Node.js, TeamViewer, Tailscale e le app portabili; la ricerca **ignora gli accenti** ("attivita" trova "Gestione attività"), capisce le **iniziali** ("vsc" → "Visual Studio Code") e le parole **in ordine diverso** ("studio code"); il catalogo si ricostruisce da solo quando apri ricerca o pannello Start, così un programma appena installato c'è subito senza riavviare Nebula.

**Novità 1.6.4:** menu contestuali **ridisegnati** con pannello in vetro arrotondato, ombra morbida, icone a sinistra di ogni voce, separatori centrati e sottomenu funzionanti (prima le voci annidate non si aprivano); anche il **menu della tray** segue lo stesso tema scuro, con scorciatoie allineate a destra e righe regolari; i comandi di alimentazione (Blocca, Sospendi, Riavvia, Spegni, Esci) hanno icone dedicate e **si eseguono subito**, senza la finestra di conferma di Windows in mezzo; il PC si spegne direttamente dal pannello o dall'isola.

**Novità 1.6.3:** taskbar **a filo del bordo inferiore** (margine 0, spazio AppBar pari all'altezza della barra) con **icone più grandi e vicine ai lati**; nuovo pulsante Start in vetro con logo a quattro riquadri al posto del vecchio orb; **widget Media espanso** che, quando c'è una riproduzione attiva, mostra titolo, artista e stato play/pausa letto dalle sessioni multimediali di Windows; popup di aggiornamento anche per i controlli automatici e download manuali; l'aggiornamento **riavvia sempre Nebula** con un nuovo tentativo di avvio prima del rollback.

**Novità 1.6.2:** il cassetto dell'isola accetta **file e cartelle** (copia ricorsiva, dimensione e numero di file, spostamento ed eliminazione); il catalogo legge tutte le scorciatoie del menu Start anche quando una junction di sistema nega la lettura della cartella localizzata `Programmi`; l'isola dentro la taskbar resta **sopra la barra** invece di sparire dietro di essa.

**Novità 1.6.1:** scrittura **sempre fluida** nel pannello Start e nella ricerca: i tasti vengono raggruppati in un'unica ricerca quando si smette di premere (non più una ricerca per lettera), le icone si caricano in background invece di bloccare l'interfaccia, i file si cercano su un thread separato e la lista viene ricostruita solo quando i risultati cambiano davvero, mantenendo la selezione. Misurato sul campo: l'interfaccia resta impegnata ~1–7 ms per ricerca, con i file elaborati fuori dal thread UI.

**Novità 1.6:** taskbar **in basso affidabile** (la barra segue il rettangolo AppBar realmente concesso da Windows invece del bordo del monitor, quindi resta visibile e utilizzabile); **menu completo sull'icona** con *Apri come amministratore*, *Apri con…*, *Apri percorso file*, *Fissa* e *Disinstalla*, sia nella taskbar sia nel pannello Start; **Termina attività** sul tasto destro di un'icona della barra; **finiture semplici monocolore** (Vetro / Monocolore piatto / Monocolore con bordo); **barra su tutti gli schermi** con apertura delle app sullo schermo dell'icona premuta; pannelli che si aprono sullo **schermo attivo**; **Dynamic Island** che riconosce il tipo di attività (download, copia, salvataggio, compressione, estrazione, caricamento, riproduzione), distingue *in corso*, *in pausa* e *completato* e nasconde le sezioni vuote (il cassetto compare solo con file o durante un trascinamento).

**Novità 1.3:** taskbar sostitutiva **a tutta larghezza** con app a sinistra, widget al centro e controlli a destra; prenotazione reale dello spazio tramite **AppBar di Windows** (le finestre massimizzate si fermano alla barra); taskbar nativa nascosta anche sui monitor secondari e richiusa all'istante se Explorer la mostra; **widget riordinabili dentro la barra** (orologio, CPU/RAM, batteria, rete, media, ricerca, schede e altro); Studio adattivo che non esce più dallo schermo; righe impostazioni senza sovrapposizioni. Dettagli in [docs/taskbar-windows.md](docs/taskbar-windows.md).

**Novità 1.5:** nuovo preset **Liquid Glass** per widget, controlli rapidi e ricerca, nuovo stile **Liquid** per le schede e **riflesso liquido** sulla barra: una luce soffusa segue il puntatore lungo il vetro. Arrivano anche tre temi — **Prism**, **Obsidian** e **Cyber** — per un totale di 19. Dettagli in [docs/superfici.md](docs/superfici.md) e [docs/schede.md](docs/schede.md).

**Novità 1.4:** verifica **eseguibile** della sostituzione con `Nebula.exe --selftest` (AppBar reale, area di lavoro, taskbar nascosta su tutti i monitor, ordine widget dopo salvataggio/riavvio, layout a 1280×720 e 1920×1080) e ripristino d'emergenza `--restore-taskbar`; l'AppBar viene verificata sull'area di lavoro vera, non solo sulla risposta di `ABM_SETPOS`; dopo un riavvio reale di Explorer la registrazione viene rifatta automaticamente; se il watchdog manca o la verifica fallisce, Nebula **non nasconde** la taskbar nativa. Dettagli in [docs/taskbar-windows.md](docs/taskbar-windows.md) e [docs/ripristino-taskbar.md](docs/ripristino-taskbar.md).

## Funzioni

- Dock personalizzabile: in alto o in basso, margine regolabile, auto-hide, dimensione e schermo di destinazione.
- **Taskbar a tutta larghezza**: app a sinistra, widget al centro, azioni a destra; spazio riservato con l'API AppBar di Windows e ripristino watchdog.
- **19 temi completi** (Nebula, Aurora, Neon Grid, Ember, Porcelain, Mint Glass, Sakura, Graphite, Solar, Ocean, Candy, Forest, Lavender, Midnight, Sunset, Sage, Prism, Obsidian, Cyber) con accento personalizzato applicato dal vivo a dock, studio, widget e launcher.
- **6 preset superficie — Vetro / Minimal / Neon / Soft / Mono / Liquid** applicati dal vivo a widget, controlli rapidi e ricerca, indipendenti dallo stile delle schede.
- **Riflesso liquido**: una luce soffusa segue il puntatore lungo il vetro della barra; si attiva da **Studio → Stile → Dettagli**.
- **Tre zone dock indipendenti** (app, sistema, azioni): visibilità, ordine, scala, spaziatura e separatori si impostano separatamente.
- **11 widget** selezionabili dal catalogo e inseribili nel pannello: orologio, data, calendario, sistema, batteria, rete, uptime, disco, media, ricerca e controlli.
- **13 widget nella taskbar**, aggiungibili e riordinabili singolarmente: orologio, CPU, RAM, CPU+RAM, batteria, rete, media, ricerca, schede, pannello widget, controlli, impostazioni e separatore.
- **Stati icona distinti**: indicatore separato per app aperta, ridotta a icona e chiusa, con forma, dimensione, opacità e stile icona (originale, riquadro, cerchio, monocromatico, neon).
- **Icone personalizzate**: stile e forma globali per la dock, icona propria per ogni app fissata e cambio icona dei collegamenti `.lnk` del desktop tramite dialog di Windows.
- **Icone di sistema per-utente**: pagina **Studio → Icone** per applicare stili (vetro, pieno, riquadro, cerchio, duotono, mono, contorno, neon) a collegamenti `.lnk` di Desktop/Start menu/pin, tipi file sotto `HKCU\Software\Classes` e cartelle utente tramite `desktop.ini`. Icone personali `.ico/.png/.jpg/.exe`, ICO multi-size 16–256 px, backup esatto e ripristino verificato. Nessuna patch a `System32`. I tipi file la cui icona è il file stesso (`%1`, come `.exe` e `.ico`) e le sorgenti non risolvibili vengono saltati: non perdono mai la propria icona originale. Le cartelle fuori dalle sei cartelle shell del profilo restano personalizzabili singolarmente da Windows: non esiste un override globale per-utente. Dettagli in [docs/icone-sistema.md](docs/icone-sistema.md).
- **Aggiornamenti da GitHub Releases** con popup **Aggiorna e riavvia**, download ZIP, verifica SHA-256 e riavvio: controllo all'avvio e ogni 15 minuti, attivo per impostazione predefinita; dettagli e flusso di pubblicazione in [docs/aggiornamenti.md](docs/aggiornamenti.md).
- App fissate e app in esecuzione, con menu contestuale per fissare/rimuovere.
- **Schede delle finestre**: raggruppa finestre esterne in una tab bar Nebula con titoli, icone, riordino, stacco e ripristino della geometria.
- **Ricerca app e finestre** con `Ctrl+Alt+Spazio`: cerca nel menu Start e nelle finestre aperte; frecce per navigare, `Invio` per aprire o portare in primo piano, `Esc` per chiudere.
- **Widget** con `Ctrl+Alt+W` o dalla dock: orologio, data, statistiche e grafico CPU, batteria quando disponibile.
- **Controlli rapidi** dalla dock o dallo studio: volume/mute reali, play/pausa e precedente/successivo del player attivo, stato rete e apertura delle impostazioni di rete, luminosità DDC/CI quando il monitor la espone.
- Ogni superficie ha un interruttore e almeno una regola grafica in **Studio → Widget / Ricerca / Controlli**.
- Avvio con Windows realmente registrato in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- Ripristino automatico della taskbar su uscita normale, crash e terminazione forzata, con hook immediato sulle barre mostrate da Explorer.

## Installazione

Scarica `Nebula-Setup-<versione>-x64.exe` dalla Release e avvialo. Il setup:

- installa solo per l'utente corrente in `%LOCALAPPDATA%\Programs\Nebula`, senza richiedere privilegi di amministratore;
- è **self-contained**: non serve installare .NET;
- crea il collegamento nel menu Start e, opzionalmente, sul Desktop e in avvio automatico;
- registra Nebula in **Impostazioni → App → App installate**, con disinstallazione completa.

La disinstallazione chiude Nebula in modo pulito, ripristina la taskbar nativa, i gruppi di schede e le icone di sistema, quindi rimuove programma, collegamenti e voci di avvio. Le impostazioni in `%APPDATA%\Nebula` restano disponibili per una reinstallazione: elimina quella cartella per rimuovere tutto.

Chi preferisce non installare può estrarre `Nebula-win-x64.zip` e avviare `Nebula.exe`: anche lo ZIP è self-contained e contiene `Nebula.Watchdog.exe` accanto all'app.

## Requisiti

- Windows 10 1903 o successivo, oppure Windows 11.
- Nessun runtime .NET da installare: setup e ZIP includono il runtime.
- Tenere `Nebula.exe` e `Nebula.Watchdog.exe` nella stessa cartella, insieme ai rispettivi `.dll`, `.deps.json` e `.runtimeconfig.json` (vale anche per la versione portatile e per gli aggiornamenti).

## Avvio rapido

1. Avvia `Nebula-Setup-<versione>-x64.exe` e completa l'installazione.
2. Avvia Nebula dal menu Start (o lascia attiva l'opzione di avvio alla fine del setup).
3. Al primo avvio la taskbar nativa viene nascosta e compare la dock. Lo studio si apre dall'icona impostazioni della dock o dal menu della tray.
4. Per uscire in modo pulito: tray → **Esci da Nebula**, oppure **Info → Esci da Nebula** nello studio.

La taskbar originale viene ripristinata **visibile** all'uscita normale; un eventuale auto-hide precedente viene disattivato per non lasciare una barra difficile da recuperare.

Verifica automatica (usa impostazioni temporanee e ripristina sempre la taskbar):

```powershell
.\dist\Nebula.exe --selftest
$latest = Get-ChildItem .\artifacts\selftest | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content (Join-Path $latest.FullName 'selftest-report.txt')
```

Ripristino d'emergenza, anche se Nebula è bloccata:

```text
scripts\ripristina-taskbar.cmd
```

Ripristino delle icone di sistema dal backup, senza aprire lo Studio:

```powershell
.\dist\Nebula.exe --restore-icons
```

Se il backup non esiste più, il comando rimuove solo i riferimenti alle icone generate da Nebula, senza toccare le altre icone.

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

In **Studio → Stile → PRESET SUPERFICI** si scelgono **Vetro**, **Minimal**, **Neon**, **Soft**, **Mono** o **Liquid**. Il preset viene applicato dal vivo alle superfici già aperte, non solo alla riapertura:

- **Vetro:** card e controlli arrotondati, bordo vetro, slider da 4 px con pomello da 17 px, tipografia variable.
- **Minimal:** raggi più contenuti, bordo piatto, slider da 3 px con pomello da 13 px, testo Segoe UI.
- **Neon:** raggi intermedi, bordo con gradiente d'accento, slider da 5 px con pomello da 18 px, titoli in grassetto.
- **Soft:** raggi ampi, superfici più piene, ombre attenuate e tipografia morbida.
- **Mono:** raggi ridotti, contrasto alto, niente gradiente d'accento e controlli lineari.
- **Liquid:** lavaggio vetro-brillante con bordo ad accento, raggi ampi, slider da 5 px con pomello da 18 px e riflesso luminoso.

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

Una sola istanza possiede la taskbar nativa, l'AppBar e il watchdog. Un secondo avvio normale, o un `--selftest` lanciato mentre Nebula è già attiva, cede all'istanza esistente e termina con codice 0 senza creare una seconda proprietaria; l'area di lavoro e la taskbar restano quelle dell'istanza originale.

- **Uscita normale:** Nebula mostra la taskbar nativa (senza auto-hide), chiude dock, tooltip e pannelli, quindi rimuove lo stato pendente. Il watchdog non deve fare nulla.
- **Crash o `taskkill /F`:** il watchdog ripristina la taskbar visibile, senza rilanciare Nebula. La dock e i pannelli spariscono insieme al processo.
- **Auto-hide originale:** sia dopo un crash sia dopo un'uscita normale la taskbar viene mostrata visibile; l'auto-hide precedente viene disattivato per non lasciare una barra difficile da recuperare.
- **Watchdog assente o non avviabile:** il preflight ferma la sostituzione; la taskbar nativa resta visibile e la dock non viene mostrata.
- **AppBar non verificata:** se l'area di lavoro non viene riservata, Nebula ripristina la taskbar nativa invece di lasciare una barra coperta dalle finestre massimizzate.

Dettagli, test e diagnostica: [docs/ripristino-taskbar.md](docs/ripristino-taskbar.md).

## Taskbar Windows e widget

La barra viene registrata come **AppBar** di Windows: lo spazio è riservato dal sistema e le finestre massimizzate non la coprono. Con **Barra a tutta larghezza** le zone diventano sinistra/centro/destra come nella taskbar di Windows 11. I widget della barra si scelgono e si riordinano da **Studio → Widget → Widget nella taskbar**. Architettura, confronto con Windhawk/Windows 11 Taskbar Styler, diagnostica e limiti: [docs/taskbar-windows.md](docs/taskbar-windows.md).

L'audit riproducibile di binding, handler e controlli duplicati è in [docs/audit-controlli.md](docs/audit-controlli.md); il comando `scripts/audit-ui-controls.ps1` rigenera il report.

## Schede delle finestre

Dalla dock usa l'icona **schede** o il menu contestuale **Raggruppa finestre in schede…**; dallo studio: **Schede → Scegli finestre…**. Seleziona almeno due finestre e premi **Crea gruppo**. La tab bar compare ancorata alla finestra attiva, sopra o sotto in base alle impostazioni.

- Clic su una scheda: la finestra va in primo piano, le altre del gruppo vengono nascoste.
- Trascina una scheda per riordinarla.
- × sulla scheda chiude la finestra; menu destro → **Stacca scheda** la rimuove dal gruppo e le restituisce la geometria originale.
- × a destra della barra chiude il gruppo e ripristina tutte le finestre.

Nello studio, pagina **Schede**: attiva/disattiva, stile **Vetro / Minimal / Neon / Soft / Mono / Liquid**, altezza 34–64 px, posizione sopra/sotto, animazioni collegate a **Animazioni fluide**. Tutto viene applicato dal vivo e salvato.

Le finestre protette, elevate o UWP vengono rifiutate con un messaggio esplicito. I dettagli, le misure e i limiti sono in [docs/schede.md](docs/schede.md).

## Prestazioni

Misure finali della build 1.1 pubblicata in `dist/`, un monitor 1920×1080, preset superficie **Vetro**, animazioni attive, nessun gruppo di schede. La dock è stata misurata da un processo pulito dopo warm-up; la CPU è la media del tempo processo su una finestra di 20 s.

**Build 1.4 (taskbar a tutta larghezza, 5 widget, animazioni attive, 1920×1080):** CPU **1,69% di un core** su 12 s, memoria privata **87,5 MB**, working set **169,9 MB**. La work area era riservata a 1008 px e la taskbar nativa era nascosta; dopo il kill la work area è tornata a 1032 px e la taskbar nativa è risultata visibile.

| Scenario | CPU (un core) | Memoria privata |
| --- | --- | --- |
| Dock sola, dopo warm-up 20 s | **1,17%** su 20 s | **98,1 MB** (98,4 MB altri 20 s dopo) |
| Dock + pannello Widget, subito | **0,47%** su 20 s | **128,6 MB** a 2 s dall'apertura; **130,0 MB** dopo 20 s |
| Dopo chiusura del pannello | — | **129,6 MB** a 5 s |
| Watchdog | ~0% | ~6,5 MB |

La dock sola resta **sotto il target di 116 MB**. Il pannello aperto no: il pavimento misurato è **128,6–130,0 MB**, quindi ancora sopra il bersaglio. Il costo è la seconda finestra WPF layered (`AllowsTransparency`) con il calendario, i grafici e la striscia dei widget; non è heap gestito incontrollato. Alla chiusura vengono fermati timer, grafico e calendario, svuotata anche la striscia widget e rilasciato l'endpoint COM; la memoria scende a **129,6 MB**, perché WPF/DWM trattengono cache native della finestra. Per scendere sotto il target con il pannello aperto servirebbe unire il pannello alla dock o rimuovere la superficie layered.

**Misura idle finale del 18 novembre 2026 (1.5.1 installata con il flusso reale di aggiornamento e la soppressione dei pacchetti guasti, tema Aurora, accento #123456, altezza dock 78, spaziatura 7, widget orologio/batteria, isola disattivata, aggiornamenti automatici attivi, 1920×1080, 18 s di warm-up, un solo monitor):** 60,5 s con **0,2839% di un core** (0,1718 s di CPU), working set medio **182,74 MB** e memoria privata media **93,16 MB**. La misura precedente dello stesso profilo era **0,2574%**, **175,77 MB** e **88,96 MB**; la baseline dichiarata era **0,7791%**, **186,40 MB** e **97,74 MB**; la 1.5.0 precedente alle ottimizzazioni era **1,1167%**, **197,05 MB** e **108,93 MB**. La misura finale resta sotto il limite di **0,7791%** e **186,40 MB**. Sono stati rimossi il timer della dock a 150 ms (ora 500 ms), il controllo della taskbar a 500 ms (ora 1 s) e il polling delle finestre ogni 1,4 s (ora eventi WinEvent con riconciliazione ogni 5 s).

La tab bar con tre finestre raggruppate era stata misurata in una build precedente a **147,6 MB** e **0,78%** di CPU, con apertura in **16–29 ms**; non è stata rimisurata in questa revisione, ma non è stata modificata.

## Monitor e DPI

Lo studio permette di scegliere lo schermo della dock. La dock usa l'area di lavoro e il DPI dello schermo scelto, si riposiziona al cambio DPI e rileva il fullscreen sui limiti di quello schermo. Anche widget, controlli e ricerca vengono posizionati sullo schermo selezionato.

Su questa macchina è presente **un solo monitor fisico** (`\\.\DISPLAY5`, 1920×1080, primario): la selezione è stata verificata a schermo singolo, mentre il comportamento su un secondo monitor e su DPI misti non è stato osservato fisicamente.

## Build e pubblicazione

Per creare publish self-contained e setup in un solo passaggio serve **Inno Setup 6** (il compilatore `ISCC.exe`):

```powershell
.\scripts\package.ps1
```

Lo script pubblica `src/Nebula/Nebula.csproj` con `-r win-x64 --self-contained true`, verifica che il watchdog sia self-contained e crea `artifacts\setup\Nebula-Setup-<versione>-x64.exe`. La build manuale equivalente:

```powershell
.\.dotnet\dotnet.exe build .\src\Nebula\Nebula.csproj -c Release
.\.dotnet\dotnet.exe publish .\src\Nebula\Nebula.csproj -c Release -r win-x64 --self-contained true -o .\dist
```

Il target MSBuild compila il watchdog con lo stesso RID e la stessa modalità self-contained dell'app, poi lo copia nel publish. Se il watchdog non può partire, Nebula non nasconde la taskbar nativa: il controllo è anche nel report `selftest`.

Prima di ripubblicare, chiudi Nebula: altrimenti l'eseguibile in uso resta bloccato.

## Release e aggiornamenti con GitHub

Il repository include `.github/workflows/release.yml`: al push di un tag `vMAJOR.MINOR.PATCH` GitHub Actions compila la build self-contained, crea `Nebula-Setup-<versione>-x64.exe`, `Nebula-win-x64.zip` e `update.json`, calcola gli SHA-256 e pubblica la Release. Il setup è il percorso consigliato; lo ZIP serve alla versione portatile e agli aggiornamenti automatici, che mostrano il popup **Aggiorna e riavvia**.

L'aggiornamento in-app è **transazionale**. Prima dello swap l'app scrive `update-state.json`, `recover-update.ps1` e una voce `RunOnce`; la vecchia cartella resta in `.old` e la nuova build viene avviata. La nuova build scrive `update-confirmed.txt` solo dopo il Boot e il controllo di taskbar/AppBar; lo script attende fino a **60 secondi**. Se la conferma non arriva, ripristina `.old`, avvia la versione precedente e mostra il motivo. Se il PC si spegne o il processo viene ucciso, il `RunOnce` completa il rollback al logon successivo e rimuove stato e cartelle temporanee, senza loop. Un rollback scrive anche `failed-update.json` con versione e SHA-256: il controllo successivo non ripropone quello stesso pacchetto, mentre un correttivo con lo stesso numero di versione ma SHA diverso resta installabile; un aggiornamento confermato elimina il file.

Per pubblicare una versione dopo aver committato il codice:

```powershell
.\scripts\release.cmd -Version 1.5.0
```

Lo script aggiorna `<Version>` in `src/Nebula/Nebula.csproj`, crea commit e tag, e li invia a GitHub. Il repository deve essere **pubblico**, perché l'aggiornatore scarica gli asset senza credenziali. Il primo setup del repository e i dettagli del workflow sono in [docs/aggiornamenti.md](docs/aggiornamenti.md).

## Verifiche di questa release

> Le verifiche UI seguenti si riferiscono alla release 1.2. La revisione 1.3 è stata verificata con build Release e publish; la revisione 1.4 ha aggiunto il selftest AppBar, il ripristino d'emergenza, l'audit dei controlli e il layout misurato a 1280×720 e 1920×1080. La revisione 1.5 aggiunge i preset Liquid, il riflesso sulla barra e tre temi, verificati con build Release e selftest completo. Il 18 novembre 2026 è stato inoltre eseguito il flusso di aggiornamento in-app su un'installazione reale 1.5.0, con una build di prova 1.5.1 servita via loopback: i risultati sono nella sezione seguente. Il giro UI completo su taskbar sostitutiva, AppBar e widget è descritto qui sotto ed è stato rieseguito sulla macchina di destinazione.

### Installazione, aggiornamento e disinstallazione (18 novembre 2026)

- Setup self-contained `Nebula-Setup-1.5.0-x64.exe` da **47,4 MB**: installazione per-utente senza UAC in `%LOCALAPPDATA%\Programs\Nebula`, **265 file** installati, collegamento nel menu Start, voce in **App installate**, chiave `App Paths` e avvio automatico opzionale.
- Verifica del watchdog nel publish: `Nebula.Watchdog.runtimeconfig.json` contiene `includedFrameworks`, quindi il ripristino della taskbar non dipende dal .NET installato sul PC.
- Installazione sopra un'istanza in esecuzione: il setup ha chiesto a Nebula di uscire con `--exit` (circa 1 s), zero processi residui, installazione completata con exit code 0.
- **Rollback reale di una 1.5.1 guasta:** installazione 1.5.0 aggiornata tramite manifest HTTP loopback e ZIP reale con `Nebula.exe` deliberatamente a **0 byte**. La nuova build non ha superato l'avvio, lo script ha ripristinato `.old` entro la finestra di verifica e ha riaperto la 1.5.0: **265 file, 0 mancanti**, `unins000.exe/.dat` presenti, nessuna cartella `.old/.new/.failed`, nessun `update-state.json` e nessuna voce `RunOnce`. Restano `theme=aurora`, `AccentHex=#123456`, `DockHeight=78`, `ItemSpacing=7`, widget `clock,battery`, `LaunchAtStartup=True` e voce `Run` con `--startup`; taskbar riservata a **990 px** e app viva.
- **Pacchetto guasto soppresso, correzione con lo stesso numero:** dopo il rollback il file `failed-update.json` conteneva `Version=1.5.1` e lo SHA-256 del pacchetto guasto; al controllo automatico successivo, nella nuova istanza 1.5.0 avviata dal rollback, il log riportava `Update package suppressed after a failed start: 1.5.1` e **non compariva alcun popup**. Sostituito il manifest con una 1.5.1 valida ma con SHA diverso, il popup è ricomparso; l'aggiornamento ha scritto `Update startup health confirmed: 1.5.1`, è rimasto installato e ha rimosso `failed-update.json`. Impostazioni, autostart e voce `Run` sono rimasti intatti; taskbar riservata a **990 px**.
- **Aggiornamento sano della stessa 1.5.1:** eseguito dalla 1.5.0 appena ripristinata; la nuova build ha scritto `Update startup health confirmed: 1.5.1` e `Update health confirmation written for 1.5.1`, è rimasta installata e viva oltre la finestra di 60 s, con **265 file**, `unins000.exe/.dat` preservati e nessun rollback falso. Lo stesso aggiornamento è stato ripetuto dopo il test di stato pendente.
- **Stato pendente da esecuzione interrotta:** creato uno stato reale con 1.5.1 in posizione, `.old` 1.5.0 e nessuna conferma; la voce `RunOnce` che l'app registra è stata eseguita. Il recovery ha ripristinato la 1.5.0, rimosso stato, `.old` e `RunOnce`, riavviato l'app e lasciato la taskbar a **990 px**. Una seconda esecuzione del recovery senza stato è uscita 0 senza modificare nulla: nessun loop. Se il kill avviene **prima dello swap** (`.old` assente), il recovery lascia l'installazione viva e rimuove solo stato e `RunOnce`; provato con uno stato pendente senza `.old`, la 1.5.1 è rimasta intatta e non è comparso `.failed`.
- **Self-test finale:** eseguito da processo pulito con `RESULT: PASS`; taskbar nativa visibile, area di lavoro tornata a **1032 px**, AppBar rilasciata.
- **Seconda istanza:** con la 1.5.1 attiva, un secondo avvio normale e un secondo `--selftest` sono usciti con codice 0 lasciando **un solo processo Nebula**, lo stesso PID e la stessa area di lavoro riservata (990 px). `--selftest` usa lo stesso mutex dell'avvio normale e non tocca la taskbar se un'altra istanza la possiede.
- **Fallimento SHA-256 errato:** Nebula ha mostrato **Hash del pacchetto non valido: aggiornamento annullato**, ha mantenuto la 1.5.0 avviabile e non ha lasciato ZIP o staging.
- **Fallimento con file bloccato:** con `createdump.exe` aperto in modo esclusivo, lo swap non è partito. La 1.5.0 è rimasta intatta (265 file, 0 mancanti), la finestra non modale **Nebula · Aggiornamento non riuscito** ha spiegato l'errore e non sono rimasti `.new/.old`, staging, ZIP o marker. `Nebula.exe --exit` ha chiuso anche quella finestra con exit code 0 e zero processi.
- **Disinstallazione 1.5.1:** exit code 0, zero processi residui, cartella, collegamenti Desktop/Start, voce `App Paths`, voce `Run` e voce **App installate** rimossi; taskbar visibile e `taskbar.pending` assente.
- `--restore-icons`: exit 0 in 1 s senza finestre modali, condizione necessaria per il disinstallatore silenzioso.
- Build Release e `-r win-x64 --self-contained true` con **0 avvisi e 0 errori**.

Eseguite sulla build pubblicata in `dist/`:

- Widget, controlli rapidi e ricerca aperti dalla dock e dallo studio; dock e studio non duplicano finestre o processi.
- Preset **Vetro → Soft → Mono → Vetro** commutati dal vivo; i 16 temi sono presenti nella pagina Stile e il preset viene salvato. La ricerca resta aperta con l'ingranaggio e si aggiorna dal vivo.
- Catalogo widget: **Uptime** e **Disco** attivati dal catalogo, comparsi subito nella striscia del pannello e salvati in `settings.json`; disattivandoli spariscono senza chiudere il pannello.
- Zone dock: ordine **App · Sistema · Azioni → Azioni · App · Sistema** applicato dal vivo alla dock, con valore persistito e poi ripristinato.
- Icone per-app: **File Explorer** impostato su **Neon** dal selettore nella scheda ICONE; il valore è stato salvato e applicato alla dock senza riavvio.
- Icone di sistema: applicato **Neon** e **Cerchio** a un vero `.lnk` del Desktop e verificato il cambio su Explorer; applicato **Neon** a `.zip` tramite il ProgID `CompressedFolder` e visto il cambio da icona ZIP a piastra; applicato **Pieno** a `.jpg`; caricato un `.png` personale dal dialog di Windows e applicato sia a un `.lnk` sia alla cartella **Desktop**, con anteprima Explorer visibile, poi ripristinato. L'ICO generato contiene **10 frame da 16 a 256 px** (256 in PNG).
- Ripristino icone di sistema: `.lnk` tornato al valore precedente, `.zip` tornato senza chiave `CompressedFolder\DefaultIcon`, `.jpg` tornato senza chiave `jpegfile\DefaultIcon`, `desktop.ini` di Desktop/Download tornato byte per byte (incluso `LocalizedResourceName` e `IconResource` originali), backup rimosso e cartella `icons` vuota. Applicate e ripristinate **156 voci** con **Applica a tutte** (29 collegamenti, 121 superfici di registro, 6 cartelle utente).
- Journal icone: ripristino interrotto e app riavviata; il journal incompleto ha riportato le voci originali. Uscita normale e `taskkill /F` senza processi residui né file `.tmp`.
- Aggiornatore automatico self-healing: il flusso reale è stato rieseguito end-to-end il 18 novembre 2026 su un'installazione 1.5.0, con rollback di una build guasta, soppressione persistente di quel pacchetto, conferma di una build sana e recovery di uno stato pendente (vedi sopra). Il manifest e gli ZIP erano serviti su HTTP loopback; il canale GitHub pubblicato non è stato usato perché non è stata creata una Release.
- Animazioni disattivate sulle superfici: nessuna transizione di ingresso residua; riattivate, le transizioni tornano.
- Misure storiche della build 1.1 da processo pulito con warm-up e campioni a 20 s: dock **98,1–98,4 MB** e **1,17%**; pannello Widget **128,6–130,0 MB** e **0,47%**; chiusura **129,6 MB**; la dock è sotto il target di 116 MB, il pannello aperto no. La misura idle corrente della 1.5.1 è nella sezione **Prestazioni**.
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
- **Aggiornatore:** il flusso automatico è stato provato end-to-end con un manifest e un pacchetto locali su loopback; il canale GitHub `latest` non è stato usato perché non è stata creata una Release. Fuori dal loopback il codice richiede HTTPS. Lo SHA-256 verifica l'integrità, non autentica l'autore, e lo ZIP non è firmato. Se la nuova build si avvia ma poi va in crash non c'è un rollback automatico: il rollback copre gli errori prima e durante lo swap. Il log è in `%LOCALAPPDATA%\Nebula\updates\apply-update.log` se la copia fallisce.
- **Avvio e self-test:** una sola istanza possiede taskbar, AppBar e watchdog. Un secondo avvio normale cede e termina con codice 0; se `--selftest` trova un'istanza già proprietaria, cede nello stesso modo senza eseguire la diagnosi e senza toccare la taskbar. Da un avvio pulito il self-test esegue tutti i controlli e passa. La build di prova 1.5.1 non è stata pubblicata e la versione sorgente resta 1.5.0.
- **Icone personalizzate:** il selettore per-app e la persistenza dello stile sono verificati; il dialog di scelta file `.ico/.png/.jpg` e la riscrittura di un collegamento `.lnk` reale non sono stati automatizzati in questa sessione.
- Il secondo monitor fisico e i DPI misti non sono disponibili su questa macchina.
- Il test fullscreen è una finestra WinForms borderless, non un gioco esclusivo o un'app UWP protetta.
- Le finestre UWP protette o elevate non sono state osservate: il rifiuto è implementato nel selettore schede, ma il caso non è stato riprodotto.
- **Firma del setup:** il pacchetto non è firmato con un certificato di code signing, quindi SmartScreen può mostrare un avviso al primo avvio.

## Documentazione

- [Widget, controlli rapidi e ricerca](docs/superfici.md)
- [Aggiornamenti](docs/aggiornamenti.md)
- [Avvio e uso](docs/avvio.md)
- [Schede delle finestre](docs/schede.md)
- [Ripristino della taskbar](docs/ripristino-taskbar.md)
- [Taskbar Windows, AppBar e widget](docs/taskbar-windows.md)
