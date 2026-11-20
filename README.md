# AI Terminals

Two independent Windows desktop applications powered by the same **WPF + ConPTY + xterm.js** renderer:

| Application | Branding | Command-line agent |
| --- | --- | --- |
| `ClaudeTerminal.exe` | Warm terracotta | Claude Code |
| `CodexTerminal.exe` | Teal/graphite | OpenAI Codex CLI |

Each executable has its own terminal sessions, local folders, UI cache and icon. Both share the same implementation under `src/shared/`.

[Documentazione in italiano](docs/README.it.md)

## Installation and updates

**Latest stable release: v1.1.1.** The easiest way to install or update is to [download **AI-Terminals-Setup.exe**](https://github.com/simracemanagerinfo/ai-terminals/releases/latest) and double-click it.

The offline Windows setup lets you choose **Claude Terminal**, **Codex Terminal**, or both. It uses the existing per-user application locations, adds Start menu shortcuts, and can create optional desktop shortcuts. **No PowerShell installer script, GitHub login, or administrator privileges required.** Existing virtual folders, saved conversations and UI language settings are kept.

Windows SmartScreen may warn because this new public installer is currently **unsigned**. Do not disable antivirus or SmartScreen. Corporate application control may block executables depending on local policies.

The existing portable ZIP archives remain available in the same release. Advanced users can also continue to update both apps with the PowerShell installer:

```powershell
$ErrorActionPreference = 'Stop'
$installer = Join-Path $env:TEMP 'Install-AITerminals.ps1'
Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/simracemanagerinfo/ai-terminals/main/scripts/Install-AITerminals.ps1' -OutFile $installer
& $installer -App Both
```

Installed programs:

- `%LOCALAPPDATA%\PowerShellCustomization\ClaudeTerminal\ClaudeTerminal.exe`
- `%LOCALAPPDATA%\PowerShellCustomization\CodexTerminal\CodexTerminal.exe`

**Runtime requirements:** Windows x64, WebView2 Runtime, PowerShell 7, and the corresponding Claude Code or Codex CLI installed independently in `PATH`. The standalone installer only installs our two terminal frontends; it does not silently install the CLI providers. The downloadable setup does not need .NET SDK or Node.js.


## Features in v1.1.0

Changes were developed in [PR #3](https://github.com/simracemanagerinfo/ai-terminals/pull/3) and [PR #4](https://github.com/simracemanagerinfo/ai-terminals/pull/4).

### Virtual conversation folders

Each project can have custom organizational folders. **Running chats remain visible in their actual folder** instead of moving to a separate Active list; they keep the same folder when closed and can be moved while running by right-click or drag/drop. Sessions belonging to other projects remain accessible in a small separate active section. Click **+ Folder** to create one. Use a folder's context menu to rename/remove it; removing a virtual folder moves its conversations back to **Unorganized**. Right-click a conversation in history to move it to any folder, or **drag a history conversation onto a folder header**. Right-click a folder and choose **New conversation here** to start in that folder. Claude assigns the folder immediately; Codex assigns it after the first completed response supplies its persistent thread ID.

**Bin** (Italian: **Secchio**) is a built-in last section, initially collapsed. Moving a conversation there **only hides it in this terminal**; the original Claude/Codex JSONL transcripts, indexes and CLI configuration remain untouched. You can **Restore** the chat to its previous folder. This is deliberately *not* physical deletion.

Running processes remain in **Active conversations**. Close an active chat before organizing its history. New Codex sessions do not have a confirmed persistent CLI session ID until their transcript is available, so they can be filed after ending the active session.

Virtual metadata is scoped by normalized project path and separated by provider:

- `%LOCALAPPDATA%\PowerShellCustomization\ai-terminals-claude-folders.json`
- `%LOCALAPPDATA%\PowerShellCustomization\ai-terminals-codex-folders.json`

An unreadable/corrupt organization file is **never silently overwritten**.

### Language support

The UI and integrated agent-launcher diagnostics support **English and Italian**. On first launch the UI uses Windows' Italian locale when present; otherwise English. Change the language any time using the **Language** dropdown. The setting is shared by both apps and persists in `%LOCALAPPDATA%\PowerShellCustomization\ai-terminals-language.json`. Reopen the other app to apply a switch made while it was already open.

The translation catalogue is centralized in `src/shared/UiText.cs` so additional languages can be added without changing the persisted folder IDs.

### Completion notifications

When Claude `Stop` or Codex `agent-turn-complete` fires, the conversation stays highlighted as unread. When the terminal is behind another app, a compact in-app WPF popup appears without stealing focus. Clicking it selects the completed chat. The popup is not a Windows Action Center notification.

Completion hooks are scoped to the launched CLI process and do not edit user-wide agent settings. User-defined hooks for the same event may be superseded in that process; test this integration before releasing it to others.

## Local usage statistics

Each terminal can scan available local session JSONL for token statistics. These are local usage estimates, not an official balance or bill. Existing transcripts are not uploaded by the app.

## Windows build

GitHub Actions builds the shared renderer with Node 22, compiles both portable executables with .NET 10, and runs keyboard, CLI-launcher, transcript-parsing and organization regression tests. Interactive focus/popup behavior still requires manual Windows desktop testing.

For manual development from the repository root:

```powershell
Push-Location '.\src\shared\TerminalUi'
npm install
npm run test:keyboard
npm run build
Pop-Location
dotnet publish '.\src\claude\ClaudeTerminal.csproj' -c Release -r win-x64 --self-contained true -o '.\out\ClaudeTerminal'
dotnet publish '.\src\codex\CodexTerminal.csproj' -c Release -r win-x64 --self-contained true -o '.\out\CodexTerminal'
```

## Privacy and safety

The terminal manager does not delete, move or modify Claude/Codex conversation transcript files when organizing or binning chats. Local UI folder metadata is separate from CLI configuration. No API keys or transcript contents are stored in that organization file.
