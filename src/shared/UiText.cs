using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ClaudeTerminal.Host;

/// <summary>
/// Shared UI text catalogue. Stable invariant keys allow additional translations
/// without changing persisted session/folder identifiers.
/// English is the public fallback; Italian is selected on Italian Windows.
/// </summary>
public static class UiText
{
    private static readonly string PreferenceFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerShellCustomization", "ai-terminals-language.json");

    private static readonly Dictionary<string, (string En, string It)> Strings = new(StringComparer.Ordinal)
    {
        ["project"] = ("Project", "Progetto"),
        ["browse"] = ("Browse...", "Sfoglia..."),
        ["openProject"] = ("Open project", "Apri progetto"),
        ["search"] = ("Search", "Cerca"),
        ["searchHint"] = ("Show/hide search (Ctrl+F)", "Mostra/nascondi ricerca (Ctrl+F)"),
        ["refresh"] = ("Refresh", "Aggiorna"),
        ["refreshHint"] = ("Refresh local history", "Aggiorna storico dal disco"),
        ["conversations"] = ("CONVERSATIONS", "CONVERSAZIONI"),
        ["newConversation"] = ("+ New conversation", "+ Nuova conversazione"),
        ["newFolder"] = ("+ Folder", "+ Cartella"),
        ["newFolderHint"] = ("Create a virtual folder for this project", "Crea una cartella virtuale per questo progetto"),
        ["folderName"] = ("Folder name", "Nome cartella"),
        ["folderCreate"] = ("Create folder", "Crea cartella"),
        ["folderRename"] = ("Rename folder", "Rinomina cartella"),
        ["folderDelete"] = ("Remove folder", "Rimuovi cartella"),
        ["folderDeletePrompt"] = ("Remove the folder '{0}'? Its conversations will return to Unorganized; no transcript will be deleted.", "Rimuovere la cartella '{0}'? Le conversazioni torneranno in Non organizzate; nessun transcript verrà cancellato."),
        ["folderDuplicate"] = ("A folder with this name already exists.", "Esiste già una cartella con questo nome."),
        ["folderInvalid"] = ("Enter a name between 1 and 60 characters, on one line.", "Inserisci un nome tra 1 e 60 caratteri, su una sola riga."),
        ["folderReserved"] = ("This name is reserved for a built-in folder.", "Questo nome è riservato a una cartella predefinita."),
        ["folderSelect"] = ("Move to folder", "Sposta nella cartella"),
        ["unorganized"] = ("Unorganized", "Non organizzate"),
        ["liveConversations"] = ("Active conversations", "Conversazioni attive"),
        ["otherProject"] = ("Other project", "Altro progetto"),
        ["bin"] = ("Bin", "Secchio"),
        ["binHint"] = ("Conversations are hidden, not deleted from disk.", "Le conversazioni sono nascoste, non cancellate dal disco."),
        ["moveToBin"] = ("Move to Bin", "Sposta nel Secchio"),
        ["restore"] = ("Restore conversation", "Ripristina conversazione"),
        ["restoreFirst"] = ("Restore this conversation from the Bin before resuming it.", "Ripristina questa conversazione dal Secchio prima di riprenderla."),
        ["binConfirm"] = ("Move '{0}' to the Bin? The original conversation files will remain untouched.", "Spostare '{0}' nel Secchio? I file originali della conversazione resteranno intatti."),
        ["restoreConfirm"] = ("Restore this conversation?", "Ripristinare questa conversazione?"),
        ["activeMoveDenied"] = ("Close the active session before organizing it. No data was changed.", "Chiudi la sessione attiva prima di organizzarla. Nessun dato è stato modificato."),
        ["invalidOrganization"] = ("Cannot load virtual folders. Nothing was changed: {0}", "Impossibile leggere le cartelle virtuali. Nessun dato è stato modificato: {0}"),
        ["organizationError"] = ("Could not save virtual folders: {0}", "Impossibile salvare le cartelle virtuali: {0}"),
        ["foldersEmpty"] = ("No conversations", "Nessuna conversazione"),
        ["activeCount"] = ("  {0} active", "  {0} attive"),
        ["tokens"] = ("Tokens", "Token"),
        ["tokensHint"] = ("Local {0} usage", "Consumi locali {0}"),
        ["resume"] = ("Resume", "Riprendi"),
        ["closeSession"] = ("Close session", "Chiudi sessione"),
        ["noConversation"] = ("No conversation selected", "Nessuna conversazione"),
        ["sideChoose"] = ("Choose a project. No active sessions.", "Scegli un progetto. Nessuna sessione attiva."),
        ["sideProject"] = ("Project: {0}\nSessions stay active when switching projects.", "Progetto: {0}\nLe sessioni già aperte restano attive cambiando progetto."),
        ["filterHint"] = ("Search active and previous conversations", "Cerca tra sessioni attive e storico"),
        ["loadingRenderer"] = ("Starting local renderer...", "Avvio renderer locale..."),
        ["rendererLoading"] = ("Loading xterm renderer...", "Caricamento terminale xterm..."),
        ["rendererReady"] = ("Renderer ready. Open conversations or browse your history.", "Renderer pronto. Apri conversazioni o consulta lo storico."),
        ["indexing"] = ("Indexing local conversations...", "Indicizzazione conversazioni locali..."),
        ["historyCount"] = ("{0} conversations in this project's history.", "{0} conversazioni nello storico del progetto."),
        ["statusStarting"] = ("Starting", "Avvio"),
        ["statusRunning"] = ("Running", "In esecuzione"),
        ["statusExited"] = ("Finished", "Terminata"),
        ["statusIo"] = ("I/O error", "Errore I/O"),
        ["statusError"] = ("Error", "Errore"),
        ["statusHistory"] = ("History", "Storico"),
        ["responseReady"] = ("Response ready", "Risposta pronta"),
        ["newChatLabel"] = ("New chat {0}", "Nuova chat {0}"),
        ["diagnosticShell"] = ("Diagnostic shell {0}", "Shell diagnostica {0}"),
        ["cliMissing"] = ("{0} launcher script missing from package: {1}", "Script {0} mancante nel pacchetto: {1}"),
        ["chooseProject"] = ("Choose a project and wait for the renderer.", "Scegli un progetto e attendi che il renderer sia pronto."),
        ["wrongProvider"] = ("This conversation belongs to another agent.", "La conversazione appartiene a un altro agente."),
        ["alreadyActive"] = ("Conversation already active: switched to existing terminal.", "Conversazione già attiva: mostrato il terminale esistente."),
        ["wrongProject"] = ("The selected conversation does not belong to this project.", "La conversazione selezionata non appartiene al progetto attivo."),
        ["connectionFault"] = ("ConPTY disconnected; verify process: {0}", "Connessione ConPTY interrotta; processo da verificare: {0}"),
        ["processNoCode"] = ("Process closed; exit code unavailable.", "Il processo si è chiuso; codice di uscita non disponibile."),
        ["processExit"] = ("Process finished (exit code {0}).", "Processo terminato (codice {0})."),
        ["terminalStarted"] = ("Integrated {0} terminal started.", "Terminale {0} integrato avviato."),
        ["terminalStartError"] = ("Failed to start {0}: {1}", "Errore avvio {0}: {1}"),
        ["folderNotFound"] = ("Folder not found.", "Cartella non trovata."),
        ["historyReadError"] = ("History error: {0}", "Errore storico: {0}"),
        ["projectMissing"] = ("Directory does not exist: {0}", "La directory non esiste: {0}"),
        ["initFailed"] = ("Unable to initialize the terminal: {0}", "Impossibile inizializzare il terminale: {0}"),
        ["terminalProtocolError"] = ("Terminal communication error: {0}", "Errore comunicazione terminale: {0}"),
        ["clipboardTooLarge"] = ("Clipboard data is too large to paste.", "Appunti troppo grandi per incollarli nel terminale."),
        ["noTranscript"] = ("No transcript available: this chat may not have messages yet.", "Nessun transcript disponibile: la chat potrebbe non avere ancora messaggi."),
        ["closeSessionPrompt"] = ("Terminate this conversation and its {0} process? Work in progress will be interrupted.", "Terminare questa conversazione e il suo processo {0}? Il lavoro in corso verrà interrotto."),
        ["closeSessionTitle"] = ("Close session", "Chiudi sessione"),
        ["closeAppPrompt"] = ("Closing this window will terminate all running {0} sessions. Continue?", "Chiudere il terminale terminerà tutte le sessioni {0} in esecuzione. Vuoi continuare?"),
        ["sessionsActive"] = ("Sessions still running", "Sessioni ancora attive"),
        ["selectFolderTitle"] = ("Organize conversation", "Organizza conversazione"),
        ["language"] = ("Language", "Lingua"),
        ["toastComplete"] = ("Response completed", "Risposta completata"),
        ["toastOpen"] = ("Click to open the conversation", "Clicca per aprire la conversazione"),
        ["usageTitle"] = ("Local token usage", "Utilizzo token locale"),
        ["usageSubtitle"] = ("{0} · statistics from local JSONL files", "{0} · statistiche dai file JSONL locali"),
        ["usageRefresh"] = ("Recalculate", "Ricalcola"),
        ["close"] = ("Close", "Chiudi"),
        ["agent"] = ("Agent", "Agente"),
        ["scope"] = ("Scope", "Ambito"),
        ["allProjects"] = ("All projects", "Tutti i progetti"),
        ["currentProject"] = ("Current project", "Progetto corrente"),
        ["readLocal"] = ("Reading local data...", "Lettura dati locali..."),
        ["scanning"] = ("Scanning...", "Analisi in corso..."),
        ["readLocalFailed"] = ("Could not read local data: {0}", "Impossibile leggere i dati locali: {0}"),
        ["totalTokens"] = ("Total tokens: {0}", "Token totali: {0}"),
        ["cacheRead"] = ("Cache read: {0}", "Cache letta: {0}"),
        ["cacheWrite"] = ("Cache written: {0}", "Cache scritta: {0}"),
        ["input"] = ("Input: {0}", "Input: {0}"),
        ["output"] = ("Output: {0}", "Output: {0}"),
        ["reasoning"] = ("Reasoning: {0}", "Ragionamento: {0}"),
        ["usageRows"] = ("{0} sessions / {1} with available data", "{0} sessioni / {1} con dati disponibili"),
        ["usageInfo"] = ("Figures are reconstructed from local transcripts; they are not account balances or bills. Codex cached tokens are included in input; reasoning tokens are included in output. Costs are not estimated.", "Misure ricostruite dai transcript locali: non rappresentano quota residua o fatturazione dell’account. I token di cache Codex sono inclusi nell’input; il ragionamento nell’output. I costi non sono stimati."),
        ["session"] = ("Session", "Sessione"),
        ["model"] = ("Model", "Modello"),
        ["total"] = ("Total", "Totale"),
        ["cacheReadHeader"] = ("Cache read", "Cache letta"),
        ["cacheWriteHeader"] = ("Cache written", "Cache scritta"),
        ["reasoningHeader"] = ("Reasoning", "Ragionamento"),
        ["inputHeader"] = ("Input", "Input"),
        ["outputHeader"] = ("Output", "Output"),
        ["lastActivity"] = ("Last activity", "Ultima attività"),
        ["reset"] = ("Reset", "Ripristina"),
        ["cancel"] = ("Cancel", "Annulla"),
        ["save"] = ("Save", "Salva"),
        ["confirm"] = ("Confirm", "Conferma")
    };

    public static string Language { get; private set; } = LoadLanguage();
    public static bool IsItalian => Language == "it";

    public static void SetLanguage(string language)
    {
        if (language is not ("en" or "it"))
            throw new ArgumentException("Unsupported language.", nameof(language));
        var parent = Path.GetDirectoryName(PreferenceFile)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, ".ui-lang-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new { Version = 1, Language = language }));
            File.Move(temp, PreferenceFile, overwrite: true);
            Language = language;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string T(string key, params object?[] args)
    {
        if (!Strings.TryGetValue(key, out var pair)) return key;
        var pattern = IsItalian ? pair.It : pair.En;
        return args.Length == 0 ? pattern :
            string.Format(IsItalian ? CultureInfo.GetCultureInfo("it-IT") :
                                      CultureInfo.GetCultureInfo("en-US"), pattern, args);
    }

    private static string LoadLanguage()
    {
        try
        {
            if (File.Exists(PreferenceFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(PreferenceFile));
                if (doc.RootElement.TryGetProperty("Language", out var lang) &&
                    lang.ValueKind == JsonValueKind.String &&
                    lang.GetString() is "en" or "it")
                    return lang.GetString()!;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en";
    }
}
