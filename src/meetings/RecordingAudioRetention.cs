using System.IO;

namespace MeetingNotes;

/// <summary>
/// Deletes only the exact WAV tracks created by this application's recording
/// session, and only after a nonempty transcript has been persisted.
/// Never deletes imported files, old recordings or unrelated folder contents.
/// </summary>
public static class RecordingAudioRetention
{
    public static int DeleteAfterSuccessfulTranscript(
        string recordingsRoot,
        string recordingFolder,
        IEnumerable<string?> capturedTracks,
        string transcriptsRoot,
        string transcriptPath,
        bool keepOriginalAudio)
    {
        if (keepOriginalAudio)
            return 0;

        var canonicalTranscriptRoot = Path.GetFullPath(transcriptsRoot);
        var canonicalTranscript = Path.GetFullPath(transcriptPath);
        if (!IsDirectChild(canonicalTranscriptRoot, canonicalTranscript) ||
            !canonicalTranscript.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(canonicalTranscript) ||
            new FileInfo(canonicalTranscript).Length == 0)
            throw new InvalidOperationException(
                "Il TXT non è stato verificato nella cartella di trascrizione: i WAV sono conservati.");

        var canonicalRecordingsRoot = Path.GetFullPath(recordingsRoot);
        var canonicalFolder = Path.GetFullPath(recordingFolder);
        if (!IsDirectChild(canonicalRecordingsRoot, canonicalFolder) ||
            !Path.GetFileName(canonicalFolder).StartsWith("rec-", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(canonicalFolder))
            throw new InvalidOperationException(
                "La registrazione non è una cartella creata da Meeting Notes: i WAV sono conservati.");

        // Avoid deleting content through junctions and symbolic links.
        if ((File.GetAttributes(canonicalRecordingsRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(canonicalFolder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Cartella audio collegata: cancellazione automatica disabilitata.");

        var paths = capturedTracks
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length is < 1 or > 2)
            throw new InvalidOperationException("Tracce della registrazione non valide.");

        foreach (var path in paths)
        {
            if (!IsDirectChild(canonicalFolder, path) ||
                Path.GetFileName(path) is not ("microfono.wav" or "audio-pc.wav") ||
                !File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Il WAV non corrisponde alla registrazione originale: nessun file cancellato.");
        }

        var deleted = 0;
        foreach (var path in paths)
        {
            File.Delete(path);
            deleted++;
        }

        // Remove only the now-empty session folder. Preserve every other file.
        if (!Directory.EnumerateFileSystemEntries(canonicalFolder).Any())
            Directory.Delete(canonicalFolder);

        return deleted;
    }

    private static bool IsDirectChild(string parent, string fileOrFolder)
    {
        var parentOfCandidate = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(fileOrFolder));
        return parentOfCandidate is not null &&
            string.Equals(Path.TrimEndingDirectorySeparator(parent),
                Path.TrimEndingDirectorySeparator(parentOfCandidate),
                StringComparison.OrdinalIgnoreCase);
    }
}
