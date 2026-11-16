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
    Console.WriteLine("[PASS] Claude response dedupe; Codex cumulative snapshots/reset; history by project");
}
finally
{
    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
    Environment.SetEnvironmentVariable("CODEX_HOME", null);
    Directory.Delete(home, recursive:true);
}
