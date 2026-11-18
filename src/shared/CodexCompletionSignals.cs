using System.IO;

namespace ClaudeTerminal.Host;

/// <summary>
/// Reads only completion markers from the private per-process Codex hook.
/// Markers never include transcript contents, prompts or completion text.
/// </summary>
public static class CodexCompletionSignals
{
    public static IReadOnlyList<string> ReadNewTurnIds(string? path, ref int readCount, HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return [];
        try
        {
            // One short line per completed turn. This file belongs to one live
            // process and is removed when that terminal closes.
            var lines = File.ReadAllLines(path);
            if (lines.Length < readCount) readCount = 0;
            var newTurns = new List<string>();
            for (var i = readCount; i < lines.Length; i++)
            {
                var id = lines[i].Trim();
                if (id.Length is > 0 and <= 128 && seen.Add(id))
                    newTurns.Add(id);
            }
            readCount = lines.Length;
            return newTurns;
        }
        catch (IOException) { return []; } // Hook may be appending a new line.
        catch (UnauthorizedAccessException) { return []; }
    }
}
