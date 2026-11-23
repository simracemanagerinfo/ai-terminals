# Meeting Notes — MVP PowerShell

Versione iniziale sviluppata esclusivamente su **feature/meeting-notes**.
Nessuna modifica alle applicazioni ClaudeTerminal.exe e CodexTerminal.exe.

## Installazione nella cartella Windows dedicata

Aprire PowerShell:

~~~powershell
Set-Location 'C:\Users\Overdata\Documents\workspace\personale'
git clone --branch feature/meeting-notes https://github.com/simracemanagerinfo/ai-terminals.git ai-terminals-voice
Set-Location '.\ai-terminals-voice'
~~~

Se la cartella esiste già e contiene un clone corretto, usare:

~~~powershell
git switch feature/meeting-notes
git pull --ff-only origin feature/meeting-notes
~~~

In seguito, per aggiornare dalla stessa cartella:

~~~powershell
git pull --ff-only origin feature/meeting-notes
~~~

## Primo test senza dati aziendali e senza modello

Richiede PowerShell 7 (pwsh); la suite genera autonomamente una
trascrizione **sintetica**, una risposta JSON mock e un file HTML.
Non chiama Claude, Codex o altri servizi:

~~~powershell
pwsh -NoProfile -File .\tests\MeetingMinutes.Tests.ps1
~~~

Risultato atteso: messaggio [PASS] senza eccezioni.

## Esecuzione reale con un CLI autorizzato

**ATTENZIONE:** le chiamate a Claude / Codex **inviano la trascrizione al modello**.
L'assenza di un'API key nel codice non significa elaborazione locale.
Usare soltanto dati che si è autorizzati a trattare con l'account configurato.
Non eseguire test con riunioni aziendali senza il consenso/informativa
applicabile e la verifica delle policy interne.

Da PowerShell 7, con un file TXT UTF-8 locale:

~~~powershell
$r = & .\scripts\meetings\Invoke-MeetingMinutes.ps1 -TranscriptPath '.\mia-trascrizione.txt' -Provider Claude -MeetingTitle 'Riunione di prova' -StyleLevel 80 -AllowExternalProcessing
Invoke-Item -LiteralPath $r.HtmlPath
~~~

Per utilizzare Codex CLI al posto di Claude:

~~~powershell
$r = & .\scripts\meetings\Invoke-MeetingMinutes.ps1 -TranscriptPath '.\mia-trascrizione.txt' -Provider Codex -AllowExternalProcessing
Invoke-Item -LiteralPath $r.HtmlPath
~~~

I due provider richiedono che il relativo comando sia disponibile nel
PATH della sessione Windows ed esista una sessione di login valida.
I flag CLI possono cambiare; se non funzionano, verificare la versione
installata prima di adattare lo script.

## Output

Ogni esecuzione salva, senza sovrascrivere:
- minutes.html: verbale HTML autosufficiente, CSS interno, CSP e testo escapato;
- minutes.json: risultato strutturato per integrazioni successive.
- minutes.txt: testo semplice per incollare in Outlook senza formattazione.

Il file minutes.html ora usa il layout di un **corpo email Outlook**: saluto, breve introduzione, titoli semplici, elenchi e tabella delle attività con stili HTML inline. Non è una dashboard. Per riutilizzarlo: aprire il file in Edge/Chrome, premere Ctrl+A e Ctrl+C, aprire un nuovo messaggio di Outlook in formato HTML e incollare nel corpo. Non serve allegare l'HTML. È normale che l'HTML creato in precedenza rimanga con il vecchio layout: occorre rigenerare la minuta.

Percorso predefinito: %LOCALAPPDATA%\AI-Terminals\MeetingNotes\meeting-....
Con -OutputDirectory si può scegliere una cartella locale alternativa.
Per riservatezza, il testo grezzo della trascrizione non viene copiato
automaticamente nell'output, ma resta presente nel file originale.

Per il livello di scrittura 80%, si applica un **profilo di italiano tecnico
controllato ispirato ai principi** del Simplified Technical English;
non si dichiara una conformità formale ASD-STE100 in italiano.

## Limiti dell'MVP

- Nessuna registrazione audio o trascrizione automatica (fase successiva).
- Nessuna app Windows grafica già pronta (fase successiva).
- Il modello può sbagliare: decisioni, assegnazioni e scadenze richiedono
  revisione umana prima della diffusione.
- Codex in sandbox read-only non equivale a isolamento totale dei dati.
- Non usare le minute reali come esempi committati nel repository GitHub.

## Step 2 — MeetingNotes.exe (app Windows)

La prima interfaccia Windows è indipendente da ClaudeTerminal.exe e CodexTerminal.exe.
Il programma riutilizza il generatore PowerShell testato nella Fase 1.
La build avviene su GitHub Actions con .NET 10 e Windows x64; il pacchetto è
autonomo e non richiede SDK o privilegi amministrativi per l'avvio.
PowerShell 7 e il provider CLI configurato devono essere disponibili sul PC.

### Aggiornamento, installazione e avvio (PowerShell)

Comandi completi, da qualunque cartella:

~~~powershell
$project = 'C:\Users\Overdata\Documents\workspace\personale\ai-terminals-voice'
Set-Location -LiteralPath $project
git pull --ff-only origin feature/meeting-notes
if ($LASTEXITCODE -ne 0) { throw 'Git pull fallito.' }
pwsh -NoProfile -File "$project\tests\MeetingMinutes.Tests.ps1"
pwsh -NoProfile -File "$project\scripts\meetings\Install-MeetingNotes.ps1"
& "$project\out\MeetingNotes\MeetingNotes.exe"
~~~

La procedura usa GitHub CLI (gh) già autenticato per scaricare l'ultima build
riuscita del workflow meeting-notes-tests.yml e la verifica prima di sostituire
l'installazione precedente. Se gh non è disponibile, compilazione locale
alternativa (richiede .NET 10 SDK):

~~~powershell
$project = 'C:\Users\Overdata\Documents\workspace\personale\ai-terminals-voice'
Set-Location -LiteralPath $project
dotnet publish "$project\src\meetings\MeetingNotes.csproj" -c Release -r win-x64 --self-contained true -o "$project\out\MeetingNotes"
& "$project\out\MeetingNotes\MeetingNotes.exe"
~~~

### Uso

1. Seleziona un file TXT UTF-8 e indica il titolo della riunione.
2. Scegli Claude Code, Codex oppure Mock. Mock richiede un file JSON sintetico;
   non usa la rete.
3. Per i provider reali, conferma che la trascrizione può essere inviata in
   base alle policy aziendali. Il CLI non effettua elaborazione locale.
4. Genera la minuta e controlla l'anteprima testuale.
5. Usa 'Copia per Outlook' e incolla nel corpo di un'email HTML. La copia
   usa il formato CF_HTML di Windows con offset UTF-8, oltre al testo semplice.
6. In alternativa scegli 'Apri HTML', 'Copia testo' o 'Apri cartella'.

La UI non registra ancora audio. La raccolta audio Windows e la trascrizione
locale sono attività successive e vanno abilitate solo nel rispetto delle
policy interne per registrazioni e consensi.

### Test eseguiti

- Parser + test PowerShell su dati sintetici, senza chiamate IA reali.
- Publish self-contained Windows dell'eseguibile su GitHub Actions.
- Test del formato CF_HTML, inclusi gli offset di caratteri non ASCII.
- Verifica che il pacchetto includa gli script necessari.

Il comportamento grafico, l'accesso locale ai CLI, l'invio tramite account
aziendale e l'effettiva incollatura in Outlook richiedono un test interattivo
sulla postazione Windows dell'utente.
