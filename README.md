# AI Terminals

Due applicazioni Windows indipendenti e portabili, con **motore ConPTY/xterm.js condiviso**.

| App | Identità | Funzionalità |
|---|---|---|
| `ClaudeTerminal.exe` | Arancione / terracotta | Claude Code, sidebar progetti e chat, token locali, sessioni parallele |
| `CodexTerminal.exe` | Grafite / teal | Codex CLI, sidebar progetti e chat, token locali, sessioni parallele |

I progetti in `src/claude/` e `src/codex/` compilano lo stesso codice `src/shared/` senza duplicare l'implementazione di ConPTY. Ogni app ha XAML, icona, cache WebView2, storico e processo Windows separati.

## Versione stabile

**Release stabile attuale: v1.0.1** (prima versione: v1.0.0). Due EXE Windows indipendenti, con identità grafica, cache, processi e cronologia separati. Il progetto `powershell-personalizzation` non viene modificato; qui non vengono aggiunti alias o profili globali.

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

Per installare o aggiornare entrambe le applicazioni, usando soltanto GitHub:
```powershell
& '.\scripts\Install-AITerminals.ps1' -App Both
```

Lo script preferisce l'ultima **GitHub Release stabile**, verifica SHA-256 dei
pacchetti e aggiorna le due cartelle senza installazioni duplicate. Prima della
pubblicazione della prima release (o con `-RunId` esplicito) può usare gli
artifact GitHub Actions. Usa le credenziali GitHub locali già disponibili,
senza richiedere download ZIP manuali. Per aggiornare un solo programma:
`-App Claude` oppure `-App Codex`.

La directory di Claude rimane quella esistente
(`%LOCALAPPDATA%\PowerShellCustomization\ClaudeTerminal`);
il programma non tocca i dati persistenti di WebView2 né i transcript.
In presenza di processi attivi o di una cartella sconosciuta interrompe
l'operazione senza cancellare nulla.

## Funzionalità verificate su Windows

- Avvio delle due finestre contemporaneamente.
- Sessioni multiple, storico e cambio progetto dentro ogni app.
- Icona corretta per ciascun EXE e barra delle applicazioni.
- **Claude:** `Shift+Invio` invia LF (`Ctrl+J`) per una nuova riga.
- **Codex:** `Shift+Invio`, `Ctrl+Invio` e `Ctrl+J` generano un evento Windows `Ctrl+J` tramite ConPTY. Codex non sempre riconosce il semplice byte LF su Windows.
- `Invio` resta dedicato all'invio; `Ctrl+C` e `Ctrl+V` mantengono il comportamento standard. Le scorciatoie browser di WebView2 vengono disabilitate **solo in Codex Terminal**, preservando gli appunti.
- **Titoli Claude:** lo storico preferisce l'ultima rinomina `/rename`, poi il titolo `ai-title`, poi il riepilogo disponibile e infine la prima domanda. Legge i metadati locali senza modificare le conversazioni.
- **Titoli Codex:** l'app legge `~/.codex/session_index.jsonl` e preferisce i nomi assegnati tramite `/rename`. Altrimenti legge il primo messaggio `response_item` o `event_msg` nel transcript. Le conversazioni non vengono modificate.
- GitHub Actions verificano le combinazioni nel renderer e compilano entrambi gli EXE. Il test basso livello `ReadConsoleInputW` non è eseguibile in modo affidabile sul runner senza desktop: il comportamento nella TUI Codex va confermato sul PC Windows.
- Aggiornamento in-place senza cartelle residue.

## Anteprima: avviso risposta completata (Codex)

- Quando Codex termina un turno con l'evento nativo `agent-turn-complete`, il terminale contrassegna la conversazione con un indicatore e un bordo colorato.
- Se Codex Terminal non è in primo piano o è minimizzato, Windows riceve una notifica tramite l'area di notifica (`NotifyIcon`). La notifica non attiva la finestra; cliccandola si può tornare alla conversazione.
- Se la finestra è in primo piano, non ci sono popup: resta solo l'evidenziazione della chat.
- L'indicatore resta visibile finché selezioni o usi quella conversazione. Le altre chat non vengono azzerate.
- Si usano segnali temporanei separati per ogni sessione, senza leggere il testo delle risposte o creare notifiche per vecchie conversazioni.
- La CLI Codex viene avviata con un parametro `-c notify=[...]` **solo per il processo lanciato dal terminale**. Non viene modificato `~/.codex/config.toml`; tuttavia, un eventuale `notify` già configurato dall'utente viene **sostituito per quella specifica sessione**. Valutare questo vincolo prima di integrare il ramo nella release stabile.
- Le notifiche dipendono dalle impostazioni di Windows, compresa la modalità Non disturbare.

## Privacy

L'app legge transcript JSONL locali solo per storico e statistiche di token; **non** trasferisce conversazioni, chiavi API o credenziali al repository. Le statistiche locali non rappresentano quota residua o fatturazione ufficiale.

L'icona Codex in questa fase è un marchio grafico personalizzato grafite/teal, non il logo ufficiale di OpenAI.
