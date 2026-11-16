using System.Diagnostics;
using System.Text;
using ClaudeTerminal.Host;

static async Task WaitFor(Func<bool> condition, string label, int seconds = 20)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        if (condition()) return;
        await Task.Delay(80);
    }
    throw new TimeoutException("Timed out: " + label);
}

var smokeLog = Environment.GetEnvironmentVariable("CLAUDE_CONPTY_SMOKE_LOG");
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (string.IsNullOrWhiteSpace(smokeLog)) return;
    try { File.WriteAllText(smokeLog, "[FAIL] " + args.ExceptionObject); }
    catch { }
};

var project = Environment.CurrentDirectory;
using var first = new ConPtySession("cmd.exe", "/Q /K", project);
using var second = new ConPtySession("cmd.exe", "/Q /K", project);
var firstOut = new StringBuilder();
var secondOut = new StringBuilder();
var lockOne = new object();
var lockTwo = new object();
first.OutputReceived += text => { lock (lockOne) { firstOut.Append(text); } };
second.OutputReceived += text => { lock (lockTwo) { secondOut.Append(text); } };

bool HasOne(string value) { lock(lockOne) return firstOut.ToString().Contains(value, StringComparison.Ordinal); }
bool HasTwo(string value) { lock(lockTwo) return secondOut.ToString().Contains(value, StringComparison.Ordinal); }

await first.StartAsync(90, 26);
await second.StartAsync(90, 26);
first.Resize(100, 35);
second.Resize(85, 28);

try
{
    // cmd.exe avoids PowerShell/PSReadLine initialization complexities:
    // this tests the actual independent ConPTY transport and live input.
    await Task.Delay(1000);
    await first.WriteAsync("echo A_READY\r\n");
    await second.WriteAsync("echo B_READY\r\n");
    await WaitFor(() => HasOne("A_READY") && HasTwo("B_READY"), "two independent cmd.exe ConPTY responses", 12);

    if (HasOne("B_READY") || HasTwo("A_READY"))
        throw new Exception("PTY output leaked between processes");

    first.Dispose();
    await second.WriteAsync("echo B_STILL_ALIVE\r\n");
    await WaitFor(() => HasTwo("B_STILL_ALIVE"), "second ConPTY stays alive after first disposed", 12);
}
catch
{
    lock (lockOne) Console.WriteLine("[DIAG] PTY A (raw): " + firstOut.ToString().Replace("\u001b", "[ESC]")[..Math.Min(2000, firstOut.Length)]);
    lock (lockTwo) Console.WriteLine("[DIAG] PTY B (raw): " + secondOut.ToString().Replace("\u001b", "[ESC]")[..Math.Min(2000, secondOut.Length)]);
    throw;
}

if (!string.IsNullOrWhiteSpace(smokeLog))
    File.WriteAllText(smokeLog, "[PASS] Two independent interactive ConPTY processes, input/output, resize and lifecycle");

