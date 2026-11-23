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

- La registrazione locale WAV è disponibile nella GUI; resta da implementare la trascrizione automatica offline.
- La GUI Windows è disponibile, con cartella di lavoro configurabile e editor TXT.
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

L'installer non richiede gh: cerca GITHUB_TOKEN, GH_TOKEN, GitHub CLI (se presente)
o le credenziali di Git Credential Manager, generalmente già usate per
git pull. Scarica l'ultimo pacchetto riuscito tramite API GitHub con staging
e backup. Se nessuna credenziale è disponibile, prova dotnet publish
qualora sia installato .NET 10 SDK. Compilazione locale manuale alternativa:

~~~powershell
$project = 'C:\Users\Overdata\Documents\workspace\personale\ai-terminals-voice'
Set-Location -LiteralPath $project
dotnet publish "$project\src\meetings\MeetingNotes.csproj" -c Release -r win-x64 --self-contained true -o "$project\out\MeetingNotes"
& "$project\out\MeetingNotes\MeetingNotes.exe"
~~~

### Cartella di lavoro (GUI)

La schermata permette di scegliere una cartella locale, fuori da qualsiasi
repository Git, per archiviare i dati. La scelta viene ricordata nella
configurazione locale per i prossimi avvii. Non sposta automaticamente i file
creati dalle versioni precedenti.

All'interno vengono utilizzate tre sottocartelle:

- registrazioni: sottocartelle per ciascuna acquisizione, con file WAV separati
  per microfono e audio del PC;
- trascrizioni: TXT UTF-8 creati dall'editor o importati e risalvati;
- minute: una cartella per ogni elaborazione, con minutes.html,
  minutes.txt e minutes.json.

Con "Sfoglia" si può ancora importare un TXT esterno. Altrimenti è possibile
scrivere o incollare una trascrizione nell'editor, quindi premere "Salva TXT":
l'app genera un file con nome univoco, senza sovrascrivere quelli esistenti.
Premendo "Genera minuta" viene salvata automaticamente una copia del testo
corrente prima di chiamare il provider.

**Importante:** registrare un WAV non produce ancora una trascrizione TXT.
La conversione automatica audio -> testo sarà implementata dopo aver
verificato il microfono e il loopback sul PC reale. Fino a quel momento
la modifica o la creazione del TXT è manuale.

Evita cartelle sincronizzate o condivise se le policy aziendali non ne
consentono l'uso per contenuti delle riunioni.

### Uso

1. Seleziona la cartella di lavoro. Importa un TXT oppure scrivi/incolla il testo nell'editor e premi Salva TXT; indica il titolo.
2. Scegli Claude Code, Codex oppure Mock. Mock richiede un file JSON sintetico;
   non usa la rete.
3. Per i provider reali, conferma che la trascrizione può essere inviata in
   base alle policy aziendali. Il CLI non effettua elaborazione locale.
4. Genera la minuta e controlla l'anteprima testuale.
5. Usa 'Copia per Outlook' e incolla nel corpo di un'email HTML. La copia
   usa il formato CF_HTML di Windows con offset UTF-8, oltre al testo semplice.
6. In alternativa scegli 'Apri HTML', 'Copia testo' o 'Apri cartella'.

### Prova audio locale (senza trascrizione automatica)

Nella finestra Meeting Notes è presente una sezione "Prova registrazione audio".

1. Conferma che la prova è consentita dalle policy aziendali e che i partecipanti
   sono stati informati quando necessario. Per il primo collaudo usa audio
   sintetico/non riservato, ad esempio un video di prova e la tua voce.
2. Scegli "Microfono", "Audio del PC" oppure entrambi.
3. Premi "Registra", parla nel microfono e riproduci un contenuto di prova
   dal computer. La registrazione locale si ferma automaticamente dopo 10 minuti.
4. Premi "Ferma", poi "File audio": vengono salvati file WAV distinti
   microfono.wav e audio-pc.wav nella sottocartella registrazioni della cartella di lavoro.
5. Ascolta i due file separatamente per verificare chiarezza, livelli e
   funzionamento effettivo delle sorgenti.

I file restano solo nel dispositivo Windows. Nessun upload automatico,
nessun bot nelle riunioni, nessuna connessione a Claude per questa prova.
Con dispositivo audio silenzioso, WASAPI loopback potrebbe generare un file
vuoto o contenente solo l'intestazione. In questo step **non** vengono ancora
trascritti i WAV, né viene generata automaticamente la minuta dall'audio.
La trascrizione offline (Whisper o alternativa) resta nel prossimo step.

### Test eseguiti

- Parser + test PowerShell su dati sintetici, senza chiamate IA reali.
- Publish self-contained Windows dell'eseguibile su GitHub Actions.
- Test del formato CF_HTML, inclusi gli offset di caratteri non ASCII.
- Verifica che il pacchetto includa gli script necessari.

Il comportamento grafico, l'accesso locale ai CLI, l'invio tramite account
aziendale e l'effettiva incollatura in Outlook richiedono un test interattivo
sulla postazione Windows dell'utente.


## Fase 3b — Trascrizione offline di WAV

L'app include un processo indipendente MeetingTranscriber.exe che usa
Whisper.net 1.9.1 e il runtime CPU di whisper.cpp.

La conversione avviene **interamente in locale**. Il download del modello
Whisper è una **operazione separata ed esplicita**, che usa Internet una
sola volta. L'app non effettua download impliciti.

### Primo download del modello multilingua

Da PowerShell, da qualunque cartella:

~~~powershell
$project = 'C:\Users\Overdata\Documents\workspace\personale\ai-terminals-voice'
pwsh -NoProfile -File "$project\scripts\meetings\Install-WhisperModel.ps1"
~~~

Scarica ggml-base.bin (~148 MB) dal repository Hugging Face di ggerganov,
e verifica il SHA-256 prima di conservarlo in
%LOCALAPPDATA%\AI-Terminals\MeetingNotes\Models\ggml-base.bin.

Hash atteso:
60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe

Se il download è bloccato dalle policy aziendali, è possibile scegliere
manualmente un modello Whisper multilingua GGML .bin già disponibile in
locale (e autorizzato). Non usare varianti *.en.bin, dedicate all'inglese.

### Prova WAV -> TXT

1. Chiudere MeetingNotes.exe e fare pull del branch.
2. Installare l'ultimo eseguibile con Install-MeetingNotes.ps1.
3. Nella nuova sezione "Trascrizione offline · Whisper" scegliere il
   modello ggml-base.bin (la cartella predefinita è riconosciuta automaticamente).
4. Usare i WAV appena registrati oppure selezionare un WAV con "Sfoglia WAV".
5. Scegliere Microfono, Audio del PC, o entrambe le tracce e premere
   "Trascrivi offline".
6. Il processo separato normalizza i WAV a PCM mono 16 kHz prima del
   riconoscimento vocale, senza salvare file audio normalizzati permanenti.
7. La trascrizione viene scritta in un TXT UTF-8 nella sottocartella
   "trascrizioni" della cartella di lavoro e aperta nell'editor TXT.
8. Correggere il contenuto e solo poi scegliere "Genera minuta" con un CLI
   autorizzato. La chiamata al CLI può inviare il testo all'esterno;
   la trascrizione offline, invece, no.

Quando sono disponibili entrambe le tracce, la trascrizione le elabora
separatamente e riordina i segmenti per timestamp. Le etichette
[MICROFONO] e [AUDIO PC] indicano la sorgente, non identificano i
singoli partecipanti. Non è implementata la diarizzazione vera.
I timestamp tra le due sorgenti sono approssimati e il microfono può
captare anche l'audio degli altoparlanti, producendo duplicazioni.
La velocità dipende dalla CPU e dalla dimensione del modello.

Per CPU x64: Whisper.net.Runtime richiede Windows 11 e il runtime
Microsoft Visual C++ Redistributable 2022 x64 già presente nel sistema,
oltre alle istruzioni AVX/AVX2/FMA/F16C. In caso di dipendenze mancanti,
l'app segnalerà un errore. Non tentare di aggirare i controlli del PC aziendale.
Se la CPU non supporta AVX, si valuterà un runtime NoAVX separato.

### Test automatici e limiti

GitHub Actions pubblica MeetingNotes.exe e MeetingTranscriber.exe
in sottocartelle separate dell'artefatto MeetingNotes-win-x64.
Un self-test STT verifica la conversione WAV 48 kHz stereo -> 16 kHz
mono senza dover scaricare un modello né usare dati reali.
L'inferenza Whisper con il modello e l'hardware aziendale richiede
un collaudo sul computer dell'utente.

La trascrizione NON parte automaticamente con la registrazione:
si avvia con il pulsante "Trascrivi offline", così l'utente può
verificare le tracce e scegliere quali trattare.


## Stile delle minute, icona e registrazioni più lunghe

Dalla versione 0.4 la schermata offre tre scelte **indipendenti** dal livello
di semplificazione linguistica (il valore 80% può restare invariato):

- **Discorsivo**: riepilogo e argomenti in paragrafi con più contesto;
  decisioni spiegate in frasi; tabella attività sempre presente.
- **Bilanciato** (predefinito): breve riepilogo e paragrafi sugli argomenti;
  decisioni concise, questioni aperte in elenco e tabella attività.
- **Sintetico**: riepilogo breve ed elenchi puntati per argomenti e decisioni;
  tabella attività invariata.

La scelta influisce sia sulle istruzioni date a Claude/Codex, sia
sull'impaginazione HTML e TXT. Le trascrizioni non sono alterate; per cambiare
stile di una minuta già creata occorre rigenerarla. Il formato HTML resta
adatto a Outlook e l'app non deve inventare decisioni o scadenze.

### Icona e avvio rapido

Durante la compilazione viene creata una **icona Windows .ico** specifica,
incorporata in MeetingNotes.exe e nella finestra. Il file binario generato
non è conservato in Git.

L'installer crea anche un collegamento **Meeting Notes** nel menu Start
dell'utente corrente (senza amministratore). Dopo l'aggiornamento:

1. Premi **Win**, cerca **Meeting Notes** e apri l'app.
2. Fai clic destro sull'icona nella barra delle applicazioni e seleziona
   **Aggiungi alla barra delle applicazioni** (o **Fissa alla barra**).

Il collegamento punta alla posizione stabile
`C:\Users\Overdata\Documents\workspace\personale\ai-terminals-voice\out\MeetingNotes\MeetingNotes.exe`
e continua a funzionare dopo i successivi aggiornamenti dell'installer.

### Cosa accade se condividi lo schermo

L'acquisizione audio è WASAPI, distinta dal contenuto video condiviso:
attivare o interrompere la condivisione di Teams **non invia un comando
di stop** al registratore. Può però cambiare il dispositivo di input/output
audio o il routing di Teams; in questo caso il WAV potrebbe diventare
silenzioso o il dispositivo potrebbe interrompere la cattura.
**La continuità non è stata ancora collaudata** al cambio di condivisione:
prova con audio non riservato, controlla entrambi i WAV.

Il limite di registrazione per collaudo è stato portato da 10 a
**120 minuti** con un timer `hh:mm:ss`.
I WAV non compressi possono occupare molti gigabyte: verifica lo spazio
libero e interrompi manualmente quando la riunione termina. La trascrizione
offline è avviata separatamente, dopo aver salvato i WAV.


## Fase 5 — Trascrizione automatica e pulizia sicura audio

Quando la registrazione termina normalmente (tasto **Ferma** o limite di durata),
l'app finalizza i WAV e **avvia automaticamente Whisper offline** sulle tracce
effettivamente registrate, senza richiedere "Trascrivi offline".

Nella sezione **Impostazioni** è disponibile:
- **Mantieni file audio originale**: disattivato per default.
  L'impostazione resta salvata per gli avvii successivi nel file locale
  %LOCALAPPDATA%\AI-Terminals\MeetingNotes\recording-preferences.json.
- Se la casella è disattivata, la nuova registrazione è eliminata soltanto
  DOPO che Whisper termina senza errori e il TXT viene salvato,
  risulta non vuoto e contiene segmenti riconosciuti.
- Se la casella è attivata, microfono.wav e audio-pc.wav restano nella cartella.
- La scelta è fissata all'inizio della singola registrazione.

**Protezioni dalla perdita di dati:**
- Modello mancante, file WAV vuoto, errore Whisper, TXT vuoto,
  timeout, annullamento, chiusura forzata o problema di scrittura:
  i WAV vengono conservati per un nuovo tentativo.
- Sono ammissibili alla cancellazione automatica SOLO i WAV della sessione
  appena terminata, con i nomi noti e collocati direttamente in
  registrazioni/rec-.../. Non vengono eliminati file importati manualmente,
  registrazioni precedenti o file aggiuntivi presenti nella cartella.
- La trascrizione avviata manualmente da "Trascrivi offline" non elimina mai
  il WAV di origine. È il percorso di recupero per le registrazioni conservate.
- Se la pulizia fallisce per file in uso, permessi o percorso inatteso,
  l'app segnala che il TXT è stato salvato e non considera riuscita la pulizia.
- **Non viene mai avviata automaticamente la generazione di minute Claude/Codex**:
  l'utente deve correggere il TXT e autorizzare esplicitamente l'invio.

Per la prima prova non utilizzare dati riservati: acquisire una breve
registrazione, premere Ferma e verificare la creazione del TXT.
Se "Mantieni file audio originale" è disattivato, i WAV di quella
sessione devono scomparire. Ripetere con la casella selezionata e verificare
che restino presenti. Provare anche un arresto di Whisper con
"Annulla trascrizione": i WAV devono essere conservati.

Questi comportamenti sono verificati automaticamente con unit/smoke test
sintetici per impostazioni, TXT, limiti dei percorsi e cancellazione.
La sincronizzazione con il dispositivo audio fisico richiede comunque
un collaudo Windows interattivo.


## Comandi rapidi sempre visibili (v0.6)

Dopo l'avvio, nell'angolo **in alto a destra** sono disponibili:
- **Registra**: avvia subito la registrazione usando microfono e audio del PC
  selezionati nella barra sinistra. Se la conferma di registrazione non è
  selezionata, la richiede tramite una finestra esplicita, senza dover
  scorrere in basso per trovare la casella.
- **Ferma**: termina l'acquisizione e avvia la trascrizione automatica
  offline, rispettando l'impostazione di conservazione dei WAV.
- **Timer hh:mm:ss e stato**: sono visibili anche quando si scorre il
  pannello laterale. Durante l'elaborazione viene mostrato lo stato
  della trascrizione.

I pulsanti rapidi richiamano **gli stessi handler** dei pulsanti presenti
nella barra laterale e ne seguono lo stato abilitato/disabilitato.
Non esistono due registratori distinti: non è possibile avviare una
seconda acquisizione con il tasto duplicato mentre la prima è attiva.

La colonna sinistra resta dedicata a sorgenti, consenso, modelli,
preferenze e opzioni avanzate. Il gruppo comandi in alto resta fuori
dal pannello scorrevole.
