using System.Text.Json;
using ClaudeTerminal.Host;

var home = Path.Combine(Path.GetTempPath(), "claude-codex-usage-" + Guid.NewGuid().ToString("N"));
var project = Path.Combine(home, "Project One");
Directory.CreateDirectory(project);
var claudeRoot = Path.Combine(home, "claude");
var codexRoot = Path.Combine(home, "codex");
Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeRoot);
Environment.SetEnvironmentVariable("CODEX_HOME", codexRoot);
try
{
    var claudePath = Path.Combine(claudeRoot, "projects", "-test", Guid.NewGuid().ToString("D") + ".jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(claudePath)!);
    object ClaudeUsage(int output) => new {
        type="assistant", cwd=project, requestId="req-one",
        message=new {
            id="msg-one", model="claude-opus-test",
            usage=new {
                input_tokens=5, cache_creation_input_tokens=10,
                cache_read_input_tokens=100, output_tokens=output
            }
        }
    };
    File.WriteAllLines(claudePath, [
        JsonSerializer.Serialize(ClaudeUsage(5)),
        JsonSerializer.Serialize(ClaudeUsage(25))
    ]);

    var codexId = Guid.NewGuid().ToString("D");
    var codexPath = Path.Combine(codexRoot,"sessions","2026","11","16","rollout-test.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(codexPath)!);
    object CodexUsage(int input,int cache,int output,int reasoning) => new {
        type="event_msg",
        payload=new {
            type="token_count",
            info=new {
                total_token_usage=new {
                    input_tokens=input, cached_input_tokens=cache,
                    output_tokens=output, reasoning_output_tokens=reasoning,
                    total_tokens=input+output
                }
            }
        }
    };
    File.WriteAllLines(codexPath, [
        JsonSerializer.Serialize(new {type="session_meta", payload=new { id=codexId, cwd=project }}),
        JsonSerializer.Serialize(new {type="event_msg",payload=new {type="user_message",message="Codex sample task"}}),
        JsonSerializer.Serialize(CodexUsage(100,20,10,3)),
        JsonSerializer.Serialize(CodexUsage(100,20,10,3)), // duplicate cumulative snapshot
        JsonSerializer.Serialize(CodexUsage(220,60,30,8)),
        JsonSerializer.Serialize(CodexUsage(40,5,6,2)) // reset after compaction
    ]);

    var rows = UsageScanner.Scan();
    if (rows.Count != 2) throw new Exception("Expected two unique session rows, got " + rows.Count);
    var claude = rows.Single(x => x.Provider == "Claude");
    var codex = rows.Single(x => x.Provider == "Codex");
    if (claude.Total != 140 || claude.Output != 25 || claude.CacheRead != 100)
        throw new Exception($"Claude duplicates counted incorrectly: total={claude.Total} output={claude.Output}");
    if (codex.Input != 260 || codex.CacheRead != 65 || codex.Output != 36 ||
        codex.Reasoning != 10 || codex.Total != 296)
        throw new Exception($"Codex cumulative counters incorrect: {codex}");
    if (claude.Project != project || codex.Project != project)
        throw new Exception("Project attribution mismatch");
    var historical = CodexHistory.ForProject(project);
    if (historical.Count != 1 || historical[0].Id != codexId ||
        historical[0].Provider != "Codex" || historical[0].Title != "Codex sample task")
        throw new Exception("Codex history index failed");
    // Codex versions may persist the first prompt only as response_item.
    var responseId = Guid.NewGuid().ToString("D");
    var responsePath = Path.Combine(codexRoot, "sessions", "2026", "11", "16", "rollout-response.jsonl");
    File.WriteAllLines(responsePath, [
        JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = responseId, cwd = project } }),
        JsonSerializer.Serialize(new { type = "response_item", payload = new {
            type = "message", role = "user",
            content = new[] { new { type = "input_text", text = "First meaningful Codex prompt" } }
        }}),
        JsonSerializer.Serialize(new { type = "response_item", payload = new {
            type = "message", role = "user",
            content = new[] { new { type = "input_text", text = "Later prompt must not replace the title" } }
        }})
    ]);

    var indexPath = Path.Combine(codexRoot, "session_index.jsonl");
    File.WriteAllLines(indexPath, [
        JsonSerializer.Serialize(new { id = codexId, thread_name = "Old session name" }),
        JsonSerializer.Serialize(new { id = codexId, thread_name = "Mixer test: definitive name" }),
        "{invalid-json"
    ]);

    var renamed = CodexHistory.ForProject(project);
    if (renamed.Count != 2 ||
        renamed.Single(x => x.Id == codexId).Title != "Mixer test: definitive name" ||
        renamed.Single(x => x.Id == responseId).Title != "First meaningful Codex prompt")
        throw new Exception("Codex renamed titles or response_item first-message fallback incorrect");

    // Claude can save session titles long after the first user prompt. The
    // terminal must prefer explicit renames, then AI-generated and indexed
    // summaries, and only then the first prompt.
    var claudeFolder = Path.GetDirectoryName(claudePath)!;
    string ClaudePath(string id) => Path.Combine(claudeFolder, id + ".jsonl");
    string ClaudeUser(string prompt) => JsonSerializer.Serialize(new {
        type = "user", cwd = project, message = new { content = prompt }
    });
    var customId = Guid.NewGuid().ToString("D");
    var autoId = Guid.NewGuid().ToString("D");
    var indexId = Guid.NewGuid().ToString("D");
    var fallbackId = Guid.NewGuid().ToString("D");

    var longTranscript = new List<string> { ClaudeUser("First question is not the title") };
    longTranscript.AddRange(Enumerable.Repeat("{\"type\":\"progress\"}", 500));
    longTranscript.Add(JsonSerializer.Serialize(new { type = "ai-title", sessionId = customId,
        aiTitle = "Claude generated title" }));
    longTranscript.Add(JsonSerializer.Serialize(new { type = "custom-title", sessionId = customId,
        customTitle = "Old manual title" }));
    longTranscript.Add("{incomplete-json");
    longTranscript.Add(JsonSerializer.Serialize(new { type = "custom-title", sessionId = customId,
        customTitle = "Final renamed title" }));
    File.WriteAllLines(ClaudePath(customId), longTranscript);
    File.WriteAllLines(ClaudePath(autoId), [
        ClaudeUser("Another long first question"),
        JsonSerializer.Serialize(new { type = "ai-title", sessionId = autoId,
            aiTitle = "AI-generated task title" })
    ]);
    File.WriteAllLines(ClaudePath(indexId), [ClaudeUser("Index prompt should not appear")]);
    File.WriteAllLines(ClaudePath(fallbackId), [ClaudeUser("First prompt fallback")]);

    File.WriteAllText(Path.Combine(claudeFolder, "sessions-index.json"),
        JsonSerializer.Serialize(new { entries = new object[] {
            new { sessionId = customId, customTitle = "Stale indexed title", summary = "Indexed custom summary" },
            new { sessionId = autoId, summary = "Older indexed auto-summary" },
            new { sessionId = indexId, summary = "Indexed session summary" }
        }}));
    string ClaudeTitle(string id) => SessionHistory.ForProject(project).Single(x => x.Id == id).Title;
    if (ClaudeTitle(customId) != "Final renamed title" ||
        ClaudeTitle(autoId) != "AI-generated task title" ||
        ClaudeTitle(indexId) != "Indexed session summary" ||
        ClaudeTitle(fallbackId) != "First prompt fallback")
        throw new Exception("Claude title precedence, long transcript scanning or first-prompt fallback failed");

    // Re-reading an unchanged file is cached; appending a title invalidates it.
    File.AppendAllLines(ClaudePath(autoId), [
        JsonSerializer.Serialize(new { type = "custom-title", sessionId = autoId,
            customTitle = "New title after live rename" })
    ]);
    if (ClaudeTitle(autoId) != "New title after live rename")
        throw new Exception("Claude live title refresh did not invalidate the transcript cache");

    Console.WriteLine("[PASS] Claude response dedupe; Codex token snapshots/reset; Claude stored titles, explicit Codex titles and response_item fallback");

}
finally
{
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
    Environment.SetEnvironmentVariable("CODEX_HOME", null);
    Directory.Delete(home, recursive:true);
}
