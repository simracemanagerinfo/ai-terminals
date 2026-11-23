using System.IO;
using System.Text;
using System.Text.Json;

namespace MeetingNotes;

/// <summary>
/// Percorsi e testi creati solo sul PC. L'utente può scegliere la cartella,
/// ma non è consentito scrivere dati delle riunioni dentro un repository Git.
/// </summary>
public static class WorkspaceStore
{
    public const int MaxTranscriptBytes = 2 * 1024 * 1024;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Terminals", "MeetingNotes", "Workspace");

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Terminals", "MeetingNotes", "workspace-settings.json");

    public static string Transcripts(string root) => Path.Combine(root, "trascrizioni");
    public static string Recordings(string root) => Path.Combine(root, "registrazioni");
    public static string Minutes(string root) => Path.Combine(root, "minute");

    public static string LoadPreferredRoot()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return DefaultRoot;

            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("workspaceRoot", out var value))
            {
                var candidate = value.GetString();
                if (!string.IsNullOrWhiteSpace(candidate))
                    return ValidateRoot(candidate);
            }
        }
        catch (Exception)
        {
            // Impostazione corrotta/non valida: usare il percorso locale,
            // senza modificare documenti dell'utente o divulgare il path.
        }
        return DefaultRoot;
    }

    public static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Seleziona una cartella con percorso assoluto.", nameof(root));

        var full = Path.GetFullPath(root);
        for (DirectoryInfo? current = new DirectoryInfo(full);
             current is not null;
             current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                throw new InvalidOperationException(
                    "La cartella selezionata si trova dentro un repository Git. " +
                    "Per evitare commit accidentali di contenuti riservati, scegli una cartella esterna.");
        }
        return full;
    }

    public static void EnsureFolders(string root)
    {
        root = ValidateRoot(root);
        Directory.CreateDirectory(Transcripts(root));
        Directory.CreateDirectory(Recordings(root));
        Directory.CreateDirectory(Minutes(root));
    }

    public static void SavePreferredRoot(string root)
    {
        root = ValidateRoot(root);
        EnsureFolders(root);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(new { workspaceRoot = root });
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
    }

    public static string CreateTranscript(string root, string title, string contents)
    {
        if (string.IsNullOrWhiteSpace(contents))
            throw new ArgumentException("Inserisci una trascrizione prima di salvarla.", nameof(contents));
        if (Encoding.UTF8.GetByteCount(contents) > MaxTranscriptBytes)
            throw new InvalidOperationException("La trascrizione supera il limite di 2 MiB.");

        root = ValidateRoot(root);
        Directory.CreateDirectory(Transcripts(root));

        var safe = new string((title ?? "").Trim().Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(safe)) safe = "riunione";
        if (safe.Length > 48) safe = safe[..48];

        var filename = $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}-{Guid.NewGuid():N}.txt";
        var file = Path.Combine(Transcripts(root), filename);
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(contents);
        }
        return file;
    }
}
