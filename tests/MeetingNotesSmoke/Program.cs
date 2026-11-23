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


// Retention tests: WAVs may only be removed after a saved, nonempty TXT.
// No real meeting data, network calls or audio devices are involved.
var retentionRoot = Path.Combine(Path.GetTempPath(), "meeting-retention-tests-" + Guid.NewGuid().ToString("N"));
try
{
    WorkspaceStore.EnsureFolders(retentionRoot);
    var preferences = Path.Combine(retentionRoot, "preferences.json");
    if (RecordingPreferenceStore.Load(preferences).KeepOriginalAudio)
        throw new InvalidOperationException("Audio retention default must be disabled.");

    RecordingPreferenceStore.Save(new RecordingPreferences(true), preferences);
    if (!RecordingPreferenceStore.Load(preferences).KeepOriginalAudio)
        throw new InvalidOperationException("Keep original WAV preference was not persisted.");

    RecordingPreferenceStore.Save(new RecordingPreferences(false), preferences);
    if (RecordingPreferenceStore.Load(preferences).KeepOriginalAudio)
        throw new InvalidOperationException("Disabled preference was not persisted.");
    File.WriteAllText(preferences, "{not-json");
    if (RecordingPreferenceStore.Load(preferences).KeepOriginalAudio)
        throw new InvalidOperationException("Corrupted settings must fall back to disabled.");

    var recordingFolder = Path.Combine(WorkspaceStore.Recordings(retentionRoot), "rec-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(recordingFolder);
    var mic = Path.Combine(recordingFolder, "microfono.wav");
    var pc = Path.Combine(recordingFolder, "audio-pc.wav");
    File.WriteAllBytes(mic, new byte[] { 82, 73, 70, 70, 1, 2 });
    File.WriteAllBytes(pc, new byte[] { 82, 73, 70, 70, 3, 4 });
    var other = Path.Combine(recordingFolder, "unrelated.txt");
    File.WriteAllText(other, "This should never be deleted.");
    var transcript = Path.Combine(WorkspaceStore.Transcripts(retentionRoot), "trascrizione.txt");
    var paths = new string?[] { mic, pc };

    var refusedMissing = false;
    try
    {
        RecordingAudioRetention.DeleteAfterSuccessfulTranscript(
            WorkspaceStore.Recordings(retentionRoot), recordingFolder, paths,
            WorkspaceStore.Transcripts(retentionRoot), transcript, false);
    }
    catch (InvalidOperationException) { refusedMissing = true; }
    if (!refusedMissing || !File.Exists(mic) || !File.Exists(pc))
        throw new InvalidOperationException("WAVs were not preserved after missing transcript.");

    File.WriteAllText(transcript, "");
    var refusedEmpty = false;
    try
    {
        RecordingAudioRetention.DeleteAfterSuccessfulTranscript(
            WorkspaceStore.Recordings(retentionRoot), recordingFolder, paths,
            WorkspaceStore.Transcripts(retentionRoot), transcript, false);
    }
    catch (InvalidOperationException) { refusedEmpty = true; }
    if (!refusedEmpty || !File.Exists(mic))
        throw new InvalidOperationException("WAVs were not preserved after empty transcript.");

    File.WriteAllText(transcript, "[00:00:01] [MICROFONO] Questa è una prova.");
    var kept = RecordingAudioRetention.DeleteAfterSuccessfulTranscript(
        WorkspaceStore.Recordings(retentionRoot), recordingFolder, paths,
        WorkspaceStore.Transcripts(retentionRoot), transcript, true);
    if (kept != 0 || !File.Exists(mic) || !File.Exists(pc))
        throw new InvalidOperationException("Keep original audio option ignored.");

    var foreign = Path.Combine(retentionRoot, "external.wav");
    File.WriteAllBytes(foreign, new byte[] { 1, 2, 3 });
    var refusedForeign = false;
    try
    {
        RecordingAudioRetention.DeleteAfterSuccessfulTranscript(
            WorkspaceStore.Recordings(retentionRoot), recordingFolder, new string?[] { mic, foreign },
            WorkspaceStore.Transcripts(retentionRoot), transcript, false);
    }
    catch (InvalidOperationException) { refusedForeign = true; }
    if (!refusedForeign || !File.Exists(mic) || !File.Exists(pc) || !File.Exists(foreign))
        throw new InvalidOperationException("External WAV safety gate failed.");

    var deleted = RecordingAudioRetention.DeleteAfterSuccessfulTranscript(
        WorkspaceStore.Recordings(retentionRoot), recordingFolder, paths,
        WorkspaceStore.Transcripts(retentionRoot), transcript, false);
    if (deleted != 2 || File.Exists(mic) || File.Exists(pc) ||
        !File.Exists(transcript) || !File.Exists(other) || !Directory.Exists(recordingFolder))
        throw new InvalidOperationException("Successful transcript did not trigger scoped WAV cleanup.");

    Console.WriteLine("[PASS] Default-off audio retention, settings persistence, missing/empty TXT safety, import protection and cleanup.");
}
finally
{
    if (Directory.Exists(retentionRoot))
        Directory.Delete(retentionRoot, recursive: true);
}
