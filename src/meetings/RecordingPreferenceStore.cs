using System.IO;
using System.Text;
using System.Text.Json;

namespace MeetingNotes;

public sealed record RecordingPreferences(bool KeepOriginalAudio);

/// <summary>
/// Preferenze locali, separate dalla cartella di lavoro. Nessun dato della
/// riunione viene incluso nel file di configurazione.
/// </summary>
public static class RecordingPreferenceStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Terminals", "MeetingNotes", "recording-preferences.json");

    public static RecordingPreferences Load(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultPath;
        try
        {
            if (!File.Exists(path))
                return new RecordingPreferences(false);

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.TryGetProperty("keepOriginalAudio", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return new RecordingPreferences(value.GetBoolean());
        }
        catch (Exception)
        {
            // Configurazione assente/corrotta: minimizzazione dei dati richiesta
            // dall'utente. L'audio non verrà comunque cancellato senza un TXT valido.
        }
        return new RecordingPreferences(false);
    }

    public static void Save(RecordingPreferences preferences, string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".recording-preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var json = JsonSerializer.Serialize(new { keepOriginalAudio = preferences.KeepOriginalAudio });
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
