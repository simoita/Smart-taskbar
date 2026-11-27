# Schede delle finestre

Nebula può raccogliere finestre di altre applicazioni in un gruppo e mostrarle con una tab bar disegnata da Nebula, ancorata alla finestra attiva. Le finestre non vengono spostate nella dock e non vengono "adottate": rimangono processi esterni, con la loro posizione e dimensione native.

## Creare un gruppo

1. Apri la dock.
2. Clicca l'icona **schede** (due rettangoli sovrapposti) oppure fai clic destro su un punto vuoto della dock e scegli **Raggruppa finestre in schede…**.
3. Seleziona almeno due finestre nel selettore.
4. Premi **Crea gruppo**.

Lo stesso comando è disponibile nello studio, pagina **Schede → Scegli finestre…**. Dal menu di un'app nella dock, **Raggruppa schede…** apre il selettore con le finestre di quell'app già preselezionate.

Nebula rifiuta le finestre che non può gestire in sicurezza: finestre di sistema, UWP protette, finestre con privilegi superiori a Nebula, finestre di Nebula stessa e finestre non leggibili. Il selettore mostra un messaggio esplicito e non crea un gruppo parziale.

## La tab bar

La tab bar contiene una scheda per ogni finestra, con titolo e icona reali. Il titolo si aggiorna automaticamente quando la finestra lo cambia.

Con **Posizione = Sopra la finestra** la barra è la barra superiore della finestra attiva: copre esattamente la vecchia barra del titolo di Windows (stessa larghezza, stessa altezza della fascia, stesso bordo) e al suo posto mostra le schede e i pulsanti **riduci**, **ingrandisci/ripristina** e **chiudi**. Il trascinamento della barra sposta la finestra con l'aggancio di Windows, il doppio clic la ingrandisce o ripristina, e dai bordi della barra si ridimensiona la finestra come dalla cornice. Le finestre che disegnano da sé la propria intestazione (nessuna fascia non client) non possono essere coperte senza tagliarne il contenuto: per loro, e per **Posizione = Sotto la finestra**, la barra resta staccata e usa l'altezza scelta nello studio.

| Azione | Come |
| --- | --- |
| Selezionare | Clic sulla scheda |
| Riordinare | Trascinare la scheda |
| Chiudere la finestra | × sulla scheda, oppure menu destro → **Chiudi finestra** |
| Staccare la finestra dal gruppo | Menu destro → **Stacca scheda** |
| Ridurre, ingrandire, chiudere | Pulsanti della finestra a destra nella barra |
| Spostare la finestra | Trascinare la barra |
| Ridimensionare la finestra | Trascinare il bordo della barra |
| Menu di sistema | Menu destro sulla barra (ripristina, sposta, dimensiona, riduci, ingrandisci, chiudi) |
| Chiudere il gruppo | Menu destro → **Chiudi gruppo e ripristina le finestre** |

Selezionando una scheda, la finestra corrispondente torna in primo piano e le altre vengono nascoste. Le finestre nascoste conservano la loro `WINDOWPLACEMENT` originale: staccandole o chiudendo il gruppo vengono riportate esattamente alla posizione e dimensione di quando sono entrate nel gruppo. Se una finestra viene chiusa dall'utente o dall'app, la sua scheda sparisce entro il tick di controllo (300 ms).

La tab bar è una finestra `WS_EX_NOACTIVATE`: cliccare una scheda non ruba il focus alla dock o allo studio e non porta in primo piano una finestra protetta.

## Stili e impostazioni

Nello studio, pagina **Schede**:

- **Attiva le schede**: abilita o disabilita la funzione. Disattivandola, tutti i gruppi vengono chiusi e le finestre ripristinate.
- **Stile**: **Vetro**, **Minimal**, **Neon**, **Soft**, **Mono**, **Liquid**; cambia angoli, riempimento, bordo e accento della barra.
- **Altezza della barra**: da 34 a 64 px, usata quando la barra resta staccata. Se prende il posto del titolo di Windows segue l'altezza della fascia della finestra.
- **Posizione**: **Sopra** sostituisce la barra del titolo; **Sotto** lascia la barra staccata sotto la finestra.
- **Animazioni**: apertura, cambio scheda e chiusura seguono **Animazioni fluide** e **Velocità** della pagina Animazioni. Con le animazioni disattivate la barra compare e scompare senza dissolvenza.

Ogni scheda ritaglia i propri elementi interni (accento e sottolineatura) con il raggio della sua pillola: nessuna parte esce dalla sagoma, in nessuno stile.

Tutte le impostazioni vengono salvate in `%APPDATA%\Nebula\settings.json` e applicate dal vivo senza riavviare Nebula.

## Crash e terminazione forzata

Quando un gruppo è attivo, Nebula scrive `%APPDATA%\Nebula\tabs.pending` con handle e posizione originali di ogni finestra. All'uscita normale il file viene rimosso dopo il ripristino.

Se Nebula viene terminata con `taskkill /F`, il watchdog:

1. attende la fine del processo;
2. ripristina le finestre elencate in `tabs.pending`;
3. verifica che siano davvero visibili, non solo che `SetWindowPlacement` abbia restituito successo;
4. ripristina la taskbar nativa;
5. rimuove `tabs.pending`.

Il risultato viene verificato sul campo: dopo il kill le tre finestre di prova erano visibili nelle posizioni originali, la tab bar non esisteva più e la taskbar nativa era visibile.

## Prestazioni misurate

Macchina di prova: Windows, un monitor 1920×1080, build pubblicata in `dist/`, tre finestre WinForms di test.

| Scenario | CPU (un core) | Memoria privata | Tab bar visibile |
| --- | --- | --- | --- |
| Dock sola, build 1.1 | **1,17%** (misura della dock) | **98,1–98,4 MB** | — |
| Tre finestre raggruppate, tab bar Minimal 56 px in basso (build precedente, non rimisurata) | **0,78%** | **147,6 MB** | **16–29 ms** |

La tab bar compare in **16–29 ms** dal comando di raggruppamento (misurato dal timestamp del gruppo al primo render). La memoria aumenta di circa **32 MB** rispetto alla dock finale attuale per la finestra WPF della tab bar (trasparenza, animazioni e stili); la misura con tre finestre risale alla build precedente e non è stata ripetuta in questa revisione.

## Test eseguiti

- Due e tre finestre WinForms con titoli diversi, icona reale e geometrie distinte.
- Creazione gruppo da dock, menu contestuale della dock e studio.
- Cambio scheda con finestra in primo piano e altre nascoste.
- Riordino con trascinamento.
- Barra in alto su due finestre WinForms reali: rettangolo della barra uguale alla fascia visibile della finestra (267,200–1033,231), `clientTop=231`, nessun pixel del contenuto coperto; pulsanti riduci/ingrandisci/chiudi e cattura `tabbar-window-top.png` nel self-test.
- Titolo aggiornato a runtime: la scheda ha seguito il nuovo titolo.
- Chiusura di un membro: processo terminato, scheda rimossa senza fantasmi.
- Stacco: geometria originale ripristinata esattamente (350,260,760×480).
- Chiusura gruppo con × della barra e con **Chiudi tutti i gruppi**: tutte le finestre visibili.
- Stile Vetro → Neon → Minimal, altezza 46 → 56 e posizione alto → basso applicati dal vivo; valori presenti in `settings.json` dopo il riavvio e applicati al nuovo gruppo.
- Uscita normale con gruppo attivo: finestre ripristinate, tab bar chiusa, taskbar nativa visibile, `tabs.pending` assente.
- `taskkill /F`: watchdog con 3 finestre visibili e geometrie originali, taskbar nativa visibile, nessun file pendente.

## Limiti

- Le finestre UWP protette, elevate o di sistema non sono state osservate su questa macchina perché richiedono un contesto UAC/admin: il codice le rifiuta, ma il rifiuto visivo non è stato riprodotto.
- È disponibile un solo monitor fisico: ancoraggio su monitor secondari e DPI misti non è stato osservato.
- La tab bar usa WPF per mantenere stile, trasparenza e animazioni coerenti con Nebula; il costo è descritto sopra. Anche widget e controlli rapidi usano una finestra WPF condivisa, documentata in [superfici.md](superfici.md).
- L'animazione di apertura/chiusura è verificata a livello di codice e con tempi di render; la resa visiva delle animazioni non è stata misurata con strumenti esterni.
