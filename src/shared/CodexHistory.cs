using System.IO;
using System.Text.Json;

namespace ClaudeTerminal.Host;

public static class CodexHistory
{
    public static IReadOnlyList<HistoryEntry> ForProject(string directory, int maxFiles = 1500)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        var folders = new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") };
        var wanted = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var names = LoadSessionNames(Path.Combine(home, "session_index.jsonl"));
        var found = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files = [];
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                files = files.Concat(Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories).ToArray());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var path in files.OrderByDescending(File.GetLastWriteTimeUtc).Take(maxFiles))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string? id = null;
                string? cwd = null;
                string? firstPrompt = null;

                // Codex serializes user prompts both as event_msg/user_message
                // and as response_item/message/role=user/content/input_text.
                // Older and newer versions can write only one representation.
                for (var n = 0; n < 1000 && !reader.EndOfStream; n++)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!TryString(root, "type", out var type) ||
                            !root.TryGetProperty("payload", out var payload) ||
                            payload.ValueKind != JsonValueKind.Object)
                            continue;

                        if (type == "session_meta")
                        {
                            if (TryString(payload, "cwd", out var directoryValue))
                                cwd = directoryValue;
                            if (TryString(payload, "id", out var value) ||
                                TryString(payload, "session_id", out value))
                                id = value;
                        }
                        else if (firstPrompt is null && type == "event_msg" &&
                                 TryString(payload, "type", out var eventType) &&
                                 eventType == "user_message" &&
                                 TryString(payload, "message", out var prompt))
                        {
                            firstPrompt = prompt;
                        }
                        else if (firstPrompt is null && type == "response_item" &&
                                 TryString(payload, "type", out var itemType) &&
                                 itemType == "message" &&
                                 TryString(payload, "role", out var role) &&
                                 role == "user" &&
                                 payload.TryGetProperty("content", out var parts))
                        {
                            firstPrompt = ReadUserContent(parts);
                        }
                    }
                    catch (JsonException) { }

                    if (!string.IsNullOrWhiteSpace(id) &&
                        !string.IsNullOrWhiteSpace(cwd) &&
                        !string.IsNullOrWhiteSpace(firstPrompt))
                        break;
                }

                if (!Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(cwd)) continue;
                string fullPath;
                try { fullPath = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar); }
                catch { continue; }
                if (!StringComparer.OrdinalIgnoreCase.Equals(wanted, fullPath)) continue;

                // Explicit /rename names are append-only in session_index.jsonl.
                // Prefer them to the first prompt, just like Codex resume.
                string title = names.TryGetValue(id!, out var name) ? name : firstPrompt ?? "";
                title = NormalizeTitle(title);
                if (title.Length == 0) title = "Codex " + id![..8];

                var row = new HistoryEntry(id!, title, fullPath, File.GetLastWriteTimeUtc(path), "Codex");
                // The same thread can exist in sessions and archived_sessions.
                found.TryAdd(id!, row);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found.Values.OrderByDescending(x => x.Timestamp).Take(250).ToArray();
    }

    private static Dictionary<string, string> LoadSessionNames(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!(TryString(root, "id", out var id) ||
                          TryString(root, "session_id", out id)) ||
                        !Guid.TryParse(id, out _) ||
                        !TryString(root, "thread_name", out var title)) continue;
                    title = NormalizeTitle(title);
                    if (title.Length > 0) result[id] = title; // latest rename wins
                }
                catch (JsonException) { } // Ignore torn final JSONL lines.
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    private static string? ReadUserContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object ||
                !TryString(part, "type", out var type) ||
                (type != "input_text" && type != "text") ||
                !TryString(part, "text", out var text)) continue;
            return text;
        }
        return null;
    }

    private static bool TryString(JsonElement element, string key, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(key, out var token) ||
            token.ValueKind != JsonValueKind.String) return false;
        value = token.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string NormalizeTitle(string title)
    {
        title = string.Join(" ", title.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (title.Length > 90) title = title[..87] + "...";
        return title;
    }
}
