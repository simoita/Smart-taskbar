# Ripristino della taskbar

Nebula non deve mai lasciare l'utente senza taskbar o con finestre raggruppate nascoste. Il ripristino è gestito da due percorsi indipendenti: l'uscita normale dell'app e un piccolo processo watchdog per crash e terminazioni forzate.

## Come funziona

Quando Nebula nasconde la taskbar nativa:

1. scrive `%APPDATA%\Nebula\taskbar.pending` con un GUID di sessione e lo stato originale della taskbar;
2. avvia `Nebula.Watchdog.exe` accanto a `Nebula.exe`;
3. il watchdog attende la fine del processo Nebula.

Quando viene creato un gruppo di schede, Nebula scrive anche `%APPDATA%\Nebula\tabs.pending` con handle e `WINDOWPLACEMENT` originali di ogni finestra. Il file viene aggiornato quando cambia la composizione del gruppo e rimosso dopo il ripristino.

Alla fine del processo:

- **uscita normale:** Nebula ha già ripristinato lo stato originale e rimosso il file pendente; il watchdog non trova nulla da fare e termina;
- **crash o `taskkill /F`:** il file pendente appartiene alla sessione del processo morto; il watchdog ripristina prima le finestre di `tabs.pending` (verificando che siano davvero visibili), poi la taskbar e termina. Non serve rilanciare Nebula.

Il watchdog agisce solo sul file della propria sessione: non tocca la taskbar di altre istanze o sessioni.

## Cosa cambia tra uscita normale e crash

| Evento | Taskbar | Dock e pannelli | Watchdog |
| --- | --- | --- | --- |
| Uscita normale | Stato originale esatto, anche auto-hide | Chiusi da Nebula | Termina senza interventi |
| Crash / `taskkill /F` | Visibile, per sicurezza | Spariscono con il processo; le finestre raggruppate vengono ripristinate | Ripristina finestre e taskbar, poi termina |
| Watchdog assente | Auto-hide di fallback | Restano gestiti da Nebula | Non disponibile |

Dopo un crash l'auto-hide originale non viene ripristinato: la taskbar viene mostrata visibile. È una scelta deliberata per non lasciare una barra nascosta e difficile da recuperare.

## Test manuale del crash

Dalla radice del progetto, con Nebula in esecuzione:

```powershell
$p = Start-Process .\dist\Nebula.exe -PassThru
Start-Sleep -Seconds 8
taskkill /F /PID $p.Id
Start-Sleep -Seconds 2
```

La taskbar deve essere di nuovo visibile. Non devono restare:

```powershell
Get-Process Nebula, Nebula.Watchdog -ErrorAction SilentlyContinue
Test-Path "$env:APPDATA\Nebula\taskbar.pending"
Test-Path "$env:APPDATA\Nebula\tabs.pending"
```

Risultato atteso: nessun processo, `False` per i file pendenti, nessuna finestra dock o studio, e nessuna finestra raggruppata nascosta.

Nella build finale, `taskkill /F` con il pannello Controlli rapidi aperto ha lasciato la taskbar nativa visibile, nessun processo Nebula/watchdog e `taskbar.pending` assente. Il tempo di ripristino non è stato cronometrato separatamente in questa revisione; nelle prove precedenti senza gruppi era di **335 ms**. Con un gruppo di tre finestre il watchdog aveva ripristinato **3 finestre visibili** nelle geometrie originali, taskbar visibile e `tabs.pending` rimosso.

## Riavvio dopo il kill

Rilanciare `Nebula.exe` dopo un crash è sicuro:

- l'app legge l'eventuale stato pendente e ripristina prima la taskbar;
- poi nasconde di nuovo la taskbar e mostra la dock;
- l'uscita successiva torna a ripristinare lo stato corrente.

Questo percorso è stato verificato sulla build pubblicata: dock e studio presenti dopo il rilancio, poi uscita normale con taskbar e nessun processo residuo.

## Log e diagnostica

- `%APPDATA%\Nebula\app.log`: avvio, sostituzione e ripristino della taskbar.
- `%APPDATA%\Nebula\watchdog.log`: watchdog avviato, attesa del processo, ripristino dopo crash.
- `%APPDATA%\Nebula\taskbar.pending`: stato temporaneo; deve sparire dopo un'uscita normale o dopo l'intervento del watchdog.
- `%APPDATA%\Nebula\tabs.pending`: finestre raggruppate e geometrie originali; deve sparire dopo chiusura gruppo, uscita normale o intervento del watchdog.

Messaggi tipici:

```text
Native taskbar hidden; crash watchdog armed.
Native taskbar restored.
taskbar restored after parent exit (state=0)
no pending state; nothing to restore
```

## Se qualcosa va storto

1. Sposta il mouse sul bordo inferiore o superiore: se la taskbar è in auto-hide, riappare.
2. Verifica che `Nebula.Watchdog.exe` sia nella stessa cartella di `Nebula.exe`.
3. Controlla `watchdog.log` per capire se il watchdog è stato avviato e se ha trovato lo stato pendente.
4. Riavvia Nebula e scegli **Info → Esci da Nebula** per un ripristino pulito.
5. In caso di emergenza, il riavvio di Esplora file o del PC ripristina comunque la taskbar di sistema.

Non cancellare `taskbar.pending` mentre Nebula è in esecuzione: serve al watchdog. Se il file resta dopo un crash e non c'è alcun processo Nebula, il watchdog non è riuscito ad avviarsi; controlla il log e la presenza dei file del watchdog.

## Cosa non fa

- Non crea un servizio di sistema.
- Non resta in background dopo il ripristino.
- Non richiede privilegi di amministratore.
- Non nasconde mai completamente la taskbar senza una via di ripristino: in assenza del watchdog usa l'auto-hide.
