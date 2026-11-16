using System.Diagnostics;
using System.Text;
using ClaudeTerminal.Host;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Expected path to Win32KeyProbe.exe");

const string win32CtrlJ = "\x1b[74;36;10;1;8;1_\x1b[74;36;10;0;8;1_";
var log = Environment.GetEnvironmentVariable("KEYPROBE_LOG");
var output = new StringBuilder();
var guard = new object();
var path = Path.GetFullPath(args[0]);
var directory = Path.GetDirectoryName(path)!;

async Task WaitFor(string expected, int timeoutSeconds = 15)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
    {
        lock (guard)
        {
            if (output.ToString().Contains(expected, StringComparison.Ordinal)) return;
        }
        await Task.Delay(80);
    }
    lock (guard)
        throw new TimeoutException($"Missing {expected}. Output: {output.ToString().Replace("\x1b", "[ESC]")}");
}

try
{
    // Launch the probe under cmd.exe so Windows creates it in the same
    // pseudo-console, rather than inheriting the headless runner's handles.
    var arguments = "/Q /C \"\"" + path + "\"\"";
    using var pty = new ConPtySession("cmd.exe", arguments, directory);
    pty.OutputReceived += text => { lock (guard) output.Append(text); };
    await pty.StartAsync(80, 24);
    await WaitFor("KEYPROBE_READY");
    await pty.WriteAsync(win32CtrlJ);
    await WaitFor("KEYPROBE_OK");
    if (log is not null) File.WriteAllText(log, "[PASS] ConPTY decoded Win32 Ctrl+J key record.");
}
catch (Exception ex)
{
    if (log is not null) File.WriteAllText(log, "[FAIL] " + ex);
    throw;
}
