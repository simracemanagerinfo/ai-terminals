# AI Terminals

Due applicazioni Windows indipendenti e portabili, con **motore ConPTY/xterm.js condiviso**.

| App | Identità | Funzionalità |
|---|---|---|
| `ClaudeTerminal.exe` | Arancione / terracotta | Claude Code, sidebar progetti e chat, token locali, sessioni parallele |
| `CodexTerminal.exe` | Grafite / teal | Codex CLI, sidebar progetti e chat, token locali, sessioni parallele |

I progetti in `src/claude/` e `src/codex/` compilano lo stesso codice `src/shared/` senza duplicare l'implementazione di ConPTY. Ogni app ha XAML, icona, cache WebView2, storico e processo Windows separati.

## Stato

**Branch di migrazione:** `feature/split-claude-codex`. Il vecchio terminale funzionante installato dall'utente non viene modificato, e `powershell-personalizzation/main` non viene toccato. La pubblicazione di una release con due EXE è subordinata ai test Windows e alla prova manuale.

## Build Windows

GitHub Actions compila le due versioni con .NET 10, Node 22 e xterm.js. I pacchetti portabili sono pubblicati come due artifact separati: `ClaudeTerminal-win-x64` e `CodexTerminal-win-x64`. Non serve installare SDK, npm o privilegi amministrativi sul PC di utilizzo. WebView2 Runtime e le rispettive CLI devono essere presenti.

Per una build locale di sviluppo con gli strumenti già disponibili:

```powershell
Push-Location src/shared/TerminalUi
npm install
npm run test:keyboard
npm run build
Pop-Location
dotnet publish src/claude/ClaudeTerminal.csproj -c Release -r win-x64 --self-contained true -o out/ClaudeTerminal
dotnet publish src/codex/CodexTerminal.csproj -c Release -r win-x64 --self-contained true -o out/CodexTerminal
```

## Installazione o aggiornamento da GitHub

Non si scaricano ZIP manualmente e non si creano alias PowerShell.

Durante il collaudo **non sostituire Claude Terminal**, che è già funzionante:
```powershell
& '.\scripts\Install-AITerminals.ps1' -App Codex
```

Lo script seleziona l'ultima GitHub Actions Windows riuscita, usando `gh auth`
o Git Credential Manager già configurato. Scarica il pacchetto Codex
in una directory temporanea, verifica i file e lo installa in
`%LOCALAPPDATA%\PowerShellCustomization\CodexTerminal`.

Dopo il collaudo e la release, per entrambe le applicazioni:
```powershell
& '.\scripts\Install-AITerminals.ps1' -App Both
```

La directory di Claude rimane quella esistente
(`%LOCALAPPDATA%\PowerShellCustomization\ClaudeTerminal`);
il programma non tocca i dati persistenti di WebView2 né i transcript.
In presenza di processi attivi o di una cartella sconosciuta interrompe
l'operazione senza cancellare nulla.

## Prova richiesta prima della release

- Avvio delle due finestre contemporaneamente.
- Sessioni multiple, storico e cambio progetto dentro ogni app.
- Icona corretta per ciascun EXE e barra delle applicazioni.
- `Shift+Invio` dentro Claude, copia/incolla e popup token.
- Aggiornamento in-place senza cartelle residue.

## Privacy

L'app legge transcript JSONL locali solo per storico e statistiche di token; **non** trasferisce conversazioni, chiavi API o credenziali al repository. Le statistiche locali non rappresentano quota residua o fatturazione ufficiale.

L'icona Codex in questa fase è un marchio grafico personalizzato grafite/teal, non il logo ufficiale di OpenAI.
