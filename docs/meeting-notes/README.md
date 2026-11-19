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
