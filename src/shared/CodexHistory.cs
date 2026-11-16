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
                string? title = null;

                for (var n = 0; n < 300 && !reader.EndOfStream; n++)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch (JsonException) { continue; }

                    using (doc)
                    {
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var ty)) continue;
                        if (!root.TryGetProperty("payload", out var payload) ||
                            payload.ValueKind != JsonValueKind.Object) continue;

                        if (ty.ValueKind == JsonValueKind.String && ty.GetString() == "session_meta")
                        {
                            if (payload.TryGetProperty("cwd", out var wd) && wd.ValueKind == JsonValueKind.String)
                                cwd = wd.GetString();
                            if (payload.TryGetProperty("id", out var identity) && identity.ValueKind == JsonValueKind.String)
                                id = identity.GetString();
                        }
                        else if (ty.ValueKind == JsonValueKind.String && ty.GetString() == "event_msg" &&
                                 payload.TryGetProperty("type", out var evt) && evt.GetString() == "user_message")
                        {
                            if (payload.TryGetProperty("message", out var text) &&
                                text.ValueKind == JsonValueKind.String)
                                title = text.GetString();
                        }
                    }
                    if (cwd is not null && id is not null && title is not null) break;
                }

                if (!Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(cwd)) continue;
                string fullPath;
                try { fullPath = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar); }
                catch { continue; }
                if (!StringComparer.OrdinalIgnoreCase.Equals(wanted, fullPath)) continue;
                if (string.IsNullOrWhiteSpace(title)) title = "Codex " + id[..8];
                title = string.Join(" ", title.Split(['\r','\n','\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
                if (title.Length > 90) title = title[..87] + "...";
                var row = new HistoryEntry(id, title, fullPath, File.GetLastWriteTimeUtc(path), "Codex");
                found.TryAdd(id, row);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return found.Values.OrderByDescending(x => x.Timestamp).Take(250).ToArray();
    }
}
