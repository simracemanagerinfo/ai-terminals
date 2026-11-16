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

## Privacy

L'app legge transcript JSONL locali solo per storico e statistiche di token; **non** trasferisce conversazioni, chiavi API o credenziali al repository. Le statistiche locali non rappresentano quota residua o fatturazione ufficiale.

L'icona Codex in questa fase è un marchio grafico personalizzato grafite/teal, non il logo ufficiale di OpenAI.
