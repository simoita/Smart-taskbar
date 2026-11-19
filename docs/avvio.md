# Avvio e uso

## Installazione

1. Scarica `Nebula-Setup-<versione>-x64.exe` dalla Release e avvialo.
2. Scegli le opzioni desiderate. Il setup crea il collegamento nel menu Start, un collegamento sul Desktop (opzionale) e l'avvio con Windows (opzionale).
3. Al termine puoi avviare Nebula direttamente dalla pagina finale.

Il setup installa solo per l'utente corrente in `%LOCALAPPDATA%\Programs\Nebula` e non richiede privilegi di amministratore: l'app usa solo risorse del profilo e la chiave di avvio in `HKCU`. La build è **self-contained**, quindi non serve installare .NET.

La disinstallazione si avvia da **Impostazioni → App → App installate → Nebula Taskbar Studio**. Prima di rimuovere i file, il disinstallatore chiede a Nebula di uscire in modo pulito, ripristina la taskbar di Windows, i gruppi di schede e le icone di sistema, e cancella collegamenti e voci di avvio. Le impostazioni e i backup restano in `%APPDATA%\Nebula`: elimina quella cartella per rimuovere anche quelli.

In alternativa, la versione portatile si ottiene estraendo `Nebula-win-x64.zip`. Non separare `Nebula.exe` da `Nebula.Watchdog.exe`: il watchdog è il meccanismo che rimette la taskbar se Nebula viene terminata forzatamente. Anche la versione portatile è self-contained.

## Primo avvio

Avvia `Nebula.exe`. Se l'impostazione **Sostituisci la taskbar di Windows** è attiva:

- la taskbar nativa viene nascosta;
- compare la dock in basso al centro;
- viene avviato `Nebula.Watchdog.exe` accanto all'app.

Per aprire lo studio senza passare dalla dock:

```powershell
.\dist\Nebula.exe --settings
```

Se Nebula è già in esecuzione, il comando riapre la finestra dello studio dell'istanza esistente. Una sola istanza possiede la taskbar, l'AppBar e il watchdog; un secondo avvio cede all'istanza attiva e termina con codice 0. Lo stesso vale per `--selftest`, che non tocca la taskbar se un'altra istanza la possiede.

## Comandi e scorciatoie

| Azione | Comando |
| --- | --- |
| Ricerca app e finestre | `Ctrl+Alt+Spazio` |
| Pannello widget | `Ctrl+Alt+W` |
| Controlli rapidi | icona **Controlli rapidi** nella dock, oppure **Controlli → Apri controlli rapidi** nello studio |
| Studio | icona impostazioni nella dock o nella tray |
| Schede | icona schede nella dock, menu destro sulla dock, oppure **Schede** nello studio |
| Uscita pulita | tray → **Esci da Nebula**, oppure **Info → Esci da Nebula** |

Nella dock, il tasto destro su un'app apre il menu per fissarla o rimuoverla. Le app fissate restano dopo il riavvio. Il tasto destro su un punto vuoto apre anche **Raggruppa finestre in schede…**.

## Widget e controlli rapidi

Apri il widget con l'icona **Widget** nella dock, con `Ctrl+Alt+W`, oppure da **Studio → Widget → Apri pannello widget**. Il pannello mostra la striscia dei widget scelti dal catalogo, oltre a orologio, data, statistiche CPU/RAM, batteria se presente e un grafico CPU. Ogni elemento ha un interruttore nella pagina **Widget**.

I **Controlli rapidi** si aprono dall'icona dedicata nella dock o da **Studio → Controlli → Apri controlli rapidi**. Volume e mute agiscono sull'endpoint audio reale; i pulsanti media inviano play/pausa, precedente e successivo al player attivo; la rete mostra lo stato e apre le impostazioni di Windows; la luminosità usa DDC/CI quando il monitor la espone, altrimenti lo slider è disabilitato con un avviso. **Layout compatto controlli** riduce il pannello da 410 a 370 px.

Widget e controlli condividono la stessa finestra: non vengono creati processi o finestre WPF aggiuntive.

In **Studio → Stile → PRESET SUPERFICI** puoi passare dal vivo tra **Vetro**, **Minimal**, **Neon**, **Soft**, **Mono** e **Liquid**. Il preset vale per widget, controlli rapidi e ricerca, anche quando sono già aperti; le schede mantengono il proprio stile. Ogni preset cambia card, raggi, bordi, padding, slider, tipografia e peso dei titoli. L'interruttore **Animazioni fluide** controlla anche le transizioni di ingresso di queste superfici.

## Zone della dock

In **Studio → Dock → ZONE DELLA DOCK** la dock è divisa in tre spazi indipendenti: **icone/app**, **sistema** (orologio, batteria, statistiche) e **azioni/ricerca**. Per ciascuno puoi decidere se mostrarlo, l'ordine complessivo, la scala, la distanza e i separatori. La scala ridimensiona solo quella zona, senza toccare le altre.

## Icone

In **Studio → Stile → ICONE** scegli lo stile (originale, riquadro, cerchio, monocromatico, neon) e la forma (arrotondata, squircle, cerchio, quadrata). Nella stessa scheda puoi assegnare un'immagine `.ico`, `.png`, `.jpg` o un `.exe` a ogni app fissata; l'immagine viene ritagliata nella forma scelta. I collegamenti `.lnk` del desktop compaiono sotto **COLLEGAMENTI DEL DESKTOP** e possono ricevere un'icona `.ico` o `.exe` tramite il dialog di Windows.

Nebula non modifica gli eseguibili di sistema né le icone globali di Windows da questa scheda: il cambio icona riguarda la dock e i collegamenti scelti. Le icone delle app non fissate usano l'icona originale del processo.

### Icone di tutto il profilo

La pagina separata **Studio → Icone** estende il cambio icona ai collegamenti `.lnk` di Desktop/Start menu/pin, ai tipi file del profilo e alle sei cartelle shell del profilo (Desktop, Documenti, Download, Immagini, Musica, Video). Usa solo superfici per-utente (`IconLocation`, `HKCU\Software\Classes` e `desktop.ini`), con backup automatico e comando **Ripristina originali**. Non tocca `System32` né `HKLM`. Le altre cartelle del disco restano personalizzabili una per una dalle Proprietà di Windows.

- Scegli **stile** (originale, vetro, pieno, riquadro, cerchio, duotono, mono, contorno, neon) e **forma**.
- **Scegli icona** apre il dialog di Windows e accetta `.ico`, `.png`, `.jpg` ed `.exe` per una singola voce; l'ICO generato contiene le misure 16–256 px.
- **Applica a tutte** modifica tutti i collegamenti, le associazioni e le sei cartelle utente rilevate; **Ripristina originali** rimette i valori esatti e ripristina il `desktop.ini` originale byte per byte.

Prima di usare il pulsante globale, tieni presente che le modifiche sono persistenti alla chiusura dell'app: il backup serve al ripristino, non a un rollback automatico. Procedura e limiti: [icone-sistema.md](icone-sistema.md).

## Ricerca

`Ctrl+Alt+Spazio`, l'icona **Cerca** nella dock o **Studio → Ricerca → Apri ricerca** aprono il launcher. Cerca le app del menu Start e le finestre aperte; le frecce scorrono i risultati, `Invio` apre l'app o porta in primo piano la finestra, `Esc` chiude.

L'ingranaggio nella ricerca apre lo Studio **senza chiudere la ricerca**: puoi cambiare preset e tema e vedere l'aggiornamento sulla ricerca già aperta; chiudendo lo Studio la ricerca torna davanti.

In **Studio → Ricerca** puoi disattivare la superficie (**Abilita ricerca**), escludere le finestre (**Cerca finestre aperte**) e impostare la larghezza del pannello tra 560 e 900 px.

## Schede

1. Clicca l'icona schede nella dock (due rettangoli) o fai clic destro sulla dock → **Raggruppa finestre in schede…**.
2. Seleziona almeno due finestre; le finestre protette/elevate/UWP non compaiono nel selettore.
3. Premi **Crea gruppo**. La tab bar compare sopra o sotto la finestra attiva.

Clic sulla scheda per selezionare, trascina per riordinare, × per chiudere la finestra, menu destro per **Stacca scheda** o **Chiudi gruppo**. La × a destra chiude il gruppo e ripristina tutte le finestre nella posizione originale.

Stili, altezza, posizione e animazioni si impostano in **Studio → Schede**. Dettagli: [schede.md](schede.md).

## Sostituzione della taskbar

Nello studio, pagina **Dock → Integrazione con Windows**:

- **Sostituisci la taskbar di Windows**: attiva/disattiva la dock. Disattivandola, la taskbar nativa torna visibile e la dock viene nascosta.
- **Ripristina taskbar**: voce per tornare subito alla taskbar di Windows senza chiudere Nebula.
- **Schermo della dock**: sceglie il monitor su cui centrare la dock, rispettandone area di lavoro e DPI.

Quando la taskbar nativa viene ripristinata, Nebula chiude anche launcher, widget, controlli rapidi e tooltip aperti.

## Avvio con Windows

Nello studio, pagina **Comportamento**, attiva **Avvia con Windows**.

L'operazione scrive davvero:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
Valore: Nebula
Dati:   "C:\percorso\Nebula.exe" --startup
```

Disattivandola, il valore viene rimosso. Se sposti la cartella di Nebula, disattiva e riattiva l'opzione per registrare il nuovo percorso.

## Uscita

Chiudi sempre con **Esci da Nebula** quando puoi. L'uscita normale:

1. rende visibile la taskbar nativa (disattivando l'auto-hide precedente);
2. ripristina tutte le finestre dei gruppi di schede;
3. chiude dock, tab bar, tooltip e pannelli;
4. rimuove `%APPDATA%\Nebula\taskbar.pending` e `tabs.pending`;
5. termina il watchdog.

Se Nebula viene terminata forzatamente, la taskbar e le finestre raggruppate vengono comunque ripristinate dal watchdog: vedi [ripristino-taskbar.md](ripristino-taskbar.md).

## Aggiornamento

In **Studio → Info → AGGIORNAMENTI** incolla l'URL del manifest e lascia attivo **Aggiornamenti automatici**: Nebula controlla all'avvio e ogni 15 minuti, scarica il pacchetto, ne verifica lo SHA-256, si chiude e riparte con la nuova versione. **Verifica ora** forza un controllo; **Scarica e installa** applica manualmente la versione trovata. Il manifest e il formato dello ZIP sono descritti in [aggiornamenti.md](aggiornamenti.md).

In alternativa puoi aggiornare manualmente: esci da Nebula, sostituisci i file in `dist/` con la nuova build, assicurati che `Nebula.Watchdog.exe` sia accanto a `Nebula.exe` e riavvia. Se l'avvio automatico era attivo e la cartella non è cambiata, non serve toccare il registro.
