# Aggiornamenti e release GitHub

Nebula usa **GitHub Releases** come canale di aggiornamento. Il repository pubblica una Release con:

- `Nebula-Setup-<versione>-x64.exe`: l'installer per-utente self-contained, senza obbligo di installare .NET.
- `Nebula-win-x64.zip`: la build Windows self-contained, con `Nebula.exe` e `Nebula.Watchdog.exe` nella radice dello ZIP; serve alla versione portatile e all'aggiornamento automatico.
- `update.json`: il manifest letto dalle installazioni esistenti.

L'URL predefinito del manifest è:

```text
https://github.com/simoita/Smart-taskbar/releases/latest/download/update.json
```

`simoita/Smart-taskbar` viene inserito nella build dal workflow (`.github/workflows/release.yml`) tramite la proprietà MSBuild `UpdateRepository`, quindi non va scritto a mano nel codice. Le installazioni create da una Release GitHub hanno già l'URL corretto.

## Flusso di pubblicazione

Il flusso previsto è:

1. modifichi il codice e fai il commit;
2. lanci lo script di release, che aggiorna `<Version>`, crea commit e tag e li invia a GitHub;
3. GitHub Actions compila la build, crea lo ZIP, calcola lo SHA-256 e crea la Release;
4. le installazioni precedenti controllano il manifest all'avvio e ogni 15 minuti;
5. quando trovano una versione più recente mostrano il popup **Aggiorna e riavvia**;
6. il clic scarica il pacchetto, verifica lo SHA-256, scrive `update-state.json`, `recover-update.ps1` e la voce `RunOnce`, conserva la cartella precedente in `.old` e avvia la nuova build;
7. la nuova build conferma l'avvio solo dopo il Boot e il controllo di taskbar/AppBar; se la conferma non arriva entro **60 secondi**, lo script ripristina `.old`, riapre la versione precedente e registra versione e SHA-256 del pacchetto guasto in `failed-update.json`;
8. se il PC si spegne o il processo viene ucciso, il `RunOnce` esegue il recovery al logon successivo, senza lasciare stato pendente né loop. Anche il recovery che esegue un rollback registra lo stesso divieto; se il kill è avvenuto prima dello swap, il divieto non viene scritto.
9. dai controlli successivi quel pacchetto viene ignorato, anche dopo un riavvio dell'app. Un pacchetto corretto con lo **stesso numero di versione ma SHA diverso**, o una versione superiore, torna proponibile; un aggiornamento confermato elimina `failed-update.json`.

Per pubblicare una nuova versione:

```powershell
# Prima committa le modifiche al codice.
# Poi, dalla radice del progetto:
.\scripts\release.cmd -Version 1.4.0
```

Se ometti `-Version`, viene incrementata la patch:

```powershell
.\scripts\release.cmd
```

Per creare commit e tag senza inviarli subito:

```powershell
.\scripts\release.cmd -Version 1.4.0 -NoPush
git push origin HEAD
git push origin v1.4.0
```

Il push del tag è l'evento che avvia la Release. In alternativa puoi avviare **Actions → Release Nebula → Run workflow** e inserire la versione; il workflow crea la Release e il tag.

## Primo setup GitHub

Crea un repository vuoto su GitHub (senza README, `.gitignore` o licenza) e collega questa cartella:

```powershell
git init -b main
git add .
git commit -m "Initial commit"
git remote add origin https://github.com/simoita/Smart-taskbar.git
git push -u origin main
```

Poi verifica in **Settings → Actions → General** che i workflow possano scrivere nel repository. Il workflow dichiara già `permissions: contents: write`; se l'organizzazione impone permessi restrittivi, abilita la scrittura per `GITHUB_TOKEN`.

La prima Release va creata con lo script o con **Run workflow**. Da quel momento l'URL `releases/latest/download/update.json` è disponibile.

## Cosa fa il workflow

Su ogni tag `vMAJOR.MINOR.PATCH`:

1. verifica che `<Version>` in `src/Nebula/Nebula.csproj` corrisponda al tag;
2. esegue `dotnet publish` con `-r win-x64 --self-contained true`, `UpdateRepository=${{ github.repository }}` e la versione del tag;
3. verifica che anche `Nebula.Watchdog.runtimeconfig.json` sia self-contained;
4. installa Inno Setup e crea `Nebula-Setup-<versione>-x64.exe` da `installer/Nebula.iss`;
5. crea `Nebula-win-x64.zip` con il contenuto di `publish/` nella radice;
6. calcola gli SHA-256 e genera `update.json` con `version`, `downloadUrl`, `sha256` e `notes`;
7. crea la Release e carica setup, ZIP e manifest, impostandola come latest.

Il workflow è in `.github/workflows/release.yml`. Non richiede secret manuali: usa il `GITHUB_TOKEN` fornito da Actions.

## Cosa succede sul PC dell'utente

- Il controllo automatico parte **15 secondi dopo l'avvio** e si ripete ogni **15 minuti**.
- Se `AutoUpdate` è attivo e il manifest contiene una versione maggiore, Nebula mostra una finestra con versione installata, versione disponibile, note e i pulsanti **Più tardi** e **Aggiorna e riavvia**. Durante il download la finestra mostra una **barra di avanzamento** con la percentuale, i **byte scaricati sul totale** annunciato dal server e la **velocità** in MB/s; gli stessi dati compaiono nel pannello Studio se l'installazione parte da lì.
- **Aggiorna e riavvia** scarica lo ZIP, verifica lo SHA-256, estrae in staging, scrive `%LOCALAPPDATA%\Nebula\updates\update-state.json`, genera `recover-update.ps1` e registra la voce `RunOnce` `NebulaUpdateRecovery`.
- Lo script attende la chiusura di Nebula e del watchdog, quindi rinomina la cartella corrente in `.old`, mette la nuova build in posizione e preserva `unins000.exe`/`unins000.dat`. La nuova build viene avviata con `--startup`.
- La nuova build scrive `update-confirmed.txt` solo dopo il Boot riuscito e il controllo di taskbar/AppBar; lo script attende fino a **60 secondi**. Se la conferma non arriva (crash, binario non valido, taskbar non riservata), ferma la nuova build, sposta la cartella guasta in `.failed`, ripristina `.old`, rimuove stato e `RunOnce`, scrive il marker di errore e riapre `Nebula.exe`.
- Se la conferma arriva, lo script elimina `.old`, stato e marker e lascia la nuova versione installata. Se il PC si spegne o il processo viene ucciso prima, `recover-update.ps1` viene eseguito dal `RunOnce` al logon: se c'è una conferma finalizza, altrimenti ripristina `.old`; se il kill è avvenuto **prima dello swap** (`.old` assente), lascia intatta l'installazione viva e rimuove solo stato e `RunOnce`. La seconda esecuzione senza stato è un no-op.
- Se il rollback avviene dopo lo swap, viene scritto anche `%LOCALAPPDATA%\Nebula\updates\failed-update.json` con `Version` e `Sha256`. Il controllo successivo confronta entrambi: lo stesso pacchetto resta soppresso anche dopo un riavvio, mentre un correttivo con lo stesso numero di versione e SHA diverso viene mostrato e installato. Un aggiornamento confermato cancella il file. La soppressione non blocca una versione superiore.
- In caso di errore scrive `%LOCALAPPDATA%\Nebula\updates\update-failed.txt`. Al riavvio Nebula consuma il marker e mostra una finestra **non modale** `Nebula · Aggiornamento non riuscito` con il motivo; `--exit` continua a funzionare anche con quella finestra aperta. Se il pacchetto è soppresso, **Verifica ora** mostra lo stato corrispondente senza riaprire il popup.
- I file dell'utente in `%APPDATA%\Nebula` non vengono toccati.
- Se l'installazione è in `C:\Program Files` senza permessi di scrittura, la copia fallisce: installa Nebula in una cartella dell'utente oppure usa i permessi adatti. L'app non richiede UAC da sola.
- Se il PC è offline o GitHub non risponde, il controllo fallisce in silenzio e viene ritentato al ciclo successivo.

Il pulsante **Verifica ora** nello Studio esegue un controllo manuale e abilita **Scarica e installa** quando trova una versione. Il popup automatico viene mostrato una sola volta per versione in ogni sessione; **Più tardi** lo chiude.

Il log dell'applicazione è in `%LOCALAPPDATA%\Nebula\updates\apply-update.log` se la copia fallisce.

## Verifica end-to-end eseguita il 18 novembre 2026

La verifica è stata fatta su questa macchina con il setup reale `Nebula-Setup-1.5.0-x64.exe`, non con un'installazione finta:

- Installazione 1.5.0 per-utente: **265 file**, disinstallatore presente, avvio automatico e impostazioni salvati.
- Build di prova **1.5.1** creata solo con `-p:Version=1.5.1`; la versione sorgente è rimasta **1.5.0**. Manifest e ZIP reali serviti su `http://127.0.0.1:8765/`; il codice di download, verifica, swap e riavvio è quello di produzione. Fuori dal loopback l'URL deve essere HTTPS.
- **Rollback reale:** la 1.5.0 ha scaricato una 1.5.1 con `Nebula.exe` a **0 byte**. La nuova build non è partita, lo script ha ripristinato `.old` entro la finestra di verifica e ha riaperto la 1.5.0: **265 file, 0 mancanti**, `unins000.exe/.dat` presenti, nessuna cartella `.old/.new/.failed`, nessun `update-state.json` e nessuna voce `RunOnce`. Conservati `theme=aurora`, `AccentHex=#123456`, `DockHeight=78`, `ItemSpacing=7`, widget `clock,battery`, `LaunchAtStartup=True` e `Run` con `--startup`; taskbar riservata a **990 px** e app viva.
- **Soppressione e correzione con lo stesso numero:** il rollback ha scritto `failed-update.json` con `Version=1.5.1` e lo SHA del pacchetto guasto. Al controllo automatico successivo, nella nuova istanza 1.5.0 avviata dal rollback, il log ha registrato `Update package suppressed after a failed start: 1.5.1` e il popup non è comparso. Cambiando il manifest a una 1.5.1 valida con **SHA diverso**, il popup è comparso e l'aggiornamento è riuscito (`Update startup health confirmed: 1.5.1`); `failed-update.json`, stato e `.old` sono stati rimossi e le impostazioni sono rimaste intatte.
- **Aggiornamento sano:** ripetuto con la 1.5.1 valida dalla 1.5.0 appena ripristinata e poi di nuovo dopo il test di stato pendente. La nuova build ha scritto `Update startup health confirmed: 1.5.1` e `Update health confirmation written for 1.5.1`, è rimasta installata e viva oltre la finestra di 60 s, con **265 file**, `unins000.exe/.dat` e nessun rollback falso.
- **Stato pendente da interruzione:** creato uno stato reale (1.5.1 in posizione, `.old` 1.5.0, nessuna conferma) ed eseguito il comando `RunOnce` che l'app registra. Il recovery ha ripristinato la 1.5.0, rimosso stato, `.old` e `RunOnce`, riavviato l'app e lasciato la taskbar a **990 px**. Una seconda esecuzione senza stato è uscita 0 senza modificare nulla: nessun loop.
- **Kill prima dello swap:** creato uno stato pendente con `.old` assente, come se l'app fosse stata uccisa subito dopo aver scritto `update-state.json`. Il `RunOnce` ha lasciato intatta la **1.5.1** già installata, rimosso stato e `RunOnce`, evitato `.failed` e riavviato l'app con taskbar a **990 px**. È il caso che una versione precedente del recovery avrebbe potuto spostare via lasciando il disco senza installazione.
- `--selftest` sulla 1.5.1, da processo pulito: **RESULT: PASS**, taskbar nativa visibile, area di lavoro **1032 px**, AppBar rilasciata.
- **Una sola proprietaria:** con la 1.5.1 già in esecuzione, un secondo avvio normale e un secondo `--selftest` sono usciti con codice 0 lasciando un solo processo Nebula, lo stesso PID e la stessa area di lavoro riservata. `--selftest` usa lo stesso mutex dell'avvio normale: se trova un'istanza attiva cede senza toccare taskbar, AppBar o watchdog.
- **Idle finale:** 60,5 s, CPU **0,2839% di un core** (0,1718 s), working set medio **182,74 MB**, privata **93,16 MB**, misurati sulla 1.5.1 installata dal flusso reale e con la soppressione dei pacchetti guasti, tema Aurora, altezza dock 78, spaziatura 7, widget orologio/batteria e aggiornamenti automatici attivi. La misura precedente dello stesso profilo era **0,2574%**, **175,77 MB** e **88,96 MB**; la baseline era **0,7791%**, **186,40 MB** e **97,74 MB**; la 1.5.0 pre-ottimizzazioni era **1,1167%**, **197,05 MB** e **108,93 MB**. Dettagli e configurazione nel README.
- **Fallimento SHA-256:** il popup ha mostrato **Hash del pacchetto non valido: aggiornamento annullato**; la 1.5.0 è rimasta avviabile e non sono rimasti ZIP o staging.
- **Fallimento swap con file bloccato:** con `createdump.exe` aperto in modo esclusivo, la cartella non è stata spostata. La 1.5.0 è rimasta intatta (265 file, 0 mancanti), la finestra non modale ha mostrato l'errore, e non sono rimasti `.new/.old`, staging, ZIP o `update-failed.txt`. `Nebula.exe --exit` ha chiuso la finestra e l'app con exit code 0.
- **Disinstallazione 1.5.1:** exit code 0, cartella, collegamenti, `Run`, `App Paths` e voce **App installate** rimossi; taskbar visibile e nessuno stato pendente.
- Build Release e `-r win-x64 --self-contained true`: **0 avvisi, 0 errori**.

Non è stata creata né usata una Release GitHub: il canale `releases/latest/download/update.json` resta il percorso previsto per la pubblicazione, ma in questa sessione non è stato possibile verificarlo senza tag, push e release. Lo ZIP non è firmato. Il rollback copre il caso in cui la nuova build non conferma l'avvio entro 60 secondi; un crash **dopo** la conferma non annulla automaticamente un aggiornamento già sano. Il caso power-off/kill è stato riprodotto con uno stato pendente reale e l'esecuzione del comando `RunOnce`, non con un reboot fisico della macchina.

## Build locale e repository personalizzato

Una build locale non ha un repository GitHub incorporato, quindi il campo **Manifest aggiornamenti** in **Studio → Info** resta vuoto. Per provare gli aggiornamenti da un repository:

```powershell
.\.dotnet\dotnet.exe publish .\src\Nebula\Nebula.csproj -c Release -r win-x64 --self-contained true -o .\dist `
  -p:Version=1.4.0 `
  -p:UpdateRepository=simoita/Smart-taskbar
```

In alternativa inserisci nel campo **Manifest aggiornamenti** un URL personalizzato, anche un manifest locale servito in HTTP per i test. Un URL personalizzato ha precedenza sul valore predefinito.

## Formato del manifest

```json
{
  "version": "1.4.0",
  "downloadUrl": "https://github.com/simoita/Smart-taskbar/releases/download/v1.4.0/Nebula-win-x64.zip",
  "sha256": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
  "notes": "Nuovi widget e correzioni."
}
```

- `version`: versione numerica `MAJOR.MINOR.PATCH`; il confronto è numerico, quindi `1.10.0` è più recente di `1.9.0`.
- `downloadUrl`: URL HTTPS dello ZIP.
- `sha256`: hash esadecimale dello ZIP; se non coincide, l'installazione viene annullata.
- `notes`: testo mostrato nel popup e nello Studio.

Lo ZIP deve contenere i file della build direttamente nella radice e almeno `Nebula.exe` e `Nebula.Watchdog.exe`. Non includere `settings.json`, log o dati personali.

## Sicurezza e limiti

- Lo SHA-256 verifica l'integrità del pacchetto, non autentica crittograficamente l'autore. Proteggi il repository e l'account GitHub; per una distribuzione pubblica valuta la firma del codice.
- Il repository e le Release devono essere **pubblici** (o gli asset scaricabili senza login): l'aggiornatore non invia credenziali GitHub. Su un repository privato il download del pacchetto riceve un errore di autenticazione.
- Non cancellare una Release ancora puntata da `latest`: pubblica una versione successiva per correggere un problema.
- Se rinomini il repository, le installazioni vecchie continuano a puntare al vecchio URL; pubblica una build con il nuovo `UpdateRepository` o indica il nuovo URL nelle impostazioni.
- Il setup e lo ZIP sono **self-contained**: sul PC non serve installare .NET. L'aggiornamento automatico richiede accesso HTTPS a GitHub e il permesso di scrivere nella cartella dell'app.
- Il setup non è firmato con un certificato di code signing: SmartScreen può mostrare un avviso al primo avvio.
