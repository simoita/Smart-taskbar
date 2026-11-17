# Aggiornamenti e release GitHub

Nebula usa **GitHub Releases** come canale di aggiornamento. Il repository pubblica una Release con:

- `Nebula-win-x64.zip`: la build Windows framework-dependent, con `Nebula.exe` e `Nebula.Watchdog.exe` nella radice dello ZIP.
- `update.json`: il manifest letto dalle installazioni esistenti.

L'URL predefinito del manifest è:

```text
https://github.com/OWNER/REPO/releases/latest/download/update.json
```

`OWNER/REPO` viene inserito nella build dal workflow (`.github/workflows/release.yml`) tramite la proprietà MSBuild `UpdateRepository`, quindi non va scritto a mano nel codice. Le installazioni create da una Release GitHub hanno già l'URL corretto.

## Flusso di pubblicazione

Il flusso previsto è:

1. modifichi il codice e fai il commit;
2. lanci lo script di release, che aggiorna `<Version>`, crea commit e tag e li invia a GitHub;
3. GitHub Actions compila la build, crea lo ZIP, calcola lo SHA-256 e crea la Release;
4. le installazioni precedenti controllano il manifest all'avvio e ogni 15 minuti;
5. quando trovano una versione più recente mostrano il popup **Aggiorna e riavvia**;
6. il clic scarica il pacchetto, verifica lo SHA-256, installa i file, chiude Nebula e la riapre con la nuova versione.

Per pubblicare una nuova versione:

```powershell
# Prima committa le modifiche al codice.
# Poi, dalla radice del progetto:
.\scripts\release.cmd -Version 1.3.0
```

Se ometti `-Version`, viene incrementata la patch:

```powershell
.\scripts\release.cmd
```

Per creare commit e tag senza inviarli subito:

```powershell
.\scripts\release.cmd -Version 1.3.0 -NoPush
git push origin HEAD
git push origin v1.3.0
```

Il push del tag è l'evento che avvia la Release. In alternativa puoi avviare **Actions → Release Nebula → Run workflow** e inserire la versione; il workflow crea la Release e il tag.

## Primo setup GitHub

Crea un repository vuoto su GitHub (senza README, `.gitignore` o licenza) e collega questa cartella:

```powershell
git init -b main
git add .
git commit -m "Initial commit"
git remote add origin https://github.com/OWNER/REPO.git
git push -u origin main
```

Poi verifica in **Settings → Actions → General** che i workflow possano scrivere nel repository. Il workflow dichiara già `permissions: contents: write`; se l'organizzazione impone permessi restrittivi, abilita la scrittura per `GITHUB_TOKEN`.

La prima Release va creata con lo script o con **Run workflow**. Da quel momento l'URL `releases/latest/download/update.json` è disponibile.

## Cosa fa il workflow

Su ogni tag `vMAJOR.MINOR.PATCH`:

1. verifica che `<Version>` in `src/Nebula/Nebula.csproj` corrisponda al tag;
2. esegue `dotnet publish` in Release con `UpdateRepository=${{ github.repository }}` e la versione del tag;
3. crea `Nebula-win-x64.zip` con il contenuto di `publish/` nella radice;
4. calcola lo SHA-256;
5. genera `update.json` con `version`, `downloadUrl`, `sha256` e `notes`;
6. crea la Release e carica entrambi gli asset, impostandola come latest.

Il workflow è in `.github/workflows/release.yml`. Non richiede secret manuali: usa il `GITHUB_TOKEN` fornito da Actions.

## Cosa succede sul PC dell'utente

- Il controllo automatico parte **15 secondi dopo l'avvio** e si ripete ogni **15 minuti**.
- Se `AutoUpdate` è attivo e il manifest contiene una versione maggiore, Nebula mostra una finestra con versione installata, versione disponibile, note e i pulsanti **Più tardi** e **Aggiorna e riavvia**.
- **Aggiorna e riavvia** scarica lo ZIP, verifica lo SHA-256, estrae in staging e avvia uno script PowerShell nascosto.
- Lo script attende la chiusura di Nebula, copia i file nella cartella dell'app e riapre `Nebula.exe`.
- I file dell'utente in `%APPDATA%\Nebula` non vengono toccati.
- Se l'installazione è in `C:\Program Files` senza permessi di scrittura, la copia fallisce: installa Nebula in una cartella dell'utente oppure usa i permessi adatti. L'app non richiede UAC da sola.
- Se il PC è offline o GitHub non risponde, il controllo fallisce in silenzio e viene ritentato al ciclo successivo.

Il pulsante **Verifica ora** nello Studio esegue un controllo manuale e abilita **Scarica e installa** quando trova una versione. Il popup automatico viene mostrato una sola volta per versione in ogni sessione; **Più tardi** lo chiude.

Il log dell'applicazione è in `%LOCALAPPDATA%\Nebula\updates\apply-update.log` se la copia fallisce.

## Build locale e repository personalizzato

Una build locale non ha un repository GitHub incorporato, quindi il campo **Manifest aggiornamenti** in **Studio → Info** resta vuoto. Per provare gli aggiornamenti da un repository:

```powershell
.\.dotnet\dotnet.exe publish .\src\Nebula\Nebula.csproj -c Release -o .\dist `
  -p:Version=1.3.0 `
  -p:UpdateRepository=OWNER/REPO
```

In alternativa inserisci nel campo **Manifest aggiornamenti** un URL personalizzato, anche un manifest locale servito in HTTP per i test. Un URL personalizzato ha precedenza sul valore predefinito.

## Formato del manifest

```json
{
  "version": "1.3.0",
  "downloadUrl": "https://github.com/OWNER/REPO/releases/download/v1.3.0/Nebula-win-x64.zip",
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
- La build è framework-dependent: sul PC serve il **.NET 8 Desktop Runtime**.
- L'aggiornamento automatico richiede accesso HTTPS a GitHub e il permesso di scrivere nella cartella dell'app.
