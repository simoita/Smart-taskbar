# Widget, controlli rapidi e ricerca

Nebula usa le superfici già presenti nella dock, non processi o finestre WPF aggiuntive:

- **Widget**, **Controlli rapidi** e **Rete** condividono la stessa `WidgetsWindow` con tre schede. Aprendo una scheda si riusa la finestra già creata; chiudendola si fermano timer e risorse della scheda.
- **Ricerca** usa la `LauncherWindow` esistente. Cerca app del menu Start e, se attivo, finestre aperte tramite `WindowTracker`.
- **Start** (tasto Windows) usa un solo `StartPanelWindow`, creato in anticipo con `DispatcherPriority.ApplicationIdle` e poi riusato: alla chiusura viene nascosto, non distrutto. A ogni apertura torna con la ricerca vuota e il fuoco già nel campo, prima che parta l'animazione; se il fuoco non fosse ancora arrivato, il primo tasto premuto lo porta nella ricerca invece di perdersi.
- Tutte le superfici leggono lo stesso `settings.json` in `%APPDATA%\Nebula` e reagiscono dal vivo alle modifiche dello studio.

## Aprire le superfici

| Superficie | Dalla dock | Dallo studio | Scorciatoia |
| --- | --- | --- | --- |
| Widget | icona **Widget** | **Widget → Apri pannello widget** | `Ctrl+Alt+W` |
| Controlli rapidi | icona **Controlli rapidi** | **Controlli → Apri controlli rapidi** | — |
| Rete | widget **Wi-Fi**, **Bluetooth** o **Rete** | **Controlli → Apri controlli rapidi** | — |
| Ricerca | icona **Cerca** | **Ricerca → Apri ricerca** | `Ctrl+Alt+Spazio` |

Le icone della dock compaiono solo se la superficie è attiva. Il menu contestuale della dock offre le stesse voci. `Esc` chiude il pannello o la ricerca; un clic fuori dalla ricerca la chiude.

## Preset superficie

In **Studio → Stile → PRESET SUPERFICI** ci sono sei pulsanti: **Vetro**, **Minimal**, **Neon**, **Soft**, **Mono** e **Liquid**. Il preset scelto viene salvato in `SurfaceStyle` e applicato dal vivo alle tre superfici, anche se sono già aperte. Le schede non vengono ridisegnate da questi pulsanti; lo stile delle schede resta indipendente.

| Preset | Card / interna | Risultato / controllo | Slider | Tipografia |
| --- | --- | --- | --- | --- |
| Vetro | 22 / 14 px | 11 / 10 px | 4 px, pomello 17 px | variable, titoli semibold |
| Minimal | 10 / 8 px | 6 / 6 px | 3 px, pomello 13 px | Segoe UI, titoli semibold |
| Neon | 16 / 10 px | 9 / 8 px | 5 px, pomello 18 px | variable, titoli bold |
| Soft | 26 / 18 px | 22 / 12 px | 5 px, pomello 19 px | variable, titoli semibold |
| Mono | 6 / 4 px | 16 / 3 px | 3 px, pomello 14 px | Segoe UI, titoli semibold |
| Liquid | 24 / 18 px | 14 / 12 px | 5 px, pomello 18 px | variable, titoli semibold |

Il preset cambia anche sfondo, bordo, padding, spessore del bordo e peso del titolo. Widget, controlli rapidi e ricerca usano le stesse risorse dinamiche `S.*`: non serve riaprirli. La tabella qui sopra descrive i valori impostati da `ThemeEngine.ApplySurfaceResources`; le famiglie e le dimensioni dei font sono risorse dinamiche, non valori duplicati nelle singole viste.

Con **Animazioni fluide** disattivato, l'apertura di widget e ricerca non avvia fade o slide; con l'interruttore riattivato le transizioni tornano. Il grafico CPU viene fermato quando il pannello passa ai controlli; la scheda Rete aggiorna lo stato delle radio ogni 7 secondi e le reti Wi-Fi al massimo ogni 15 secondi, per non forzare una scansione continua.

## Widget

La scheda **Widget** mostra la striscia dei widget scelti dal catalogo, oltre a orologio, data, statistiche CPU/RAM, batteria (se presente) e grafico CPU. Il catalogo contiene 33 widget; ogni widget si attiva o disattiva in **Studio → Widget** e la striscia si aggiorna senza chiudere il pannello. Nella stessa riga il pulsante **Icona/Espanso** decide se il widget mostra il valore completo o solo il glifo; i widget espansi si ingrandiscono al passaggio del mouse. Orologio, data, secondi, formato 24 ore, statistiche e batteria hanno interruttori dedicati. Il pannello segue stile, accento, vetro, grana, bagliore e animazioni correnti.

## Controlli rapidi

La scheda **Controlli rapidi** agisce sul sistema:

- **Volume e mute:** endpoint COM predefinito (`IAudioEndpointVolume`); lo slider e il pulsante cambiano il volume reale e mostrano la percentuale.
- **Media:** pulsanti precedente, play/pausa e successivo inviano i tasti multimediali di Windows (`keybd_event`), quindi funzionano con il player attivo senza integrarsi nel suo processo.
- **Rete:** interruttori Wi-Fi e Bluetooth tramite l'API WinRT `Windows.Devices.Radios.Radio`, senza elevazione. Le reti si leggono e si aprono con `netsh wlan`; una rete protetta non ancora salvata apre le impostazioni Wi-Fi di Windows per la password. La sezione Bluetooth elenca i dispositivi associati e apre le impostazioni di Windows per associazione e connessione, perché Windows non espone un'API generica di connessione per ogni profilo.
- **Rete:** legge lo stato degli adattatori attivi e mostra interfaccia e velocità; **Apri impostazioni di rete** apre `ms-settings:network-status`.
- **Luminosità:** usa DDC/CI quando il monitor lo espone. Se il display non lo supporta, lo slider viene disabilitato e compare un avviso esplicito; non viene simulato un controllo inesistente.

In **Studio → Controlli** si trovano:

- **Mostra controlli rapidi**: interruttore della superficie e del pulsante in dock.
- **Layout compatto controlli**: larghezza 370 px invece di 410 px, applicata dal vivo.
- I pulsanti **Apri controlli rapidi**.

## Ricerca

La ricerca parte dalla dock, dallo studio o da `Ctrl+Alt+Spazio`. Mostra:

- applicazioni installate nel menu Start, con nome e icona;
- finestre aperte, con titolo, processo e sottotitolo **finestra aperta**;
- azioni della dock, ad esempio aprire widget o controlli.

La tastiera è il percorso principale: le frecce scorrono i risultati, `Invio` apre l'app o porta in primo piano la finestra selezionata, `Esc` chiude. L'ingranaggio in alto a destra (**Apri Studio dalla ricerca**) apre lo Studio lasciando la ricerca aperta: le modifiche di tema e preset superficie si applicano mentre la ricerca è visibile; chiudendo lo Studio, la ricerca torna in primo piano con il focus sulla casella.

In **Studio → Ricerca**:

- **Abilita ricerca**: interruttore della superficie e della scorciatoia.
- **Cerca finestre aperte**: include o esclude le finestre dai risultati.
- **Larghezza ricerca**: 560–900 px, applicata alla finestra di ricerca.

La larghezza e l'inclusione delle finestre sono state verificate impostandole, riaprendo la ricerca e dopo il riavvio.

## Misure di questa release

Build 1.1 pubblicata in `dist/`, un monitor 1920×1080, preset superficie **Vetro**, animazioni attive, nessun gruppo di schede aperto. Dock da processo pulito con warm-up iniziale; CPU media del tempo processo su 20 s:

| Scenario | CPU (un core) | Memoria privata |
| --- | --- | --- |
| Dock sola, dopo warm-up 20 s | **1,17%** su 20 s | **98,1 MB** (98,4 MB dopo altri 20 s) |
| Dock + pannello Widget, subito | **0,47%** su 20 s | **128,6 MB** a 2 s; **130,0 MB** dopo 20 s |
| Dopo chiusura del pannello | — | **129,6 MB** a 5 s |
| Watchdog | ~0% | ~6,5 MB |

**La dock sola è sotto il target di 116 MB.** Il pannello aperto non lo è: il pavimento misurato è **128,6–130,0 MB**. La causa è la seconda finestra WPF layered (`AllowsTransparency`) con calendario, grafici e striscia widget; non è heap gestito incontrollato. Alla chiusura il pannello ferma il timer, svuota calendario e striscia, rilascia l'endpoint COM e scende a **129,6 MB**, perché WPF/DWM trattengono cache native di finestra e layout.

Per scendere sotto 116 MB servirebbe unire il pannello alla dock o eliminare la superficie WPF layered; non è stato fatto in questa revisione perché cambierebbe l'architettura richiesta.

## Verifiche eseguite

- Apertura di widget, controlli e ricerca sia dalla dock sia dallo studio.
- Preset Vetro → Minimal → Neon cambiato dal vivo con widget e controlli aperti; la ricerca è stata mantenuta aperta con l'ingranaggio e si è aggiornata dal vivo.
- Animazioni disattivate: apertura di widget e controlli senza fade/slide residui; poi riattivate.
- Misure finali: dock 98,1–98,4 MB / 1,17%; pannello 128,6–130,0 MB / 0,47%; chiusura 129,6 MB. Dock sotto il target di 116 MB, pannello sopra.
- Volume reale portato da 100% a 26% e riportato a 100%; mute reale verificato con un probe COM esterno.
- Media play/pausa verificato osservando il pulsante del Lettore multimediale passare da **Sospendi** a **Riproduci** e viceversa.
- Ricerca della finestra **Senza titolo - Blocco note**: `Invio` l'ha portata in primo piano (titolo della finestra in foreground verificato).
- Ricerca di **Prompt dei comandi**: `Invio` ha avviato un nuovo processo `cmd` (conteggio processi 1 → 2).
- Ricerca con **Cerca finestre aperte** disattivato: la finestra non compare; riattivato: ricompare.
- Larghezza ricerca 800 px e 720 px applicata alla finestra alla riapertura; layout controlli compatto 370 px applicato dal vivo.
- Disattivazione e riattivazione dei pulsanti Widget, Ricerca e Controlli rapidi dalla pagina Studio.
- Animazioni disattivate: apertura e chiusura di widget e controlli senza errori; poi riattivate.
- Persistenza dopo riavvio di `QuickControlsCompact`, `SearchCardWidth` e `SearchIncludeWindows`; i valori di prova sono stati ripristinati ai default.
- Uscita normale con pannello aperto: nessun processo Nebula o watchdog, `taskbar.pending` assente, taskbar nativa visibile.
- `taskkill /F` con pannello aperto: taskbar ripristinata, nessun processo o file pendente.

## Limiti verificati

- **Luminosità:** il monitor di prova non espone DDC/CI; il controllo è disabilitato con avviso. Non è stato possibile verificare una variazione reale di luminosità.
- **Rete:** Nebula legge lo stato e apre le impostazioni di Windows. Attivare o disattivare un adattatore richiede privilegi amministrativi e non viene simulato.
- **Media successivo/precedente:** i tasti vengono inviati realmente, ma la sessione di prova aveva una sola traccia e il pulsante **Avanti** del player era disabilitato. Il cambio traccia non è stato osservato; play/pausa sì.
- **Ricerca:** indicizza il menu Start e le finestre visibili, non l'intero catalogo Store/UWP né file o contenuti delle app.
- **Memoria:** con il pannello aperto il target di 116 MB non è raggiunto; il pavimento è 128,6–130,0 MB, la dock sola 98,1–98,4 MB e dopo la chiusura 129,6 MB. Il costo residuo è WPF layered/native, non heap gestito incontrollato.
- **Monitor/DPI:** è disponibile un solo monitor fisico; multi-monitor e DPI misti non sono stati osservati dal vivo.
