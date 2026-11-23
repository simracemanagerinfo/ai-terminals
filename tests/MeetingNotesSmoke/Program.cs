using System.IO;
using System.Globalization;
using System.Text;
using MeetingNotes;

static int Offset(string header, string key)
{
    var pos = header.IndexOf(key + ":", StringComparison.Ordinal);
    if (pos < 0)
        throw new InvalidOperationException("Missing CF_HTML field: " + key);
    return int.Parse(header.Substring(pos + key.Length + 1, 10), CultureInfo.InvariantCulture);
}

var body = "<p>Riunione con Federica: qualità, attività, città &amp; note</p>" +
    "<table><tr><td>À è ù — prova</td></tr></table>";
var cf = OutlookClipboard.BuildCfHtml("<!doctype html><html><head></head><body style=\"font-family:Arial\">" + body + "</body></html>");
var bytes = Encoding.UTF8.GetBytes(cf);
var header = cf[..cf.IndexOf("<html>", StringComparison.Ordinal)];
var startHtml = Offset(header, "StartHTML");
var endHtml = Offset(header, "EndHTML");
var start = Offset(header, "StartFragment");
var end = Offset(header, "EndFragment");

if (startHtml < 0 || start <= startHtml || end <= start || endHtml <= end || endHtml != bytes.Length)
    throw new InvalidOperationException("CF_HTML offset range is invalid.");

var segment = Encoding.UTF8.GetString(bytes[start..end]);
if (segment != body)
    throw new InvalidOperationException("CF_HTML UTF-8 fragment offsets do not match the email body.");

if (!Encoding.UTF8.GetString(bytes[startHtml..endHtml]).Contains("<!--StartFragment-->"))
    throw new InvalidOperationException("CF_HTML fragment marker is missing.");

Console.WriteLine("[PASS] Windows CF_HTML offsets, Unicode and Outlook fragment.");


// Test percorso configurabile e testo TXT UTF-8, senza file nel repo.
var tmp = Path.Combine(Path.GetTempPath(), "meeting-notes-workspace-test-" + Guid.NewGuid().ToString("N"));
try
{
    WorkspaceStore.EnsureFolders(tmp);
    if (!Directory.Exists(WorkspaceStore.Transcripts(tmp)) ||
        !Directory.Exists(WorkspaceStore.Recordings(tmp)) ||
        !Directory.Exists(WorkspaceStore.Minutes(tmp)))
        throw new InvalidOperationException("Workspace folder initialization failed.");

    var transcript = "09:00 - Sara: prova microfono, città, qualità.\n10:00 - Paolo: attività per martedì.";
    var saved = WorkspaceStore.CreateTranscript(tmp, "Prova: Riunione / Team", transcript);
    if (!File.Exists(saved) || File.ReadAllText(saved, Encoding.UTF8) != transcript)
        throw new InvalidOperationException("Transcript UTF-8 was not stored correctly.");
    if (!Path.GetFileName(saved).EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Transcript filename does not end in TXT.");

    var second = WorkspaceStore.CreateTranscript(tmp, "Prova: Riunione / Team", transcript);
    if (saved == second)
        throw new InvalidOperationException("Transcript creation overwrote an existing TXT.");

    // Simula un worktree Git: il salvataggio di materiale sensibile deve essere bloccato.
    var gitRoot = Path.Combine(tmp, "test-repository");
    var nested = Path.Combine(gitRoot, "meeting-data");
    Directory.CreateDirectory(nested);
    File.WriteAllText(Path.Combine(gitRoot, ".git"), "gitdir: fake-for-test");
    var denied = false;
    try { WorkspaceStore.ValidateRoot(nested); }
    catch (InvalidOperationException) { denied = true; }
    if (!denied) throw new InvalidOperationException("Git worktree protection missing.");

    Console.WriteLine("[PASS] Local workspace folders, TXT UTF-8, unique names and Git safety.");
}
finally
{
    if (Directory.Exists(tmp))
        Directory.Delete(tmp, recursive: true);
}
