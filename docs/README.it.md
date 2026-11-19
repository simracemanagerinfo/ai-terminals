# AI Terminals

Due applicazioni Windows indipendenti e portabili, con **motore ConPTY/xterm.js condiviso**.

| App | Identità | Funzionalità |
|---|---|---|
| `ClaudeTerminal.exe` | Arancione / terracotta | Claude Code, sidebar progetti e chat, token locali, sessioni parallele |
| `CodexTerminal.exe` | Grafite / teal | Codex CLI, sidebar progetti e chat, token locali, sessioni parallele |

I progetti in `src/claude/` e `src/codex/` compilano lo stesso codice `src/shared/` senza duplicare l'implementazione di ConPTY. Ogni app ha XAML, icona, cache WebView2, storico e processo Windows separati.

## Versione stabile

**Release stabile attuale: v1.1.0** (prima versione: v1.0.0). Due EXE Windows indipendenti, con identità grafica, cache, processi e cronologia separati. Il progetto `powershell-personalizzation` non viene modificato; qui non vengono aggiunti alias o profili globali.

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

Per installare o aggiornare entrambe le applicazioni, da una qualunque directory con PowerShell 7:
```powershell
$ErrorActionPreference = 'Stop'
$installer = Join-Path $env:TEMP 'Install-AITerminals.ps1'
Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/simracemanagerinfo/ai-terminals/main/scripts/Install-AITerminals.ps1' -OutFile $installer
& $installer -App Both
```

Lo script preferisce l'ultima **GitHub Release stabile**, verifica SHA-256 dei
pacchetti e aggiorna le due cartelle senza installazioni duplicate. Prima della
pubblicazione della prima release (o con `-RunId` esplicito) può usare gli
artifact GitHub Actions. Per le release stabili pubbliche non servono credenziali GitHub; per le anteprime GitHub Actions viene utilizzata l'autenticazione locale. Per aggiornare un solo programma:
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

## Avviso risposta completata (Claude e Codex)

- Entrambi i terminali distinguono il completamento della risposta dai normali pause/ritardi: Codex usa il suo evento `agent-turn-complete`, Claude usa l'hook nativo `Stop`.
- A finestra attiva **nessun popup**, ma evidenziazione della chat completata (arancione Claude, teal Codex). Il segnale sparisce quando apri o usi quella chat; non cancella gli indicatori delle altre.
- Se la finestra è in secondo piano o ridotta a icona compare un **popup WPF compatto, non modale e non attivante** nell'angolo basso destro dello schermo su cui si trova il terminale. Il popup dura circa 7 secondi; cliccandolo apre la conversazione.
- Il popup è interno all'applicazione, non una notifica registrata nel Centro notifiche di Windows. Sostituisce i vecchi balloon `NotifyIcon` che in alcune configurazioni non apparivano. Applicazioni in full-screen esclusivo possono comunque coprirlo.
- File di segnale temporanei separati per sessione, solo ID della notifica e mai prompt, risposte o token; cancellati alla chiusura.
- **Claude:** passa `--settings` alla sola istanza CLI avviata per aggiungere lo Stop hook; **Codex:** usa `-c notify=[...]` sulla sola istanza. Non vengono modificati i file `~/.claude/settings.json` o `~/.codex/config.toml`. Eventuali configurazioni personali per gli stessi hook possono essere sovrascritte solo per la sessione del terminale: verificare prima del merge.

## Privacy

L'app legge transcript JSONL locali solo per storico e statistiche di token; **non** trasferisce conversazioni, chiavi API o credenziali al repository. Le statistiche locali non rappresentano quota residua o fatturazione ufficiale.

L'icona Codex in questa fase è un marchio grafico personalizzato grafite/teal, non il logo ufficiale di OpenAI.


## Cartelle virtuali e internazionalizzazione

- **Secchio** è sempre l'ultima cartella e resta inizialmente chiuso. Spostare una conversazione qui la nasconde senza toccare transcript JSONL o indici. Si può ripristinare nella cartella precedente.
- Le cartelle sono **virtuali per progetto** e vengono salvate in `%LOCALAPPDATA%\PowerShellCustomization\ai-terminals-claude-folders.json` oppure `ai-terminals-codex-folders.json`, mai nella cartella di codice del progetto.
- Per creare una cartella usa `+ Cartella`; clicca una cartella per espandere/ridurre. Con il tasto destro su una cartella puoi rinominarla o rimuoverla (le chat tornano fra quelle non organizzate).
- Con il tasto destro su una chat dello storico puoi spostarla in una cartella, nel Secchio o ripristinarla.
- Puoi anche trascinare una conversazione dello storico sopra una cartella (drag & drop). Dal tasto destro sulla cartella scegli **Nuova conversazione qui** per avviarla direttamente in quella categoria. Per Claude il collegamento è immediato; per Codex viene assegnato al completamento della prima risposta, quando si conosce il vero ID della sessione. Le sessioni in esecuzione rimangono nella sezione dedicata; chiudile prima di organizzarle.
- **Lingua**: selettore in alto a sinistra, English/Italiano; inizialmente segue la lingua di Windows, inglese come fallback. La preferenza è condivisa tra i due terminali e sopravvive al riavvio.
- Nessun archivio o transcript viene eliminato fisicamente.
- Funzioni incluse in v1.1.0, con codice condiviso e installazione negli stessi due percorsi.
