# Meeting Notes — stato implementazione

Aggiornamento: 2026-11-23

## Ambito e isolamento
- Repo: simracemanagerinfo/ai-terminals
- Branch: feature/meeting-notes
- Cartella di lavoro Windows scelta dall'utente:
  C:\Users\Overdata\Documents\workspace\personale\ai-terminals-voice
- main e i due terminali esistenti rimangono invariati.
- Nessun merge e nessuna release stabile modificata.

## Fase 1 — MVP PowerShell: IMPLEMENTATA
- Input: file TXT locale UTF-8 (massimo 2 MiB).
- Provider: Claude CLI / Codex CLI e modalità Mock priva di rete.
- Chiamata del provider in un processo dedicato via PowerShell 7.
- Trascrizione passata su stdin; non è parte degli argomenti del processo.
- Timeout, controllo exit code e risposta JSON obbligatoria.
- Output JSON e HTML con CSS interno, escape e CSP.
- Minuta: sintesi, argomenti, decisioni e relativo stato, attività,
  responsabili/scadenze, questioni aperte, cronologia.
- Stile: italiano tecnico controllato ispirato ai principi ASD-STE100,
  default 80%, senza dichiarazione di conformità allo standard inglese.
- Cartella di output univoca per evitare sovrascritture.
- Flag esplicito -AllowExternalProcessing per evitare chiamate involontarie.
- .gitignore ampliato per evitare commit di registrazioni e minute comuni.
- Test con dati sintetici, inclusi fake claude.cmd e codex.cmd.

## Verifiche automatiche
GitHub Actions su Windows: **PASS** alla run 44538607931 del 2026-11-19.
URL: https://github.com/simracemanagerinfo/ai-terminals/actions/runs/44538607931

Controlli: parsing di tre script PowerShell, produzione HTML/JSON,
escaping, gestione UTF-8, cartelle univoche, blocco chiamate senza
consenso, input/JSON non validi, subprocess mock per entrambi i CLI.

Le chiamate reali ai modelli non vengono effettuate in CI e restano
da collaudare sul PC aziendale con account e policy autorizzati.

## Layout Outlook (2026-11-19)
- Il documento minutes.html ha ora un corpo email con paragrafi ed elenchi semplici,
  senza banner, card, presentazione o controlli interattivi.
- Tabella attività con colonne Attività, Responsabile, Scadenza.
- Stili inline compatibili con il copia/incolla in Outlook, senza risorse esterne.
- Generato anche minutes.txt, versione testuale di fallback.
- Per provarlo occorre generare un nuovo verbale; i file precedenti non cambiano.

## Fase 2 — UI Windows: IMPLEMENTATA (v0.1, da provare su desktop)
- Progetto WPF separato src/meetings/MeetingNotes.csproj (net10.0-windows).
- Import TXT, titolo riunione, provider Claude/Codex/Mock, stile italiano,
  consenso esplicito prima di invio non locale, generazione/cancellazione.
- Anteprima testuale, apertura HTML, apertura cartella, copia testo,
  copia HTML direttamente negli appunti Windows per Outlook.
- PowerShell invocato tramite processo senza shell interpolation e con
  limite di 10 minuti; output JSON machine-readable con flag -AsJson.
- Copia CF_HTML con offset UTF-8 verificati, HTML senza script esterni.
- Packaging autonomo MeetingNotes-win-x64 tramite pipeline Windows isolata.
- Installer GitHub CLI dedicato, senza toccare installer degli altri terminali.

Verifiche automatizzate: Windows build, PowerShell, CLI mock e test offsets CF_HTML.

Verifica finale della build con registrazione audio e installer senza gh:
GitHub Actions run 45135664143, risultato PASS.
https://github.com/simracemanagerinfo/ai-terminals/actions/runs/45135664143
Controlli: parser PowerShell, test simulati, build .NET WPF, clipboard UTF-8,
artifact MeetingNotes-win-x64 e download dell'artifact tramite API GitHub
con GITHUB_TOKEN, senza gh. Microfono e output WASAPI restano da collaudare
su una postazione Windows con hardware e permessi reali.

Build GUI e artifact dedicato: run GitHub Actions 45133254586,
https://github.com/simracemanagerinfo/ai-terminals/actions/runs/45133254586
Pacchetto: MeetingNotes-win-x64 (autonomo, non incluso nelle release stabili).
Resta da confermare l'uso con GUI, login CLI reale e Outlook desktop sul PC
aziendale. Non dichiarare completo il collaudo E2E prima di questi riscontri.

## Fase 3a — Test registrazione locale: IMPLEMENTATA, COLLAUDO DEVICE DA FARE
- NAudio 3.3 (WASAPI) integrato nel progetto WPF autonomo.
- Nell'interfaccia si possono selezionare microfono e audio del PC.
- Vengono creati WAV distinti nella cartella locale recordings.
- Richiesta conferma prima di registrare; nessun upload o uso del modello.
- Registrazione limitata a 10 minuti per ogni collaudo e stop manuale.
- Per verificare dispositivi e contenuto dei file serve un PC Windows fisico,
  GitHub Actions non può simulare microfono e loopback in una sessione desktop reale.
- Non presente la trascrizione locale WAV → TXT (step successivo).

## Installer migliorato
- Non richiede più gh: usa GITHUB_TOKEN/GH_TOKEN, gh opzionale o credenziali
  git credential fill (Git Credential Manager), già disponibili con git pull.
- Scarica artifacts dal workflow Windows tramite API GitHub, con stage/backup.
- Senza credenziali prova dotnet publish, se il .NET SDK è disponibile.
- Installazione sul PC aziendale ancora da verificare realmente.

## Cartella di lavoro e TXT manuale (2026-11-23)

- Aggiunta cartella di lavoro selezionabile nella GUI e memorizzata in
  %LOCALAPPDATA%\AI-Terminals\MeetingNotes\workspace-settings.json.
- Sottocartelle: registrazioni/, trascrizioni/, minute/.
- Audio WAV e HTML/JSON/TXT della minuta vengono creati nella cartella scelta.
- Editor testo: import TXT, crea nuovo testo, modifica e salva un TXT UTF-8.
- Genera minuta salva prima uno snapshot univoco del testo corrente, per evitare
  sovrascritture o invii di una versione diversa da quella visibile.
- Non sposta i file prodotti prima dell'aggiornamento.
- Rifiuta cartelle interne a repository o worktree Git, per ridurre il rischio
  di commit accidentali di dati riservati.
- Test Windows della struttura cartelle, caratteri italiani, nomi univoci e
  protezione Git inseriti nella suite MeetingNotesSmoke.
- La trascrizione automatica WAV → TXT NON è ancora implementata; il testo
  salvato tramite editor è inserito/importato manualmente.

### Test della cartella di lavoro

GitHub Actions Windows: PASS, run 45137832801.
https://github.com/simracemanagerinfo/ai-terminals/actions/runs/45137832801

Verificati: compilazione WPF autonoma, test PowerShell e mock, formattazione
per Outlook, test TXT UTF-8/cartelle/nome univoco/blocco Git e download installer
via GitHub API senza dipendenza da gh.
Restano da verificare manualmente sul PC aziendale il selettore cartella,
l'editor e la copia TXT, nonché la cattura audio su dispositivi reali.

## Fase 3b — Trascrizione offline CPU: implementata, test inferenza reale da fare

- Aggiunta app console indipendente src/meetings/transcriber/MeetingTranscriber.csproj,
  pubblicata nel pacchetto portabile sotto transcriber/MeetingTranscriber.exe.
- Whisper.net 1.9.1 + Whisper.net.Runtime CPU + NAudio 3.3: nessun servizio
  esterno durante l'elaborazione. Non serve GPU.
- Convertitore WAV multicanale/48 kHz -> PCM 16 kHz mono; test self-test
  con dati audio sintetici senza scaricare modelli.
- Microfono e audio del PC vengono trascritti separatamente e poi riordinati
  per timestamp approssimativo. Etichette per sorgente, non vera diarizzazione.
- GUI: selezione file WAV/cartella, modello GGML multilingua (.bin),
  "Trascrivi offline", annullamento e TXT nell'editor della cartella di lavoro.
- Modello ggml-base.bin scaricabile una sola volta con
  scripts/meetings/Install-WhisperModel.ps1, esecuzione esplicita e controllo SHA256.
  Il modello non viene committato e non è necessario per la build CI.
- L'audio e le trascrizioni restano in locale durante lo STT; solo il successivo
  "Genera minuta" può inviare il TXT al provider Claude/Codex previa conferma.
- Limiti: qualità da provare con voci italiane su CPU aziendale, modello e
  runtime VC++ disponibili, differenze di sincronizzazione/eco tra sorgenti;
  STT non parte automaticamente dopo il pulsante Ferma.

### Collaudo CI fase 3b (2026-11-23)

- GitHub Actions run **45141323188: PASS**.
- https://github.com/simracemanagerinfo/ai-terminals/actions/runs/45141323188
- Verificati su Windows: PowerShell scripts, minute mock, pubblicazione WPF
  e transcriber CPU, self-test normalizzazione WAV, appunti Outlook,
  test workspace e download dell'artefatto mediante installer senza gh.
- Artefatto: MeetingNotes-win-x64, contenente MeetingTranscriber.exe.
- **Non testata in CI l'inferenza con modello reale né l'accuratezza su audio italiano**:
  primo collaudo necessario sul PC aziendale dell'utente.
## Prossime fasi (NON ancora implementate)
- Collaudo su PC aziendale di Whisper CPU con modello reale, accuratezza e prestazioni.
- Sicurezza avanzata: cifratura, conservazione automatica, gestione consensi.
- Eventuale packaging, release separata e internazionalizzazione completa.

## Come verificare sul PC
Vedere docs/meeting-notes/README.md. I test offline non richiedono token
o account IA. L'esecuzione reale richiede autorizzazione specifica all'invio
della trascrizione al provider e revisione umana della minuta prodotta.
