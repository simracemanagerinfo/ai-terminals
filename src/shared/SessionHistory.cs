using System.IO;
using System.Text.Json;

namespace ClaudeTerminal.Host;

public sealed record HistoryEntry(string Id, string Title, string Directory, DateTimeOffset Timestamp, string Provider = "Claude")
{
    public string Modified => Timestamp.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
}

public static class SessionHistory
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string HistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerShellCustomization", TerminalBrand.Provider == "Claude" ? "claude-project-history.json" : "codex-project-history.json");

    public static IReadOnlyList<string> RecentProjects()
    {
        if (!File.Exists(HistoryFile)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(HistoryFile));
            if (doc.RootElement.GetProperty("Version").GetInt32() != 1) return [];
            return doc.RootElement.GetProperty("Paths").EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String)
                .Select(p => p.GetString())
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Select(p => Path.GetFullPath(p!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(15).ToArray();
        }
        catch { return []; }
    }

    public static void SaveProject(string project)
    {
        // Never overwrite an unreadable existing history file.
        if (File.Exists(HistoryFile))
        {
            try
            {
                using var current = JsonDocument.Parse(File.ReadAllText(HistoryFile));
                if (current.RootElement.GetProperty("Version").GetInt32() != 1 ||
                    current.RootElement.GetProperty("Paths").ValueKind != JsonValueKind.Array)
                    return;
            }
            catch { return; }
        }
        var paths = new[] { project }.Concat(RecentProjects())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToArray();
        var parent = Path.GetDirectoryName(HistoryFile)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, "claude-recent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new { Version = 1, Paths = paths }));
            File.Move(temp, HistoryFile, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static IReadOnlyList<HistoryEntry> ForProject(string directory, int maxFiles = 1000)
    {
        var root = Path.Combine(Home, ".claude", "projects");
        if (!Directory.Exists(root)) return [];
        var wanted = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(maxFiles).ToArray();
        }
        catch { return []; }

        var found = new List<HistoryEntry>();
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParse(id, out _)) continue;
            var cwd = "";
            var title = "";
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var r = new StreamReader(fs);
                for (var i = 0; i < 450 && !r.EndOfStream; i++)
                {
                    var line = r.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var ev = doc.RootElement;
                        if (string.IsNullOrWhiteSpace(cwd) &&
                            ev.TryGetProperty("cwd", out var dir) && dir.ValueKind == JsonValueKind.String)
                            cwd = dir.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(title) &&
                            ev.TryGetProperty("type", out var ty) && ty.GetString() == "user" &&
                            ev.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content))
                        {
                            if (content.ValueKind == JsonValueKind.String) title = content.GetString() ?? "";
                            else if (content.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var part in content.EnumerateArray())
                                {
                                    if (part.ValueKind == JsonValueKind.Object &&
                                        part.TryGetProperty("type", out var pt) && pt.GetString() == "text" &&
                                        part.TryGetProperty("text", out var txt))
                                    { title = txt.GetString() ?? ""; break; }
                                }
                            }
                        }
                    }
                    catch (JsonException) { }
                    if (cwd.Length > 0 && title.Length > 0) break;
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (cwd.Length == 0) continue;
            string actual;
            try { actual = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar); }
            catch { continue; }
            if (!StringComparer.OrdinalIgnoreCase.Equals(wanted, actual)) continue;
            title = string.Join(" ", title.Split(['\r','\n','\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (title.Length == 0 || title.StartsWith("<local-command", StringComparison.OrdinalIgnoreCase))
                title = "Conversazione " + id[..8];
            if (title.Length > 90) title = title[..87] + "...";
            found.Add(new HistoryEntry(id, title, actual, File.GetLastWriteTimeUtc(file)));
            if (found.Count >= 250) break;
        }
        return found;
    }
}
