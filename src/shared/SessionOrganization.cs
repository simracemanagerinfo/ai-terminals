using System.IO;
using System.Text.Json;

namespace ClaudeTerminal.Host;

/// <summary>
/// Local UI-only organization. Never moves, edits or deletes CLI transcripts.
/// Every agent has its own store, and folders are scoped to a project path.
/// </summary>
public sealed class SessionOrganization
{
    public const string Unfiled = "";
    public const string Bin = "__bin__";

    public sealed record FolderInfo(string Id, string Name);

    public sealed class Placement
    {
        public string FolderId { get; set; } = Unfiled;
        public string PreviousFolderId { get; set; } = Unfiled;
    }

    public sealed class ProjectData
    {
        public List<FolderInfo> Folders { get; set; } = [];
        public Dictionary<string, Placement> Placements { get; set; } = [];
    }

    public sealed class Document
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, ProjectData> Projects { get; set; } = [];
    }

    private readonly string _file;
    private Document _state;

    private SessionOrganization(string file, Document state)
    {
        _file = file;
        _state = state;
    }

    public static SessionOrganization Open(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var data = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
                if (data is null || data.Version != 1 || data.Projects is null)
                    throw new InvalidDataException("Unsupported organization metadata version.");
                foreach (var item in data.Projects.Values)
                {
                    if (item is null || item.Folders is null || item.Placements is null)
                        throw new InvalidDataException("Invalid organization metadata.");
                }
                return new SessionOrganization(path, data);
            }
            catch (JsonException ex)
            {
                // Never overwrite an unreadable user file.
                throw new InvalidDataException("Cannot read organization metadata.", ex);
            }
        }
        return new SessionOrganization(path, new Document());
    }

    public static SessionOrganization ForAgent(string provider)
    {
        if (provider is not ("Claude" or "Codex"))
            throw new ArgumentException("Unsupported provider.", nameof(provider));
        var root = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "PowerShellCustomization");
        return Open(Path.Combine(root, "ai-terminals-" + provider.ToLowerInvariant() + "-folders.json"));
    }

    private static string Key(string project)
        => Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar).ToUpperInvariant();

    private ProjectData? Find(string project)
        => _state.Projects.TryGetValue(Key(project), out var result) ? result : null;

    private ProjectData Ensure(string project)
    {
        var key = Key(project);
        if (!_state.Projects.TryGetValue(key, out var value))
            _state.Projects[key] = value = new ProjectData();
        return value;
    }

    public IReadOnlyList<FolderInfo> Folders(string project)
        => Find(project)?.Folders.ToArray() ?? [];

    public string FolderOf(string project, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsedId)) return Unfiled;
        sessionId = parsedId.ToString("D");
        var data = Find(project);
        return data is not null && data.Placements.TryGetValue(sessionId, out var placement)
            ? placement.FolderId : Unfiled;
    }

    public FolderInfo Create(string project, string name)
    {
        var data = Ensure(project);
        var clean = ValidName(name);
        if (data.Folders.Any(x => x.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A folder with this name already exists.");
        var folder = new FolderInfo(Guid.NewGuid().ToString("N"), clean);
        data.Folders.Add(folder);
        Save();
        return folder;
    }

    public void Rename(string project, string id, string name)
    {
        var data = Ensure(project);
        var current = data.Folders.FindIndex(x => x.Id == id);
        if (current < 0) throw new ArgumentException("Folder not found.");
        var clean = ValidName(name);
        if (data.Folders.Any(x => x.Id != id &&
                                  x.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A folder with this name already exists.");
        data.Folders[current] = data.Folders[current] with { Name = clean };
        Save();
    }

    /// <summary>Removing a virtual folder returns its chats to Unorganized.</summary>
    public void RemoveFolder(string project, string id)
    {
        var data = Ensure(project);
        if (data.Folders.RemoveAll(f => f.Id == id) == 0)
            throw new ArgumentException("Folder not found.");
        foreach (var placement in data.Placements.Values)
        {
            if (placement.FolderId == id) placement.FolderId = Unfiled;
            if (placement.PreviousFolderId == id) placement.PreviousFolderId = Unfiled;
        }
        Save();
    }

    public void Move(string project, string sessionId, string destination)
    {
        if (!Guid.TryParse(sessionId, out var parsedId))
            throw new ArgumentException("Invalid session ID.", nameof(sessionId));
        sessionId = parsedId.ToString("D");
        var data = Ensure(project);
        if (destination != Unfiled && destination != Bin &&
            !data.Folders.Any(x => x.Id == destination))
            throw new ArgumentException("Target folder not found.", nameof(destination));
        if (!data.Placements.TryGetValue(sessionId, out var placement))
            data.Placements[sessionId] = placement = new Placement();

        if (destination == Bin && placement.FolderId != Bin)
            placement.PreviousFolderId = placement.FolderId;
        placement.FolderId = destination;
        if (destination == Unfiled)
            data.Placements.Remove(sessionId);
        Save();
    }

    public void Restore(string project, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsedId)) return;
        sessionId = parsedId.ToString("D");
        var data = Find(project);
        if (data is null || !data.Placements.TryGetValue(sessionId, out var placement) ||
            placement.FolderId != Bin) return;
        var previous = placement.PreviousFolderId;
        if (previous != Unfiled && !data.Folders.Any(f => f.Id == previous))
            previous = Unfiled;
        Move(project, sessionId, previous);
    }

    private static string ValidName(string name)
    {
        var clean = name.Trim();
        if (clean.Length is < 1 or > 60 ||
            clean.IndexOfAny(['\r', '\n', '\t']) >= 0)
            throw new ArgumentException("Folder name must contain 1–60 characters on one line.");
        if (new[] { "Bin", "Secchio", "Unorganized", "Non organizzate" }
            .Any(reserved => reserved.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Reserved folder name.");
        return clean;
    }

    private void Save()
    {
        var parent = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, ".folders-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_state,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
