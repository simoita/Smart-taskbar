# Taskbar Windows: sostituzione, AppBar e widget

Nebula non decora la taskbar di Explorer: la **sostituisce** con una barra WPF
propria e registrata nel sistema operativo. Questa pagina spiega cosa viene
fatto davvero, cosa resta nativo e quali sono i limiti.

## Cosa fa Nebula quando sostituisce la taskbar

1. Salva lo stato originale della taskbar in `%APPDATA%\Nebula\taskbar.pending`.
2. Avvia `Nebula.Watchdog.exe` per il ripristino dopo crash o `taskkill /F`.
3. Imposta la taskbar nativa in auto-hide e nasconde `Shell_TrayWnd` e ogni
   `Shell_SecondaryTrayWnd`, quindi anche i monitor secondari.
4. Installa un hook `EVENT_OBJECT_SHOW`: se Windows mostra di nuovo la barra
   (hover sul bordo o riavvio di Explorer), viene nascosta subito.
5. Registra la barra Nebula con l'API **AppBar** (`SHAppBarMessage` con
   `ABM_NEW`, `ABM_QUERYPOS` e `ABM_SETPOS`). La striscia riservata viene
   rimossa dall'area di lavoro di Windows, quindi le finestre massimizzate si
   fermano esattamente al bordo della barra. Dopo `ABM_SETPOS` Nebula legge
   `SPI_GETWORKAREA` e considera riuscita la registrazione solo se l'area di
   lavoro è davvero cambiata; altrimenti forza `ABM_REMOVE` + `ABM_NEW`.
6. Mantiene un guard timer da 500 ms come ulteriore rete di sicurezza.

L'AppBar è lo stesso meccanismo che usa la taskbar di sistema. Non è un
trucco grafico: è Windows che riserva lo spazio e ridimensiona le finestre.

Se il preflight non trova `Shell_TrayWnd`, il watchdog accanto all'eseguibile
o una directory di stato scrivibile, Nebula **non nasconde** la taskbar. Se
l'AppBar non riserva davvero l'area di lavoro, la dock viene nascosta e la
taskbar nativa viene ripristinata.

## Verifica automatica (selftest)

```powershell
.\dist\Nebula.exe --selftest
```

Il selftest usa una directory impostazioni temporanea, non modifica il profilo
utente e ripristina la taskbar in `finally`. Usa lo stesso mutex dell'avvio
normale: se Nebula è già attiva, il nuovo processo cede con codice 0 senza
toccare taskbar, AppBar o watchdog. Da un avvio pulito produce un report e
screenshot in `artifacts/selftest/<timestamp>/` e controlla, con valori reali:

- `Shell_TrayWnd`/`Shell_SecondaryTrayWnd` nascoste;
- `ABM_NEW`/`ABM_SETPOS` e area di lavoro riservata (misurata con
  `SystemParametersInfo`);
- barra a tutta larghezza e margine laterale esatto;
- una finestra WinForms massimizzata ferma il **client** esattamente sopra la
  barra;
- i widget nell'ordine salvato, poi riordino, salvataggio e rilettura;
- dieci pagine Studio a 1280×720 e 1920×1080, con controllo di clipping,
  sovrapposizioni e raggiungibilità del fondo;
- chiusura con taskbar nativa visibile e work area ripristinata.

Il watchdog del selftest ha anche un timeout massimo: se l'harness si blocca,
termina il processo e ripristina la shell. `scripts/ripristina-taskbar.cmd`
resta la via manuale garantita.

## Layout a tutta larghezza

Con **Dock → Integrazione con Windows → Barra a tutta larghezza** attivo, la
barra occupa la larghezza del monitor selezionato e distribuisce le tre zone:

```text
[ app e avvio ]                 [ widget ]                 [ azioni ]
```

L'ordine delle zone resta configurabile da **Zone della dock**. Se una zona
viene nascosta, le altre si ricompongono senza lasciare buchi. Senza la barra a
tutta larghezza la dock torna una pillola centrata, ma può comunque riservare lo
spazio con l'AppBar.

## Widget nella taskbar

**Studio → Widget → Widget nella taskbar** permette di aggiungere, rimuovere e
riordinare i widget che vivono direttamente nella barra:

| Widget | Cosa mostra o apre |
| --- | --- |
| Orologio | Ora e data, secondi e formato 24h opzionali |
| Data | Giorno e data completi; clic per il calendario |
| CPU / RAM / CPU + RAM | Percentuale e barra in tempo reale |
| Dettaglio RAM | Memoria usata sul totale, in GB |
| Disco | Spazio libero e utilizzo del volume di sistema |
| Uptime | Tempo dall'accensione del PC |
| Batteria | Percentuale o alimentazione AC |
| Rete | Nome dell'adattatore attivo o `Offline` |
| Volume | Volume di Windows; clic per muto/riattiva |
| Luminosità | Luminosità del monitor principale, se controllabile |
| Media | Play/pausa del player attivo, con titolo e artista |
| Ricerca | Apre la ricerca globale |
| Schede | Apre il selettore dei gruppi di finestre |
| Pannello widget | Apre calendario, sistema e widget |
| Controlli rapidi | Apre volume, media e rete |
| Impostazioni | Apre Nebula Studio |
| Notifiche | Apre il centro notifiche (`Win+N`) |
| Impostazioni rapide | Apre rete, volume e luminosità (`Win+A`) |
| Panoramica | Mostra tutte le finestre (`Win+Tab`) |
| Mostra desktop | Riduce a icona tutte le finestre (`Win+D`) |
| Cattura schermo | Avvia lo strumento di cattura (`Win+Shift+S`) |
| Appunti | Apre la cronologia appunti (`Win+V`) |
| Emoji | Apre la tastiera emoji (`Win+.`) |
| Blocca | Blocca la sessione (`Win+L`) |
| Wi-Fi / Bluetooth | Aprono il pannello **Rete** di Nebula, con radio, reti e dispositivi |
| Esplora file | Apre la cartella personale |
| Terminale | Apre Windows Terminal o PowerShell |
| Gestione attività | Apre Gestione attività |
| Cestino | Apre il cestino |
| Separatore | Linea verticale tra due gruppi |

Il catalogo contiene 33 widget e la barra ne accetta fino a 40. Quando i widget
non entrano nella larghezza disponibile, la barra non li taglia: la zona sistema
ha uno scroller orizzontale e la **rotellina del mouse** la fa scorrere. Ogni
widget aggiorna i propri valori dentro un `try/catch`: un sensore assente
(batteria, luminosità, endpoint audio) mostra `—` solo nel proprio riquadro e
non interrompe gli altri né il timer della barra.

Ogni widget può restare **Espanso** (icona, valore e barra) oppure essere
ridotto a **Icona**, dal pulsante nella sua riga in **Studio → Widget**. La
scelta è salvata per singolo widget in `TaskbarWidgetModes` (`icon`; l'assenza
della chiave significa espanso). I widget espansi si ingrandiscono del 16% al
passaggio del mouse, senza spostare gli altri; quelli in modalità icona restano
compatti.

La lista è salvata in `settings.json` come `TaskbarWidgets`. I vecchi
interruttori `ShowClock`, `ShowStats` e `ShowBattery` vengono usati una sola
volta per migrare la configurazione esistente, poi la lista è l'unica fonte di
verità. I widget sono indipendenti dall'ordine delle zone e possono stare anche
in una dock non a tutta larghezza.

## Serve l'amministratore?

No: Windows non ha un'impostazione, un criterio di gruppo o una chiave di
registro che "toglia" la taskbar lasciando attivo Explorer. `Shell_TrayWnd`
appartiene alla stessa sessione utente, quindi nasconderla, metterla in
auto-hide e riservare il bordo con l'AppBar funziona da un processo normale,
senza elevazione. Le uniche vie che richiedono davvero l'amministratore sono la
sostituzione della shell (`Winlogon\Shell`) o la modalità chiosco/Assigned
Access: entrambe rimuovono Explorer e il desktop, non solo la barra, e non sono
adatte a un uso quotidiano. Se la barra Nebula non arriva a filo del bordo è
quindi un problema di geometria dell'AppBar, non di permessi.

## Confronto con Windows 11 Taskbar Styler (Windhawk)

[Windows 11 Taskbar Styler](https://windhawk.net/mods/windows-11-taskbar-styler)
è un mod di Windhawk che **inietta stili XAML dentro Explorer**: cambia colori,
raggi, spaziature e visibilità degli elementi della taskbar nativa. È potente,
ma richiede Windhawk, iniezione nel processo di Explorer e può rompersi a ogni
aggiornamento di Windows.

Nebula segue un approccio diverso:

| Aspetto | Taskbar Styler (Windhawk) | Nebula |
| --- | --- | --- |
| Dove gira la UI | Dentro `explorer.exe` | Processo WPF separato |
| Personalizzazione | Stili XAML sugli elementi nativi | Barra e widget propri, riordinabili |
| Spazio riservato | Taskbar nativa | AppBar registrata da Nebula |
| Start, calendario e flyout | Nativi, stylabili in parte | Restano nativi di Windows |
| Rischio dopo un update | Alto (dipende da Explorer) | Basso (nessuna iniezione) |
| Ripristino dopo crash | Explorer riavvia la barra originale | Watchdog + stato pendente |

L'ispirazione presa da Taskbar Styler è il modello visivo: barra a tutta
larghezza, gruppo centrale, aree sinistra/centro/destra, icone compatte,
trasparenza e hover curati. Nebula non inietta nulla in Explorer e non modifica
i file di sistema: possiede una finestra AppBar separata.

## Cosa resta nativo

- Il menu Start, il centro notifiche, il calendario di Windows e i flyout di
  volume/rete restano quelli di Windows. Aprirli dalla barra Nebula usa le
  scorciatoie o le superfici Nebula equivalenti, non li ridisegna.
- Le finestre UWP protette o elevate non possono essere raggruppate nelle
  schede; è un limite di `SetParent`/DWM, non della barra.
- La barra non diventa la taskbar predefinita di Windows: è attiva solo mentre
  Nebula è in esecuzione. All'uscita viene ripristinata quella originale.

## Se la barra non riserva lo spazio

Dopo l'avvio Nebula attende fino a 5 secondi che la finestra sia disposta e
l'AppBar registrata. Se non lo è, ripristina la taskbar nativa e **riprova ogni
15 secondi per 8 volte**; la stessa ripresa avviene se il watchdog non riesce a
restare attivo. La scelta salvata non viene spenta: un errore transitorio non
disattiva più la sostituzione per gli avvii successivi.

1. Controlla che **Sostituisci la taskbar di Windows** e **Riserva lo spazio
   come una taskbar** siano attivi.
2. Disattiva e riattiva **Riserva lo spazio** per forzare una nuova
   registrazione AppBar.
3. Verifica `%APPDATA%\Nebula\app.log` per le righe `AppBar reserved`,
   `AppBar released`, `AppBar reservation was not applied`,
   `Taskbar replacement verified after startup` e
   `Taskbar replacement retry N/8`.
4. Se un altro programma usa l'AppBar sullo stesso bordo, Windows può spostare
   la striscia: chiudi l'altro programma o cambia bordo alla dock.
5. Se il preflight fallisce, Nebula lascia la taskbar nativa visibile e non
   mostra la dock: controlla `Nebula.Watchdog.exe` e `app.log`.

## Ripristino

Uscita normale, crash e terminazione forzata sono gestiti da
[docs/ripristino-taskbar.md](ripristino-taskbar.md). L'AppBar è legata alla
finestra di Nebula: quando il processo termina, Windows rimuove la prenotazione
e l'area di lavoro torna disponibile. Il watchdog ripristina la taskbar nativa
visibile (senza auto-hide).

## Riavvio di Explorer

Se Explorer si riavvia mentre Nebula è attiva, la vecchia registrazione AppBar
muore con la shell. Il selftest ha riprodotto un riavvio reale di Explorer: la
work area è passata da 1008 a 1032 e la barra nativa è ricomparsa. Nebula ha
rilevato che `ABM_SETPOS` non aveva riservato l'area, ha forzato
`ABM_REMOVE`/`ABM_NEW`, ha richiuso la nuova barra nativa e la work area è
tornata a 1008. Alla chiusura dell'app la taskbar nativa è tornata visibile.
