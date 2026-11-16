using System.IO;
using System.Text.Json;

namespace ClaudeTerminal.Host;

public sealed record UsageRow(
    string Provider, string Project, string Session, string Model,
    DateTime LastActivity, long Input, long CacheRead, long CacheWrite,
    long Output, long Reasoning)
{
    public long Total => Provider == "Codex"
        ? Input + Output // Cached input is included in Codex input.
        : Input + CacheRead + CacheWrite + Output;
    public string DateLabel => LastActivity.ToString("dd/MM/yyyy HH:mm");
}

public static class UsageScanner
{
    public static IReadOnlyList<UsageRow> Scan(int maxFilesPerProvider = int.MaxValue)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new List<UsageRow>();
        var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(claudeHome)) claudeHome = Path.Combine(home, ".claude");
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(home, ".codex");

        foreach (var path in FindFiles(new[] { Path.Combine(claudeHome, "projects") }, maxFilesPerProvider))
        {
            var item = ReadClaude(path);
            if (item is not null && item.Total > 0) result.Add(item);
        }

        // The same Codex rollout can appear under sessions and archived_sessions.
        // Prefer the newest copy, so counting a moved session never doubles usage.
        var codexSessions = new Dictionary<string, UsageRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in FindFiles(new[] {
            Path.Combine(codexHome, "sessions"),
            Path.Combine(codexHome, "archived_sessions") }, maxFilesPerProvider))
        {
            var item = ReadCodex(path);
            if (item is null || item.Total <= 0) continue;
            codexSessions.TryAdd(item.Session, item);
        }
        result.AddRange(codexSessions.Values);
        return result.OrderByDescending(x => x.LastActivity).ToArray();
    }

    private static IEnumerable<string> FindFiles(IEnumerable<string> roots, int maximum)
    {
        var paths = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                paths.AddRange(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return paths.OrderByDescending(File.GetLastWriteTimeUtc).Take(maximum);
    }

    private static bool TryGetString(JsonElement data, string key, out string value)
    {
        value = "";
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(key, out var prop) ||
            prop.ValueKind != JsonValueKind.String) return false;
        value = prop.GetString() ?? "";
        return value.Length > 0;
    }

    private static long Number(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
    }

    private sealed record TokenVector(long Input, long CacheRead, long CacheWrite, long Output, long Reasoning)
    {
        public static TokenVector ReadClaude(JsonElement u) => new(
            Number(u, "input_tokens"),
            Number(u, "cache_read_input_tokens"),
            Number(u, "cache_creation_input_tokens"),
            Number(u, "output_tokens"), 0);
        public static TokenVector ReadCodex(JsonElement u) => new(
            Number(u, "input_tokens"), Number(u, "cached_input_tokens"),
            Number(u, "cache_write_input_tokens"), Number(u, "output_tokens"),
            Number(u, "reasoning_output_tokens"));
        public TokenVector MergeMax(TokenVector b) => new(
            Math.Max(Input, b.Input), Math.Max(CacheRead, b.CacheRead),
            Math.Max(CacheWrite, b.CacheWrite), Math.Max(Output, b.Output),
            Math.Max(Reasoning, b.Reasoning));
        public TokenVector Plus(TokenVector b) => new(
            Input + b.Input, CacheRead + b.CacheRead, CacheWrite + b.CacheWrite,
            Output + b.Output, Reasoning + b.Reasoning);
        public TokenVector PositiveDelta(TokenVector previous) => new(
            Diff(Input, previous.Input), Diff(CacheRead, previous.CacheRead),
            Diff(CacheWrite, previous.CacheWrite), Diff(Output, previous.Output),
            Diff(Reasoning, previous.Reasoning));
        private static long Diff(long now, long before) => now >= before ? now - before : now;
    }

    private static UsageRow? ReadClaude(string path)
    {
        // Claude writes one assistant record per output content block. A single
        // API response can appear many times with identical requestId/message.id,
        // so choose the largest usage vector per response, never sum every row.
        var responses = new Dictionary<string, TokenVector>(StringComparer.Ordinal);
        var model = "—";
        var cwd = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            var withoutId = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!TryGetString(root, "type", out var kind) || kind != "assistant") continue;
                    if (TryGetString(root, "cwd", out var pathCwd)) cwd = pathCwd;
                    if (!root.TryGetProperty("message", out var msg) ||
                        msg.ValueKind != JsonValueKind.Object ||
                        !msg.TryGetProperty("usage", out var usage) ||
                        usage.ValueKind != JsonValueKind.Object) continue;

                    if (TryGetString(msg, "model", out var m) && !m.StartsWith("<", StringComparison.Ordinal))
                        model = m;
                    var id = TryGetString(msg, "id", out var msgId) ? msgId : "";
                    var request = TryGetString(root, "requestId", out var req) ? req : "";
                    var key = id.Length > 0 ? id + "/" + request :
                              request.Length > 0 ? request : "unknown-" + (++withoutId);
                    var tokens = TokenVector.ReadClaude(usage);
                    responses[key] = responses.TryGetValue(key, out var old) ? old.MergeMax(tokens) : tokens;
                }
                catch (JsonException) { }
            }
            var sum = responses.Values.Aggregate(new TokenVector(0,0,0,0,0), (a,b) => a.Plus(b));
            var session = Path.GetFileNameWithoutExtension(path);
            var project = ProjectName(cwd);
            return new UsageRow("Claude", project, session, model, File.GetLastWriteTime(path),
                sum.Input, sum.CacheRead, sum.CacheWrite, sum.Output, 0);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static UsageRow? ReadCodex(string path)
    {
        string cwd = "";
        string id = Path.GetFileNameWithoutExtension(path);
        var model = "—";
        var previous = new TokenVector(0,0,0,0,0);
        var total = previous;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.Contains("\"session_meta\"", StringComparison.Ordinal) &&
                    !line.Contains("\"turn_context\"", StringComparison.Ordinal) &&
                    !line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!TryGetString(root, "type", out var type) ||
                        !root.TryGetProperty("payload", out var payload) ||
                        payload.ValueKind != JsonValueKind.Object) continue;
                    switch (type)
                    {
                        case "session_meta":
                            if (TryGetString(payload, "cwd", out var p)) cwd = p;
                            if (TryGetString(payload, "id", out var realId)) id = realId;
                            break;
                        case "turn_context":
                            if (TryGetString(payload, "model", out var turnModel)) model = turnModel;
                            break;
                        case "event_msg":
                            if (!TryGetString(payload, "type", out var action) || action != "token_count" ||
                                !payload.TryGetProperty("info", out var info) ||
                                info.ValueKind != JsonValueKind.Object ||
                                !info.TryGetProperty("total_token_usage", out var usage) ||
                                usage.ValueKind != JsonValueKind.Object) continue;
                            var current = TokenVector.ReadCodex(usage);
                            // Repeated cumulative snapshots contribute zero.
                            // If the counter resets after compaction, add the new segment.
                            total = total.Plus(current.PositiveDelta(previous));
                            previous = current;
                            break;
                    }
                }
                catch (JsonException) { }
            }
            return new UsageRow("Codex", ProjectName(cwd), id, model,
                File.GetLastWriteTime(path), total.Input, total.CacheRead,
                total.CacheWrite, total.Output, total.Reasoning);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string ProjectName(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "(progetto non registrato)";
        try { return Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return "(progetto non registrato)"; }
    }
}
