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

    // The sidebar polls active sessions frequently. Reparse only transcripts
    // that have changed instead of scanning long conversations every 15 seconds.
    private sealed record TranscriptMetadata(string Cwd, string FirstPrompt, string CustomTitle,
        string AiTitle, string Summary);
    private sealed record CachedTranscript(long Length, DateTime UpdatedUtc, TranscriptMetadata Metadata);
    private sealed record IndexedTitle(string CustomTitle, string Summary);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedTranscript> TranscriptCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<HistoryEntry> ForProject(string directory, int maxFiles = 1000)
    {
        var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(claudeHome))
            claudeHome = Path.Combine(Home, ".claude");
        var root = Path.Combine(claudeHome, "projects");
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
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        var indexes = new Dictionary<string, Dictionary<string, IndexedTitle>>(StringComparer.OrdinalIgnoreCase);
        var found = new List<HistoryEntry>();
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParse(id, out _)) continue;

            var metadata = ReadTranscript(file, id);
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Cwd)) continue;
            string actual;
            try { actual = Path.GetFullPath(metadata.Cwd).TrimEnd(Path.DirectorySeparatorChar); }
            catch { continue; }
            if (!StringComparer.OrdinalIgnoreCase.Equals(wanted, actual)) continue;

            var folder = Path.GetDirectoryName(file)!;
            if (!indexes.TryGetValue(folder, out var index))
            {
                index = ReadIndex(Path.Combine(folder, "sessions-index.json"));
                indexes.Add(folder, index);
            }
            index.TryGetValue(id, out var indexed);
            // Explicit rename wins, then the AI-generated name, then summaries.
            // Never substitute a generic first prompt for a stored title.
            var title = FirstNonEmpty(metadata.CustomTitle, indexed?.CustomTitle,
                metadata.AiTitle, indexed?.Summary, metadata.Summary, metadata.FirstPrompt);
            title = NormalizeTitle(title);
            if (title.Length == 0 || title.StartsWith("<local-command", StringComparison.OrdinalIgnoreCase))
                title = "Conversazione " + id[..8];
            if (title.Length > 90) title = title[..87] + "...";
            found.Add(new HistoryEntry(id, title, actual, File.GetLastWriteTimeUtc(file)));
            if (found.Count >= 250) break;
        }
        return found;
    }

    private static TranscriptMetadata? ReadTranscript(string file, string sessionId)
    {
        try
        {
            var info = new FileInfo(file);
            if (TranscriptCache.TryGetValue(file, out var cached) &&
                cached.Length == info.Length && cached.UpdatedUtc == info.LastWriteTimeUtc)
                return cached.Metadata;

            var cwd = "";
            var firstPrompt = "";
            var customTitle = "";
            var aiTitle = "";
            var summary = "";
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // Once the project and first prompt are known, only parse title
                // events; this avoids deserializing large assistant/tool responses.
                if (cwd.Length > 0 && firstPrompt.Length > 0 &&
                    !line.Contains("custom-title", StringComparison.Ordinal) &&
                    !line.Contains("ai-title", StringComparison.Ordinal) &&
                    !line.Contains("\"summary\"", StringComparison.Ordinal))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var ev = doc.RootElement;
                    if (ev.ValueKind != JsonValueKind.Object) continue;
                    if (cwd.Length == 0 && TryString(ev, "cwd", out var path))
                        cwd = path;
                    if (!TryString(ev, "type", out var type)) continue;

                    if (type == "user" && firstPrompt.Length == 0 &&
                        ev.TryGetProperty("message", out var msg) &&
                        msg.ValueKind == JsonValueKind.Object &&
                        msg.TryGetProperty("content", out var content))
                    {
                        if (content.ValueKind == JsonValueKind.String)
                            firstPrompt = content.GetString() ?? "";
                        else if (content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in content.EnumerateArray())
                            {
                                if (TryString(part, "type", out var partType) && partType == "text" &&
                                    TryString(part, "text", out var text))
                                { firstPrompt = text; break; }
                            }
                        }
                    }
                    else if (type is "custom-title" or "ai-title" or "summary")
                    {
                        // Ignore metadata stamped for another session after /clear.
                        if (TryString(ev, "sessionId", out var owner) &&
                            !owner.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (type == "custom-title" && TryString(ev, "customTitle", out var userTitle))
                            customTitle = userTitle;
                        else if (type == "ai-title" && TryString(ev, "aiTitle", out var generatedTitle))
                            aiTitle = generatedTitle;
                        else if (type == "summary" && TryString(ev, "summary", out var shortSummary))
                            summary = shortSummary;
                    }
                }
                catch (JsonException) { } // Ignore partially written JSONL records.
            }
            var metadata = new TranscriptMetadata(cwd, firstPrompt, customTitle, aiTitle, summary);
            TranscriptCache[file] = new CachedTranscript(info.Length, info.LastWriteTimeUtc, metadata);
            return metadata;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static Dictionary<string, IndexedTitle> ReadIndex(string path)
    {
        var titles = new Dictionary<string, IndexedTitle>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return titles;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array) return titles;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!TryString(entry, "sessionId", out var id)) continue;
                titles[id] = new IndexedTitle(GetString(entry, "customTitle"),
                    GetString(entry, "summary"));
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return titles;
    }

    private static bool TryString(JsonElement obj, string property, out string value)
    {
        value = GetString(obj, property);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetString(JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    private static string NormalizeTitle(string title)
        => string.Join(" ", title.Split(['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries)).Trim();
}
