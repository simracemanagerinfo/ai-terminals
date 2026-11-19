# Meeting Notes — stato implementazione

Aggiornamento: 2026-11-19

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

## Prossime fasi (NON ancora implementate)
- UI MeetingNotes.exe autonoma con import TXT, generazione e anteprima.
- Registrazione audio Windows (WASAPI) e trascrizione locale STT.
- Sicurezza avanzata: cifratura, conservazione automatica, gestione consensi.
- Eventuale packaging, release separata e internazionalizzazione completa.

## Come verificare sul PC
Vedere docs/meeting-notes/README.md. I test offline non richiedono token
o account IA. L'esecuzione reale richiede autorizzazione specifica all'invio
della trascrizione al provider e revisione umana della minuta prodotta.
