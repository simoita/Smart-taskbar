# Icone di sistema

La pagina **Studio → Icone** cambia le icone di Windows per il profilo dell'utente, non solo quelle della dock. Non modifica file in `System32` e non scrive in `HKLM`.

## Superfici modificate

| Superficie | Come viene cambiata | Dove si vede |
| --- | --- | --- |
| Collegamenti `.lnk` | proprietà `IconLocation` del collegamento | Desktop, menu Start e cartelle del profilo |
| Collegamenti fissati alla taskbar | stessa proprietà dei `.lnk` in `Quick Launch\User Pinned\TaskBar` | icone dei pin |
| Tipi di file | valore predefinito del **ProgID effettivo** sotto `HKCU\Software\Classes\<ProgID>\DefaultIcon` (fallback: estensione) | icone dei file in Explorer |
| Cartelle utente | `desktop.ini` nella cartella, con `IconResource` verso l'ICO generato | Desktop, Documenti, Download, Immagini, Musica, Video |

Nebula rileva i `.lnk` del Desktop, del menu Start (anche nelle sottocartelle) e dei pin della taskbar. Per i tipi di file usa il **ProgID** dichiarato dall'estensione, non la chiave `.ext\DefaultIcon` che Explorer spesso ignora; include un catalogo di estensioni comuni e le estensioni che hanno già un `DefaultIcon` personale in `HKCU`. Le estensioni senza un'icona risolvibile vengono omesse invece di ricevere un'icona casuale.

Per le **cartelle utente** Nebula usa il `desktop.ini` della cartella, l'unica superficie per-utente che Explorer applica davvero su Windows 11. Sono incluse le sei cartelle shell del profilo quando esistono. Il file originale viene letto e salvato byte per byte prima di aggiungere o sostituire `IconResource`; le altre righe (`LocalizedResourceName`, sezioni di rete, ecc.) restano intatte durante l'applicazione e vengono comunque ripristinate dal backup.

Le chiavi globali `HKCU\Software\Classes\Folder\DefaultIcon` e `Explorer\Shell Icons` sono state provate e **ignorate** da Explorer anche dopo riavvio e ricostruzione della cache: Nebula non le usa e non finge di cambiare tutte le cartelle. Una cartella arbitraria fuori dalle sei superfici si personalizza da Windows con **Proprietà → Personalizza → Cambia icona** (`desktop.ini`).

## Stili disponibili

Il selettore propone:

- **Originale** — rimuove la personalizzazione della voce selezionata.
- **Vetro** — lastra scura traslucida con bordo luminoso.
- **Pieno** — piastra piena con gradiente dell'accento.
- **Riquadro** — piastra compatta.
- **Cerchio** — piastra circolare.
- **Duotono** — icona ricolorata nei due toni dell'accento.
- **Mono** — icona monocromatica.
- **Contorno** — sagoma luminosa attorno all'icona.
- **Neon** — alone e bordo acceso.

La forma della piastra è scelta separatamente: **arrotondata**, **squircle**, **cerchio** o **quadrata**. Lo stile **Cerchio** forza una piastra circolare.

## Icone personali

Per una singola voce, **Scegli icona → Carica un'icona…** apre il dialog di Windows e accetta `.ico`, `.png`, `.jpg`, `.jpeg` ed `.exe`.

- Le immagini vengono caricate alla risoluzione maggiore disponibile, ritagliate nella forma scelta e ricolorate secondo lo stile.
- Un `.exe` (o una DLL) viene letto tramite l'API delle icone della shell, con indice del gruppo di icone.
- Il file originale **non viene spostato**: Nebula genera una copia `.ico` nella propria cartella e applica quella.
- L'icona personale è applicabile a una voce alla volta; per evitare un effetto imprevisto, il pulsante non la propaga a tutte le voci.

### Perché le dimensioni vengono generate su disco

Explorer chiede icone a misure diverse (16, 20, 24, 32, 40, 48, 64, 96, 128 e 256 px). Nebula crea un ICO multi-frame con **tutte** queste misure:

- frame piccoli in DIB 32 bit con maschera AND;
- frame da 256 px in PNG;
- ogni frame è disegnato alla sua dimensione, non è un'unica immagine scalata al volo.

In questo modo l'icona non si sgrana né si deforma quando Explorer la mostra a 16 px nella barra del titolo o a 256 px nella vista extra-large.

## Applicare e ripristinare

1. Apri **Studio → Icone**.
2. Scegli **stile** e **forma**.
3. Per una voce premi **Scegli icona**; per tutte premi **Applica a tutte**.
4. Per tornare indietro premi **Ripristina voce** accanto a una riga, oppure **Ripristina originali**.

Il ripristino non è una "rigenerazione": usa il backup salvato prima della modifica.

- **Collegamenti:** confronta la stringa `IconLocation` esatta, compreso indice e variabili d'ambiente, e la riscrive.
- **Registro:** salva esistenza della chiave, esistenza del valore, tipo (`String`, `ExpandString`, `DWord`, `QWord`, `Binary`, `MultiString`) e dati. Dopo il ripristino rilegge il valore e lo confronta con l'originale.
- **Cartelle:** salva i byte esatti di `desktop.ini` (se esiste) e gli attributi del file e della cartella; il ripristino riscrive i byte, riapplica gli attributi e confronta il contenuto letto con il backup. Se `desktop.ini` non esisteva, viene rimosso solo il file creato da Nebula.
- **Chiavi create:** se `DefaultIcon` (o una chiave padre) non esisteva, viene rimossa dopo il ripristino solo se è vuota e non conteneva altri valori o sottochiavi.
- **Verifica:** ogni voce viene riletta e verificata una per una; se una fallisce, il backup resta e il messaggio indica quante voci non sono state ripristinate.

## Backup, journal e chiusura forzata

Il backup si trova in:

```
%APPDATA%\Nebula\system-icons\backup.json
```

Le icone generate sono in `%APPDATA%\Nebula\system-icons\icons\`. La cartella `staging` è usata solo per file temporanei. Il journal contiene anche i byte base64 del `desktop.ini` originale e gli attributi di file e cartella.

Prima della prima modifica Nebula scrive il journal con `Completed: false`. Dopo ogni voce aggiorna il backup e, solo alla fine, lo marca `Completed: true`.

- **Uscita normale:** vengono rimossi i temporanei; il backup e le icone applicate restano perché la personalizzazione è stata richiesta dall'utente.
- **`taskkill /F` o crash durante l'applicazione:** al successivo avvio il journal incompleto viene letto e le voci vengono ripristinate ai valori originali. Se il ripristino automatico non riesce, il backup non viene cancellato e l'errore viene scritto in `%APPDATA%\Nebula\app.log`.
- **`taskkill /F` dopo un'applicazione completata:** il backup è completo e valido; al riavvio l'app non annulla una personalizzazione volontaria. Non restano file `.tmp` o staging. Per annullarla usare **Ripristina originali**.

Non esiste un ripristino automatico all'uscita normale: sarebbe incompatibile con un'app di personalizzazione persistente. La rimozione completa si fa dal pulsante di ripristino o disinstallando Nebula dopo aver ripristinato le icone.

## Cosa non è possibile o non viene fatto

- **Nessuna patch dei binari di sistema.** Non viene modificato `shell32.dll`, `imageres.dll` o un eseguibile in `System32`. Sarebbe una modifica globale, fragile e non reversibile per profilo.
- **Nessuna scrittura in `HKLM`.** Le associazioni sono per-utente. Un'altra utenza del PC non vede le icone.
- **App UWP/Store, app protette o elevate:** la shell può ignorare l'icona del collegamento o gestire l'icona tramite il pacchetto dell'app. Nebula non forza il processo.
- **Associazioni senza `DefaultIcon` risolvibile:** vengono saltate. Alcune app registrano l'icona solo nel proprio eseguibile o in una chiave che Windows non usa per il tipo file.
- **Tutte le cartelle del disco:** Windows 11 non ha un override globale per-utente dell'icona delle cartelle; `Folder\DefaultIcon` e `Shell Icons` vengono ignorati. Nebula copre le sei cartelle shell del profilo con `desktop.ini`; ogni altra cartella si personalizza singolarmente da Windows. Non viene toccato `desktop.ini` di cartelle fuori da quelle sei.
- **Associazioni UWP/Store:** se Windows usa un ProgID `AppX...` per l'apertura, l'icona visibile può essere quella del pacchetto e non quella classica. Nebula modifica il ProgID classico quando esiste; per le associazioni puramente UWP l'icona può restare invariata. Questo limite è mostrato dal ProgID nella riga e non viene nascosto.
- **Cache di Explorer:** dopo una modifica Nebula invia `SHChangeNotify` per la voce e le associazioni. In alcuni casi Explorer tiene in cache l'icona fino a un refresh (`F5`), alla riapertura della finestra o al riavvio di Explorer. Il file su disco, il registro e `desktop.ini` sono già cambiati.
- **Collegamenti in cartelle protette o di sola lettura:** vengono rifiutati; il backup non viene perso e il messaggio lo segnala.
- **Icone personali con trasparenza parziale o formati non supportati:** `.ico`, `.png`, `.jpg`, `.jpeg` ed `.exe` sono i formati accettati. WebP, SVG e GIF non sono usati come sorgente personale.
- **EXE con icona non quadrata:** Windows espone l'icona associata a un `.exe` tramite la shell e la normalizza a un bitmap quadrato (32×32 nell'estrazione shell). Un'icona 2:1 incorporata in un EXE perde quindi il rapporto d'aspetto prima di arrivare a Nebula; per un'icona wide usare un `.png` personale. Le normali icone quadrate degli eseguibili funzionano.
- **Un solo stile per volta per la voce:** non esiste una coda di varianti; l'ultima applicazione sostituisce la precedente e il backup originale resta lo stesso.

## Verifiche eseguite

Sul binario pubblicato, con un collegamento di prova sul Desktop, un file `.jpg`, un `.zip` e cartelle reali:

- applicato **Neon** al `.lnk`: `IconLocation` è cambiata nel file, Explorer ha mostrato la piastra e la freccia di scorciatoia dopo il refresh;
- applicato **Cerchio** e **Neon** al tipo `.zip`: la chiave efficace è stata creata in `HKCU\Software\Classes\CompressedFolder\DefaultIcon`, non in `.zip\DefaultIcon`; Explorer è passato dall'icona ZIP gialla alla piastra;
- applicato **Pieno** a `.jpg`: `HKCU\Software\Classes\jpegfile\DefaultIcon` è stato creato e l'icona di Explorer è cambiata;
- caricato un `.png` personale dal dialog di Windows: percorso copiato nel campo, file convertito in ICO, applicato a un `.lnk` e alla cartella **Desktop**; l'anteprima shell ha mostrato il triangolo magenta e la freccia di scorciatoia, non l'icona generica;
- verificati **10 frame** nell'ICO (16–256 px) e il frame 256 in PNG;
- ripristinato il `.lnk`: `IconLocation` è tornata esattamente al valore precedente (vuoto, cioè `,0` per il collegamento di prova);
- ripristinato `.zip`: `CompressedFolder\DefaultIcon` è tornata assente e Explorer ha rimostrato l'icona ZIP gialla;
- ripristinato `.jpg`: valore e chiave `DefaultIcon` sono tornati assenti;
- applicata **Cartella Download**: `desktop.ini` ha mantenuto `LocalizedResourceName` e aggiunto `IconResource`; Explorer ha mostrato la piastra Neon; il ripristino ha rimesso il file originale con `IconResource=%SystemRoot%\system32\imageres.dll,-184` e gli attributi `ReadOnly` della cartella;
- applicata la stessa icona personale alla **Cartella Desktop**: `desktop.ini` ha conservato `LocalizedResourceName`, l'icona è cambiata in Explorer e il ripristino l'ha riportata all'originale byte per byte;
- applicata un'icona personale da un **vero `.exe`** (`C:\Windows\System32\notepad.exe`) a un `.lnk`, al tipo `.zip` e alla **Cartella Download**; l'ICO condiviso generato conteneva **10 frame** da 16 a 256 px (ratio contenuto 1.000, con 128×127 sul frame 128), Explorer ha mostrato l'icona notepad su tutte e tre le superfici dopo `SHChangeNotify`, e il ripristino ha riportato `IconLocation=,0`, `CompressedFolder\DefaultIcon` assente e il `desktop.ini` di Download allo SHA-256 originale `B029393E…98D3`, con attributi `Hidden, System, Archive`;
- applicata e ripristinata un'icona `.png` wide (2:1) a `.lnk`, `.zip` e **Cartella Download**: i 10 frame hanno mantenuto un rapporto contenuto tra 1.75 e 1.98 senza deformazioni; il test con EXE wide ha confermato che Windows normalizza l'icona a un bitmap quadrato prima dell'estrazione, limite documentato sopra;
- applicate e ripristinate **156 voci** con **Applica a tutte** (29 collegamenti, 121 superfici di registro, 6 cartelle utente), senza errori;
- interrotto un ripristino e riavviata l'app: il journal incompleto ha riportato le voci allo stato originale (percorso di recovery condiviso con le cartelle);
- uscita normale e `taskkill /F`: nessun processo Nebula residuo, nessun file `.tmp`, backup coerente;
- dopo il ripristino completo: `backup.json` assente e cartella `icons` vuota.
