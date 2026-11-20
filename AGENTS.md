# Istruzioni per gli agenti

## Regola fissa: ogni compito finisce con aggiornamento, commit, push e pubblicazione

Quando un compito che cambia il comportamento dell'app è concluso:

1. **Aggiorna la versione** in `src/Nebula/Nebula.csproj` (`<Version>`, es. `1.6.8`) e aggiungi in testa al `README.md` il paragrafo `**Novità 1.6.8:** …` con una frase su cosa cambia per l'utente.
2. **Build Release** dalla radice del repo:
   ```bash
   ./.dotnet/dotnet.exe build ./src/Nebula/Nebula.csproj -c Release
   ```
   Deve finire con 0 avvisi e 0 errori. Se il cambio è a runtime, verifica anche il flusso toccato (smoke test o `Nebula.exe --selftest`).
3. **Due commit**: prima il codice (messaggio in italiano che spiega il perché), poi `chore(release): vX.Y.Z` con il solo bump di `<Version>`. La release GitHub prende le note da `git log -1 --skip=1`, quindi le note utente devono stare nel commit di codice.
4. **Tag e push**:
   ```bash
   git tag vX.Y.Z
   git push origin main
   git push origin vX.Y.Z
   ```
5. **Pubblica e verifica**: il tag avvia il workflow `Release Nebula` (`.github/workflows/release.yml`), che controlla `<Version>` == tag e pubblica `Nebula-Setup-X.Y.Z-x64.exe`, `Nebula-win-x64.zip` e `update.json`. Controlla che il run sia `success` e che gli artefatti siano coerenti: `update.json` con la versione giusta, sha256 uguale a quello dello ZIP scaricato, `FileVersion` di `Nebula.exe` == `X.Y.Z.0`.

Le modifiche che non toccano l'app (solo documentazione o queste istruzioni) si chiudono con commit e push, senza nuova release.

## Convenzioni del repo

- Interfaccia, testi, commit e documentazione in italiano.
- Nessun footer `Generated with …` né `Co-Authored-By` nei messaggi di commit.
- `gh` non è installato: per GitHub usa `curl https://api.github.com/…`.
- Non modificare `installer/` o `.github/workflows/release.yml` senza motivo: la verifica della versione e la generazione delle note dipendono da loro.
- La sessione desktop può essere bloccata: se non puoi guardare lo schermo, dichiaralo come limite invece di affermare che la resa visiva è corretta.
