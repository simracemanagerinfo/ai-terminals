namespace ClaudeTerminal.Host;

/// <summary>
/// A running conversation belongs to its real project folder, not to a
/// separate transient "Active" folder. Codex may need a pending assignment
/// until its CLI supplies a stable session ID in a completion event.
/// </summary>
public static class LiveFolderRouting
{
    public static string EffectiveFolder(
        SessionOrganization? organization, string project,
        string? persistentSessionId, string? pendingFolderId)
    {
        var folder = pendingFolderId ??
            (persistentSessionId is null
                ? SessionOrganization.Unfiled
                : organization?.FolderOf(project, persistentSessionId) ??
                  SessionOrganization.Unfiled);

        // A running process must always be reachable. The Bin is reserved
        // for history and folders removed in the meantime are not displayed.
        if (folder == SessionOrganization.Bin)
            return SessionOrganization.Unfiled;
        if (folder == SessionOrganization.Unfiled)
            return folder;
        return organization?.Folders(project).Any(f => f.Id == folder) == true
            ? folder : SessionOrganization.Unfiled;
    }
}
