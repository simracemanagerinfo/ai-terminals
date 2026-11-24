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

    // Each Codex window has its own short completion-marker file.
    // Read new turns exactly once; multiple completions are allowed.
    var signalsPath = Path.Combine(home, "codex-turns.signals");
    int readCount = 0;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    if (CodexCompletionSignals.ReadNewTurnIds(signalsPath, ref readCount, seen).Count != 0)
        throw new Exception("Missing signal file should produce no notifications");
    File.WriteAllLines(signalsPath, ["turn-1", "turn-1"]);
    var first = CodexCompletionSignals.ReadNewTurnIds(signalsPath, ref readCount, seen);
    if (first.Count != 1 || first[0] != "turn-1")
        throw new Exception("Duplicate completion event was not suppressed");
    // The CLI's first completed turn exposes its persistent thread ID.
    var threadId = Guid.NewGuid().ToString("D");
    if (!CodexCompletionSignals.TryGetThreadId("turn-1|" + threadId, out var parsedThread) ||
        parsedThread != threadId ||
        CodexCompletionSignals.TryGetThreadId("turn-1", out _) ||
        CodexCompletionSignals.TryGetThreadId("turn-1|not-a-guid", out _))
        throw new Exception("Codex completion thread-id parsing failed.");
    // A new Codex chat can be assigned to a user folder only once its real
    // thread identity is known. Do not invent a GUID for Codex.
    var newCodexThread = Guid.NewGuid().ToString("D");
    var codexAssignments = SessionOrganization.Open(Path.Combine(home, "codex-new-chat-folders.json"));
    codexAssignments.Create(project, "Feature work");
    var targetId = codexAssignments.Folders(project).Single().Id;
    codexAssignments.Move(project, newCodexThread, targetId);
    if (SessionOrganization.Open(Path.Combine(home, "codex-new-chat-folders.json"))
            .FolderOf(project, newCodexThread) != targetId)
        throw new Exception("Codex assigned thread did not persist in chosen folder.");
    File.AppendAllLines(signalsPath, ["turn-2", "turn-3"]);
    var second = CodexCompletionSignals.ReadNewTurnIds(signalsPath, ref readCount, seen);
    if (second.Count != 2 || second[0] != "turn-2" || second[1] != "turn-3" ||
        CodexCompletionSignals.ReadNewTurnIds(signalsPath, ref readCount, seen).Count != 0)
        throw new Exception("Incremental turn completions were lost or replayed");

    // Live conversations must stay in their selected project folder both
    // before and after receiving their real stable session ID.
    var routingFile = Path.Combine(home, "live-folders.json");
    var routing = SessionOrganization.Open(routingFile);
    var workFolder = routing.Create(project, "Work in progress");
    var otherFolder = routing.Create(project, "Reviews");
    var claudeActiveId = Guid.NewGuid().ToString("D");
    var codexActiveId = Guid.NewGuid().ToString("D");
    if (LiveFolderRouting.EffectiveFolder(
            routing, project, null, workFolder.Id) != workFolder.Id)
        throw new Exception("Pending Codex session was not displayed in its folder.");
    if (LiveFolderRouting.EffectiveFolder(
            routing, project, null, null) != SessionOrganization.Unfiled)
        throw new Exception("Unassigned live chat should appear in Unorganized.");
    routing.Move(project, claudeActiveId, workFolder.Id);
    if (LiveFolderRouting.EffectiveFolder(
            routing, project, claudeActiveId, null) != workFolder.Id)
        throw new Exception("Claude live session lost its persisted folder.");
    routing.Move(project, codexActiveId, otherFolder.Id);
    var routingReopened = SessionOrganization.Open(routingFile);
    if (LiveFolderRouting.EffectiveFolder(
            routingReopened, project, codexActiveId, null) != otherFolder.Id)
        throw new Exception("Codex live folder did not survive persistence/reopen.");
    if (LiveFolderRouting.EffectiveFolder(
            routingReopened, project, claudeActiveId, otherFolder.Id) != otherFolder.Id)
        throw new Exception("Pending move must take precedence while completing.");
    routingReopened.RemoveFolder(project, workFolder.Id);
    if (LiveFolderRouting.EffectiveFolder(
            routingReopened, project, claudeActiveId, null) != SessionOrganization.Unfiled)
        throw new Exception("Removing a folder stranded a running conversation.");
    routingReopened.Move(project, codexActiveId, SessionOrganization.Bin);
    if (LiveFolderRouting.EffectiveFolder(
            routingReopened, project, codexActiveId, null) != SessionOrganization.Unfiled)
        throw new Exception("An active session should stay accessible outside the Bin.");
    var secondLiveProject = Path.Combine(home, "Separate Live Project");
    Directory.CreateDirectory(secondLiveProject);
    if (LiveFolderRouting.EffectiveFolder(
            routingReopened, secondLiveProject, codexActiveId, null) != SessionOrganization.Unfiled)
        throw new Exception("Live conversation folders must be isolated per project.");

    // Virtual folders and Bin must survive reopening without touching either
    // agent's transcript, index or CLI configuration.
    var organizationPath = Path.Combine(home, "ui-folders.json");
    var folders = SessionOrganization.Open(organizationPath);
    var architecture = folders.Create(project, "Architecture");
    var development = folders.Create(project, "Development");
    try
    {
        folders.Create(project, "Secchio");
        throw new Exception("Reserved Bin name was accepted as a user folder.");
    }
    catch (ArgumentException) { }
    folders.Move(project, customId, architecture.Id);
    folders.Move(project, codexId, development.Id);
    folders.Move(project, customId, SessionOrganization.Bin);
    if (folders.FolderOf(project, customId) != SessionOrganization.Bin)
        throw new Exception("Bin placement not saved.");
    var reopenedFolders = SessionOrganization.Open(organizationPath);
    if (reopenedFolders.FolderOf(project, customId) != SessionOrganization.Bin ||
        reopenedFolders.Folders(project).Count != 2)
        throw new Exception("Bin or folder persistence failed.");
    reopenedFolders.Restore(project, customId);
    if (reopenedFolders.FolderOf(project, customId) != architecture.Id)
        throw new Exception("Restore did not return to the previous folder.");
    reopenedFolders.Move(project, customId, SessionOrganization.Bin);
    reopenedFolders.RemoveFolder(project, architecture.Id);
    reopenedFolders.Restore(project, customId);
    if (reopenedFolders.FolderOf(project, customId) != SessionOrganization.Unfiled)
        throw new Exception("Removing a folder did not preserve a safe restore fallback.");
    var secondProject = Path.Combine(home, "Other Project");
    Directory.CreateDirectory(secondProject);
    if (reopenedFolders.Folders(secondProject).Count != 0 ||
        reopenedFolders.FolderOf(secondProject, codexId) != SessionOrganization.Unfiled)
        throw new Exception("Project organization leaked into a different project.");
    if (!File.Exists(ClaudePath(customId)) || !File.Exists(codexPath) ||
        !File.Exists(indexPath) || !File.Exists(Path.Combine(claudeFolder, "sessions-index.json")))
        throw new Exception("A virtual folder modified a provider transcript or index.");
    File.WriteAllText(organizationPath, "{broken-data");
    try
    {
        SessionOrganization.Open(organizationPath);
        throw new Exception("Corrupt organization metadata was silently overwritten.");
    }
    catch (InvalidDataException) { }
    if (File.ReadAllText(organizationPath) != "{broken-data")
        throw new Exception("Corrupted organization data was changed.");
    if (UiText.T("bin") is not ("Bin" or "Secchio") ||
        UiText.T("moveToBin") is not ("Move to Bin" or "Sposta nel Secchio") ||
        UiText.T("unknown-code") != "unknown-code")
        throw new Exception("UI language fallback or Bin translation is invalid.");

    var attention = new AttentionSignals();
    var otherAttention = new AttentionSignals();
    if (attention.Read("Do you want to continue?") != 0 ||
        attention.Read("\a") != 0 ||
        attention.Read("\x1b]9;4;1;50\a") != 0 ||
        attention.Read("\x1b]0;title\a") != 0 ||
        attention.Read("\x1b") != 0 ||
        otherAttention.Read("]9;Choose an option\a") != 0 ||
        attention.Read("]9;Choose") != 0 ||
        attention.Read(" an option\a") != 1 ||
        attention.Read("\x1b]9;Approval requested\x1b") != 0 ||
        attention.Read("\\") != 1 ||
        attention.Read("\x1b]9;" + new string('x', 9000) + "\a") != 0 ||
        attention.Read("\x1b]9;Another question\a") != 1)
        throw new Exception("Attention OSC parser failed on split, unrelated, oversized or cross-session output.");
    if (!AttentionSignals.IsAttentionMarker("input:" + Guid.NewGuid().ToString("N")) ||
        AttentionSignals.IsAttentionMarker("input:invalid") ||
        AttentionSignals.IsAttentionMarker("turn-1") ||
        AttentionSignals.IsResponseInput("\x1b[A") ||
        !AttentionSignals.IsResponseInput("\r") ||
        !AttentionSignals.IsResponseInput("\x03"))
        throw new Exception("Attention marker or response input filtering failed.");
    Console.WriteLine("[PASS] Input-required notification parser, chunk boundaries, session isolation and filtering");

    Console.WriteLine("[PASS] Claude response dedupe; Codex token snapshots/reset; Claude stored titles, explicit Codex titles and response_item fallback");

}
finally
{
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
    Environment.SetEnvironmentVariable("CODEX_HOME", null);
    Directory.Delete(home, recursive:true);
}
