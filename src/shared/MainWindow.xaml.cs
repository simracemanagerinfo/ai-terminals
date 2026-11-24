using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace ClaudeTerminal.Host;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LiveItem> _live = [];
    private readonly ObservableCollection<SidebarRow> _sidebar = [];
    private bool _updatingSidebar;
    private string? _visibleTerminalId;
    private readonly Dictionary<string, LiveItem> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initializedFolderSections = new(StringComparer.OrdinalIgnoreCase);
    private SessionOrganization? _organization;
    private string? _organizationLoadError;
    private bool _applyingLanguage;
    private System.Windows.Point _dragStart;
    private SidebarRow? _draggingRow;
    private readonly ConcurrentQueue<(string Id, string Text)> _output = new();
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _titleRefreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CompletionToastWindow? _completionToast;
    private bool _pollingHistory;
    private IReadOnlyList<HistoryEntry> _history = [];
    private string? _project;
    private bool _webReady;
    private bool _closing;
    private int _historyGeneration;
    private int _columns = 110;
    private int _rows = 32;

    public MainWindow(string requestedProject)
    {
        InitializeComponent();
        ConversationsList.ItemsSource = _sidebar;
        // Pre-attach an empty menu: WPF may not open a context menu on the
        // first right-click when ContextMenu is initially null.
        ConversationsList.ContextMenu = new ContextMenu();
        NewSessionButton.IsEnabled = false;
        NewFolderButton.IsEnabled = false;
        try { _organization = SessionOrganization.ForAgent(TerminalBrand.Provider); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { _organizationLoadError = ex.Message; }
        ApplyLanguage();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowConversationSearch();
                e.Handled = true;
            }
        };

        Title = TerminalBrand.Title;
        var icon = Path.Combine(AppContext.BaseDirectory, "assets", TerminalBrand.IconFile);
        if (File.Exists(icon))
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(icon));

        var recent = SessionHistory.RecentProjects();
        ProjectCombo.ItemsSource = recent;
        if (!string.IsNullOrWhiteSpace(requestedProject))
            ProjectCombo.Text = requestedProject;
        else
        {
            var defaults = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")
            };
            ProjectCombo.Text = recent.FirstOrDefault(p => !defaults.Contains(p, StringComparer.OrdinalIgnoreCase))
                ?? recent.FirstOrDefault() ?? "";
        }

        _flushTimer.Tick += (_, _) => FlushOutput();
        _flushTimer.Start();
        _titleRefreshTimer.Tick += async (_, _) =>
        {
            if (_closing || _pollingHistory || _project is null) return;
            if (!_live.Any(x => x.ProjectPath.Equals(_project, StringComparison.OrdinalIgnoreCase) &&
                                x.Status is "Avvio" or "In esecuzione")) return;
            _pollingHistory = true;
            try { await RefreshHistoryAsync(silent: true); }
            finally { _pollingHistory = false; }
        };
        _titleRefreshTimer.Start();
        // Both agents use native completion hooks with a private signal file.
        {
            _completionTimer.Tick += (_, _) => PollCompletions();
            _completionTimer.Start();
            // Returning to the currently visible chat counts as reading it.
            Activated += (_, _) =>
            {
                if (_visibleTerminalId is not null &&
                    _sessions.TryGetValue(_visibleTerminalId, out var selected))
                    MarkRead(selected);
            };
            ConversationsList.PreviewMouseLeftButtonUp += (_, _) =>
            {
                if (ConversationsList.SelectedItem is SidebarRow { Live: { } selected })
                    MarkRead(selected);
            };
        }
        Loaded += async (_, _) =>
        {
            try
            {
                await InitializeWebAsync();
                if (Directory.Exists(ProjectCombo.Text))
                    await SetProjectAsync(ProjectCombo.Text);
            }
            catch (Exception ex) { ShowError(UiText.T("initFailed", ex.Message)); }
            if (_organizationLoadError is not null)
                ShowError(UiText.T("invalidOrganization", _organizationLoadError));
        };
        Closing += MainWindow_Closing;
    }

    private async Task InitializeWebAsync()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
            throw new FileNotFoundException("Manca wwwroot/index.html. Scarica il pacchetto compilato da GitHub Actions.");
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerShellCustomization", TerminalBrand.DataFolder, "WebView2");
        Directory.CreateDirectory(dataRoot);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataRoot);
        await TerminalView.EnsureCoreWebView2Async(environment);
        var web = TerminalView.CoreWebView2;
        web.Settings.AreDevToolsEnabled = false;
        // Ctrl+J is also Edge's Downloads shortcut. In Codex Terminal it
        // must reach xterm.js rather than being swallowed by WebView2.
        // Microsoft's setting preserves Ctrl+C/Ctrl+V editing shortcuts.
        if (TerminalBrand.Provider == "Codex")
            web.Settings.AreBrowserAcceleratorKeysEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        web.Settings.IsWebMessageEnabled = true;
        web.SetVirtualHostNameToFolderMapping("claude-terminal.local", webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        web.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith("https://claude-terminal.local/", StringComparison.OrdinalIgnoreCase))
                args.Cancel = true;
        };
        web.NewWindowRequested += (_, args) => { args.Handled = true; };
        web.WebMessageReceived += OnWebMessage;
        TerminalView.Source = new Uri("https://claude-terminal.local/index.html");
        FooterStatus.Text = UiText.T("rendererLoading");
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closing || !e.Source.StartsWith("https://claude-terminal.local/", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var msg = doc.RootElement;
            if (!msg.TryGetProperty("type", out var typeValue)) return;
            var type = typeValue.GetString();
            var id = msg.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString() : null;
            switch (type)
            {
                case "ready":
                    _webReady = true;
                    NewSessionButton.IsEnabled = _project is not null;
                    FooterStatus.Text = UiText.T("rendererReady");
                    break;
                case "input":
                    if (id is not null && _sessions.TryGetValue(id, out var active) &&
                        active.Status is "Avvio" or "In esecuzione" &&
                        msg.TryGetProperty("data", out var dataValue) && dataValue.ValueKind == JsonValueKind.String)
                    {
                        if (IsActive && _visibleTerminalId == id)
                            MarkRead(active);
                        var input = dataValue.GetString() ?? "";
                        if (active.WaitingForInput && AttentionSignals.IsResponseInput(input))
                        {
                            active.WaitingForInput = false;
                            UpdateSidebar();
                        }
                        await active.Terminal.WriteAsync(input);
                    }
                    break;
                case "copy":
                    if (id is not null && _sessions.ContainsKey(id) &&
                        msg.TryGetProperty("data", out var copyValue) &&
                        copyValue.ValueKind == JsonValueKind.String)
                    {
                        var selectedText = copyValue.GetString();
                        if (!string.IsNullOrEmpty(selectedText) && selectedText.Length <= 2_000_000)
                            System.Windows.Clipboard.SetText(selectedText);
                    }
                    break;
                case "paste":
                    if (id is not null && _sessions.TryGetValue(id, out var pasteTarget) &&
                        pasteTarget.Status is "Avvio" or "In esecuzione" &&
                        System.Windows.Clipboard.ContainsText())
                    {
                        var clipboardText = System.Windows.Clipboard.GetText();
                        if (clipboardText.Length > 0 && clipboardText.Length <= 2_000_000)
                            Send(new { type = "clipboard-paste", id, data = clipboardText });
                        else if (clipboardText.Length > 2_000_000)
                            FooterStatus.Text = UiText.T("clipboardTooLarge");
                    }
                    break;
                case "resize":
                    if (id is not null && _sessions.TryGetValue(id, out var resized) &&
                        msg.TryGetProperty("cols", out var cols) &&
                        msg.TryGetProperty("rows", out var rows))
                    {
                        _columns = Math.Clamp(cols.GetInt32(), 10, 400);
                        _rows = Math.Clamp(rows.GetInt32(), 5, 150);
                        resized.Terminal.Resize(_columns, _rows);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            FooterStatus.Text = UiText.T("terminalProtocolError", ex.Message);
        }
    }

    private void Send(object data)
    {
        if (_webReady && TerminalView.CoreWebView2 is not null)
            TerminalView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data));
    }

    private void FlushOutput()
    {
        if (!_webReady) return;
        var buffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var bytes = 0;
        var chunks = 0;
        while (chunks < 256 && bytes < 131072 && _output.TryDequeue(out var item))
        {
            chunks++;
            bytes += item.Text.Length;
            if (!buffers.TryGetValue(item.Id, out var buf))
                buffers[item.Id] = buf = new StringBuilder();
            buf.Append(item.Text);
        }
        foreach (var (id, value) in buffers)
        {
            var data = value.ToString();
            if (_sessions.TryGetValue(id, out var active) &&
                active.Provider == "Codex" && active.CompletionSignalPath is not null &&
                active.Status is "Avvio" or "In esecuzione" &&
                active.Attention.Read(data) > 0)
                RequestAttention(active);
            Send(new { type = "output", id, data });
        }
    }

    private static string QuoteWindowsArgument(string input)
    {
        if (input.Length > 0 && !input.Any(char.IsWhiteSpace) && !input.Contains('"')) return input;
        var value = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in input)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"')
            {
                value.Append('\\', slashes * 2 + 1);
                value.Append('"');
                slashes = 0;
                continue;
            }
            value.Append('\\', slashes);
            slashes = 0;
            value.Append(ch);
        }
        value.Append('\\', slashes * 2);
        return value.Append('"').ToString();
    }

    private async Task StartSessionAsync(HistoryEntry? existing = null, bool diagnosticShell = false,
        string? virtualFolderId = null)
    {
        if (!_webReady || _project is null)
        {
            ShowError(UiText.T("chooseProject"));
            return;
        }
        var provider = TerminalBrand.Provider;
        // A new conversation can be pre-filed. A resumed conversation retains
        // its existing folder, and never inherits this optional destination.
        if (virtualFolderId is not null &&
            (existing is not null || diagnosticShell || _organization is null ||
             (virtualFolderId != SessionOrganization.Unfiled &&
              !_organization.Folders(_project).Any(folder => folder.Id == virtualFolderId))))
        {
            ShowError(UiText.T("folderNotFound"));
            return;
        }
        if (existing is not null && existing.Provider != provider)
        {
            ShowError(UiText.T("wrongProvider"));
            return;
        }
        if (existing is not null)
        {
            var liveMatch = _live.FirstOrDefault(x =>
                x.Provider == provider && x.ClaudeSessionId == existing.Id &&
                x.Status is "Avvio" or "In esecuzione" or "Errore I/O");
            if (liveMatch is not null)
            {
                UpdateSidebar(preferredTerminalId: liveMatch.Id);
                FooterStatus.Text = UiText.T("alreadyActive");
                return;
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(existing.Directory, _project))
            {
                ShowError(UiText.T("wrongProject"));
                return;
            }
        }

        var script = Path.Combine(AppContext.BaseDirectory, "scripts",
            TerminalBrand.Launcher);
        if (!diagnosticShell && !File.Exists(script))
        {
            ShowError(UiText.T("cliMissing", TerminalBrand.Provider, script));
            return;
        }
        // Claude supports --session-id; Codex currently creates its own UUID.
        // Never invent a Codex identity that the CLI has not actually accepted.
        var claudeId = diagnosticShell ? null : existing?.Id ??
            (provider == "Claude" ? Guid.NewGuid().ToString("D") : null);
        var id = Guid.NewGuid().ToString("N");
        var completionSignalPath = diagnosticShell ? null :
            Path.Combine(Path.GetTempPath(), "aiterminals-" + provider.ToLowerInvariant() + "-" + id + ".signals");
        var command = diagnosticShell
            ? "-NoLogo -NoProfile"
            : "-NoLogo -NoProfile -File " + QuoteWindowsArgument(script) +
                " -ProjectPath " + QuoteWindowsArgument(_project);
        if (!diagnosticShell && existing is not null)
            command += " -SessionId " + QuoteWindowsArgument(existing.Id);
        else if (!diagnosticShell && provider == "Claude")
            command += " -NewSessionId " + QuoteWindowsArgument(claudeId!);
        if (completionSignalPath is not null)
            command += " -CompletionSignalPath " + QuoteWindowsArgument(completionSignalPath);
        if (!diagnosticShell)
            command += " -UiLanguage " + QuoteWindowsArgument(UiText.Language);
        var pty = new ConPtySession("pwsh.exe", command, _project);
        var label = diagnosticShell
            ? UiText.T("diagnosticShell", DateTime.Now.ToString("HH:mm:ss"))
            : existing?.Title ?? UiText.T("newChatLabel", DateTime.Now.ToString("HH:mm:ss"));
        var item = new LiveItem(id, label, claudeId, _project, pty, provider, completionSignalPath)
        {
            PendingFolderId = virtualFolderId
        };
        _sessions.Add(id, item);
        _live.Add(item);
        Send(new { type = "create", id, label,
            mode = diagnosticShell ? "shell" : provider.ToLowerInvariant() });
        UpdateSidebar(preferredTerminalId: id);

        pty.OutputReceived += text => _output.Enqueue((id, text));
        pty.TransportFaulted += message => Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !_sessions.ContainsKey(id) || item.Status == "Terminata") return;
            // Un errore della pipe NON prova che il processo sia terminato.
            item.Status = "Errore I/O";
            UpdateSidebar();
            if (_visibleTerminalId == id)
                FooterStatus.Text = UiText.T("connectionFault", message);
        });
        pty.Exited += code => Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !_sessions.ContainsKey(id)) return;
            item.Status = "Terminata";
            item.WaitingForInput = false;
            UpdateSidebar();
            Send(new { type = "exited", id, code });
            if (_visibleTerminalId == id)
                FooterStatus.Text = code == -1
                    ? UiText.T("processNoCode")
                    : UiText.T("processExit", code);
        });

        try
        {
            await pty.StartAsync(_columns, _rows);
            // A fast-exiting command can signal Exited before StartAsync returns.
            if (item.Status == "Avvio")
                item.Status = "In esecuzione";
            UpdateSidebar();
            FooterStatus.Text = UiText.T("terminalStarted", TerminalBrand.Provider);
            // Claude knows its persistent identity at creation. Codex generates
            // its own identity; its completion event supplies the thread-id later.
            if (item.PendingFolderId is { } folder && claudeId is not null &&
                _organization is not null)
            {
                try
                {
                    _organization.Move(item.ProjectPath, claudeId, folder);
                    item.PendingFolderId = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    FooterStatus.Text = UiText.T("organizationError", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            item.Status = "Errore";
            UpdateSidebar();
            Send(new { type = "output", id, data = "\r\n[HOST] " + ex.Message + "\r\n" });
            pty.Dispose();
            FooterStatus.Text = UiText.T("terminalStartError", provider, ex.Message);
        }
    }

    private async Task SetProjectAsync(string path)
    {
        if (!Directory.Exists(path)) { ShowError(UiText.T("projectMissing", path)); return; }
        _project = Path.GetFullPath(path);
        ProjectCombo.Text = _project;
        SessionHistory.SaveProject(_project);
        NewSessionButton.IsEnabled = _webReady;
        NewFolderButton.IsEnabled = _organization is not null;
        SideStatus.Text = UiText.T("sideProject", _project);
        // Non mostrare lo storico del progetto precedente durante la nuova indicizzazione.
        _history = [];
        UpdateSidebar();
        await RefreshHistoryAsync();
    }

    private async Task RefreshHistoryAsync(bool silent = false)
    {
        if (_project is null) return;
        var project = _project;
        var version = ++_historyGeneration;
        if (!silent) FooterStatus.Text = UiText.T("indexing");
        try
        {
            var items = await Task.Run(() =>
                SessionHistory.ForProject(project)
                    .Where(x => TerminalBrand.Provider == "Claude")
                    .Concat(TerminalBrand.Provider == "Codex" ? CodexHistory.ForProject(project) : [])
                    .OrderByDescending(x => x.Timestamp).ToArray());
            if (version != _historyGeneration || _project != project) return;
            _history = items;
            foreach (var active in _live)
            {
                if (!active.ProjectPath.Equals(project, StringComparison.OrdinalIgnoreCase) ||
                    active.ClaudeSessionId is null) continue;

                var transcript = items.FirstOrDefault(x => x.Provider == active.Provider &&
                    x.Id.Equals(active.ClaudeSessionId, StringComparison.OrdinalIgnoreCase));
                if (transcript is null || active.Label == transcript.Title) continue;
                active.Label = transcript.Title;
                if (_visibleTerminalId == active.Id)
                    ActiveTitle.Text = active.Provider + " · " + active.Label +
                        " — " + Path.GetFileName(active.ProjectPath);
            }
            UpdateSidebar();
            if (!silent) FooterStatus.Text = UiText.T("historyCount", items.Length);
        }
        catch (Exception ex)
        {
            if (!silent) ShowError(UiText.T("historyReadError", ex.Message));
        }
    }

    private void PollCompletions()
    {
        if (_closing) return;
        var changed = false;
        foreach (var session in _live.ToArray())
        {
            var read = session.NotificationsRead;
            var turns = CodexCompletionSignals.ReadNewTurnIds(
                session.CompletionSignalPath, ref read, session.SeenNotifications);
            session.NotificationsRead = read;
            foreach (var marker in turns)
            {
                if (AttentionSignals.IsAttentionMarker(marker))
                {
                    if (session.Status is "Avvio" or "In esecuzione")
                        RequestAttention(session);
                    continue;
                }

                if (session.Provider == "Codex" &&
                    CodexCompletionSignals.TryGetThreadId(marker, out var threadId))
                {
                    session.ClaudeSessionId ??= threadId;
                    if (session.PendingFolderId is { } folder && _organization is not null)
                    {
                        try
                        {
                            _organization.Move(session.ProjectPath, threadId, folder);
                            session.PendingFolderId = null;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            FooterStatus.Text = UiText.T("organizationError", ex.Message);
                        }
                    }
                }

                session.WaitingForInput = false;
                session.Unread = true;
                changed = true;
                if (!IsActive || WindowState == System.Windows.WindowState.Minimized)
                    ShowCompletionToast(session);
            }
        }
        if (changed) UpdateSidebar();
    }

    private void RequestAttention(LiveItem session)
    {
        // Multiple native notifications for the same unanswered question
        // must not create repeated popups. Reading a chat is not answering it.
        if (session.WaitingForInput) return;
        session.WaitingForInput = true;
        UpdateSidebar();
        if (!IsActive || WindowState == System.Windows.WindowState.Minimized ||
            _visibleTerminalId != session.Id)
            ShowCompletionToast(session, waitingForInput: true);
    }

    private void ShowCompletionToast(LiveItem session, bool waitingForInput = false)
    {
        try
        {
            // A single unobtrusive toast per window. Multiple completed chats
            // remain independently highlighted in the sidebar until read.
            _completionToast?.Close();
            var toast = new CompletionToastWindow(this, session.Provider, session.Label, () =>
            {
                if (_closing || !_sessions.TryGetValue(session.Id, out var target)) return;
                if (WindowState == System.Windows.WindowState.Minimized)
                    WindowState = System.Windows.WindowState.Normal;
                UpdateSidebar(preferredTerminalId: target.Id);
                Activate();
                MarkRead(target);
            }, waitingForInput);
            _completionToast = toast;
            toast.Closed += (_, _) =>
            {
                if (ReferenceEquals(_completionToast, toast))
                    _completionToast = null;
            };
            toast.ShowForCompletion();
        }
        catch (Exception)
        {
            // The unread marker remains visible if desktop policy or an
            // exclusive full-screen application prevents showing a toast.
        }
    }

    private void MarkRead(LiveItem session)
    {
        if (!session.Unread) return;
        session.Unread = false;
        UpdateSidebar();
    }

    private static void DeleteCompletionSignal(LiveItem session)
    {
        try
        {
            if (session.CompletionSignalPath is { } file && File.Exists(file))
                File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string SectionKey(string folderId)
        => "section:" + _project + ":" + folderId;

    private string EffectiveFolder(LiveItem item) =>
        LiveFolderRouting.EffectiveFolder(
            _organization, item.ProjectPath, item.ClaudeSessionId, item.PendingFolderId);

    private void UpdateSidebar(string? preferredTerminalId = null)
    {
        var previousKey = (ConversationsList.SelectedItem as SidebarRow)?.Key;
        var selectedKey = preferredTerminalId is null
            ? previousKey : "live:" + preferredTerminalId;
        var search = ConversationFilter.Text?.Trim() ?? "";
        var rows = new List<SidebarRow>();

        bool MatchesLive(LiveItem active)
        {
            var detail = active.Provider + " " + active.Status + " " + active.ProjectPath;
            return search.Length == 0 ||
                active.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                detail.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        void AddHeader(string key, string name, int count, string? folderId = null, int waiting = 0)
        {
            var collapsed = _collapsedFolders.Contains(key);
            var symbol = folderId == SessionOrganization.Bin ? "🪣" : "▣";
            rows.Add(new SidebarRow(key, (collapsed ? "▸ " : "▾ ") + symbol +
                "  " + name + " (" + count + ")" +
                (waiting > 0 ? " · ! " + waiting : ""), "", null, null,
                IsUnread: waiting > 0, IsHeader: true, FolderId: folderId));
        }

        void AddLiveRow(LiveItem active)
        {
            var title = active.WaitingForInput ? "! " + active.Label :
                active.Unread ? "● " + active.Label : active.Label;
            var detail = active.Provider + " · " + DisplayStatus(active.Status) +
                         " · " + Path.GetFileName(active.ProjectPath) +
                         (active.WaitingForInput ? " · " + UiText.T("inputRequired") :
                          active.Unread ? " · " + UiText.T("responseReady") : "");
            rows.Add(new SidebarRow("live:" + active.Id, title, detail, active, null,
                IsUnread: active.Unread || active.WaitingForInput, Indent: new Thickness(15, 0, 0, 0)));
        }

        // When switching projects, keep processes from other projects reachable
        // in a small separate section. Active chats from the selected project
        // always appear in their real virtual folders instead.
        var otherProjectLive = _live.Where(active =>
            (_project is null ||
             !active.ProjectPath.Equals(_project, StringComparison.OrdinalIgnoreCase)) &&
            MatchesLive(active)).ToArray();
        if (otherProjectLive.Length > 0)
        {
            const string activeSection = "section:active";
            AddHeader(activeSection, UiText.T("otherProjectActive"), otherProjectLive.Length,
                waiting: otherProjectLive.Count(x => x.WaitingForInput));
            if (!_collapsedFolders.Contains(activeSection))
                foreach (var active in otherProjectLive)
                    AddLiveRow(active);
        }

        var resumedIds = _live.Where(x => x.ClaudeSessionId is not null)
            .Select(x => x.Provider + ":" + x.ClaudeSessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var history = _history.Where(h => !resumedIds.Contains(h.Provider + ":" + h.Id))
            .ToArray();

        if (_project is not null)
        {
            // On first presentation, all folders begin collapsed. Once a user
            // expands one, preserve that UI state while the app stays open.
            // An active conversation selected explicitly opens its folder.
            void InitializeSection(string section)
            {
                if (_initializedFolderSections.Add(section))
                    _collapsedFolders.Add(section);
            }
            if (_organization is not null)
                foreach (var folder in _organization.Folders(_project))
                    InitializeSection(SectionKey(folder.Id));
            InitializeSection(SectionKey("__unfiled__"));
            InitializeSection(SectionKey(SessionOrganization.Bin));

            if (preferredTerminalId is not null &&
                _live.FirstOrDefault(x => x.Id == preferredTerminalId) is { } target &&
                target.ProjectPath.Equals(_project, StringComparison.OrdinalIgnoreCase))
            {
                var folderId = EffectiveFolder(target);
                var key = folderId.Length == 0 ? "__unfiled__" : folderId;
                _collapsedFolders.Remove(SectionKey(key));
            }

            var currentProjectLive = _live.Where(active =>
                active.ProjectPath.Equals(_project, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            void AddFolder(string folderId, string title)
            {
                var assignedLive = currentProjectLive.Where(active =>
                    EffectiveFolder(active) == folderId).ToArray();
                var assignedHistory = history.Where(h =>
                    (_organization?.FolderOf(_project, h.Id) ??
                     SessionOrganization.Unfiled) == folderId).ToArray();

                var key = folderId.Length == 0 ? "__unfiled__" : folderId;
                var section = SectionKey(key);
                AddHeader(section, title, assignedLive.Length + assignedHistory.Length, folderId,
                    assignedLive.Count(x => x.WaitingForInput));
                if (_collapsedFolders.Contains(section)) return;

                foreach (var active in assignedLive.Where(MatchesLive))
                    AddLiveRow(active);
                foreach (var historical in assignedHistory.Where(h =>
                    search.Length == 0 ||
                    h.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    h.Id.Contains(search, StringComparison.OrdinalIgnoreCase)))
                    rows.Add(new SidebarRow(
                        "history:" + historical.Provider + ":" + historical.Id,
                        historical.Title,
                        historical.Provider + " · " + UiText.T("statusHistory") +
                            " · " + historical.Modified,
                        null, historical,
                        Indent: new Thickness(15, 0, 0, 0)));
            }

            if (_organization is not null)
                foreach (var folder in _organization.Folders(_project))
                    AddFolder(folder.Id, folder.Name);
            AddFolder(SessionOrganization.Unfiled, UiText.T("unorganized"));

            // The Bin always stays last, below all active and stored chats.
            AddFolder(SessionOrganization.Bin, UiText.T("bin"));
        }

        _updatingSidebar = true;
        try
        {
            _sidebar.Clear();
            foreach (var row in rows) _sidebar.Add(row);
            ConversationsList.SelectedItem = _sidebar.FirstOrDefault(x => x.Key == selectedKey);
        }
        finally { _updatingSidebar = false; }

        LiveCount.Text = UiText.T("activeCount",
            _live.Count(x => x.Status is "Avvio" or "In esecuzione" or "Errore I/O"));
        if (preferredTerminalId is not null &&
            _live.FirstOrDefault(x => x.Id == preferredTerminalId) is { } selected)
            ShowActiveTerminal(selected);
    }

    private static string DisplayStatus(string status) => status switch
    {
        "Avvio" => UiText.T("statusStarting"),
        "In esecuzione" => UiText.T("statusRunning"),
        "Terminata" => UiText.T("statusExited"),
        "Errore I/O" => UiText.T("statusIo"),
        "Errore" => UiText.T("statusError"),
        _ => status
    };

    private void ShowActiveTerminal(LiveItem item)
    {
        _visibleTerminalId = item.Id;
        ActiveTitle.Text = item.Provider + " · " + item.Label + " — " + Path.GetFileName(item.ProjectPath);
        Send(new { type = "select", id = item.Id });
    }

    private void ApplyLanguage()
    {
        _applyingLanguage = true;
        try
        {
            LanguagePicker.SelectedIndex = UiText.IsItalian ? 1 : 0;
            SubtitleLabel.Text = UiText.T("subtitle");
            ProjectLabel.Text = UiText.T("project");
            BrowseButton.Content = UiText.T("browse");
            OpenProjectButton.Content = UiText.T("openProject");
            SearchButton.Content = UiText.T("search");
            SearchButton.ToolTip = UiText.T("searchHint");
            RefreshButton.ToolTip = UiText.T("refreshHint");
            ConversationHeader.Text = UiText.T("conversations");
            NewSessionButton.Content = UiText.T("newConversation");
            NewFolderButton.Content = UiText.T("newFolder");
            NewFolderButton.ToolTip = UiText.T("newFolderHint");
            LanguageLabel.Text = UiText.T("language") + ":";
            ConversationFilter.ToolTip = UiText.T("filterHint");
            TokenButton.Content = UiText.T("tokens");
            TokenButton.ToolTip = UiText.T("tokensHint", TerminalBrand.Provider);
            ResumeButton.Content = UiText.T("resume");
            CloseSessionButton.Content = UiText.T("closeSession");
            if (_visibleTerminalId is null)
                ActiveTitle.Text = UiText.T("noConversation");
            if (_project is null)
                SideStatus.Text = UiText.T("sideChoose");
            else
                SideStatus.Text = UiText.T("sideProject", _project);
            FooterStatus.Text = _webReady ? UiText.T("rendererReady") : UiText.T("loadingRenderer");
            UpdateSidebar();
        }
        finally { _applyingLanguage = false; }
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingLanguage || !IsLoaded ||
            LanguagePicker.SelectedItem is not ComboBoxItem item) return;
        var language = item.Tag?.ToString();
        if (language is not ("en" or "it") || language == UiText.Language) return;
        try
        {
            UiText.SetLanguage(language);
            ApplyLanguage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private static string FolderLabel(SessionOrganization.FolderInfo folder)
        => folder.Name;

    private string? PromptFolderName(string heading, string initial = "")
    {
        var dialog = new Window
        {
            Owner = this, Title = heading, Width = 420, Height = 185,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = FontFamily,
            Background = Background,
            Foreground = Foreground
        };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = UiText.T("folderName") });
        var entry = new TextBox
        {
            Text = initial, Margin = new Thickness(0, 9, 0, 13),
            Padding = new Thickness(8)
        };
        content.Children.Add(entry);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var save = new Button { Content = UiText.T("save"), MinWidth = 85, IsDefault = true };
        var cancel = new Button { Content = UiText.T("cancel"), MinWidth = 85, IsCancel = true };
        save.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => { entry.Focus(); entry.SelectAll(); };
        return dialog.ShowDialog() == true ? entry.Text : null;
    }

    private void EditFolders(Action action)
    {
        try { action(); UpdateSidebar(); }
        catch (ArgumentException ex)
        {
            ShowError(ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                ? UiText.T("folderDuplicate") :
                ex.Message.Contains("reserved", StringComparison.OrdinalIgnoreCase)
                    ? UiText.T("folderReserved") : UiText.T("folderInvalid"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(UiText.T("organizationError", ex.Message));
        }
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _organization is null) return;
        var name = PromptFolderName(UiText.T("folderCreate"));
        if (name is null) return;
        EditFolders(() => _organization.Create(_project, name));
    }

    private void RenameFolder(string id)
    {
        if (_project is null || _organization is null) return;
        var folder = _organization.Folders(_project).FirstOrDefault(f => f.Id == id);
        if (folder is null) return;
        var name = PromptFolderName(UiText.T("folderRename"), folder.Name);
        if (name is null) return;
        EditFolders(() => _organization.Rename(_project, id, name));
    }

    private void RemoveFolder(string id)
    {
        if (_project is null || _organization is null) return;
        var folder = _organization.Folders(_project).FirstOrDefault(f => f.Id == id);
        if (folder is null) return;
        if (MessageBox.Show(this, UiText.T("folderDeletePrompt", folder.Name),
                UiText.T("folderDelete"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        EditFolders(() =>
        {
            _organization.RemoveFolder(_project, id);
            // Codex can still be waiting for its first completion/thread ID.
            // A pending placement must not point to a deleted virtual folder.
            foreach (var active in _live.Where(x =>
                x.ProjectPath.Equals(_project, StringComparison.OrdinalIgnoreCase) &&
                x.PendingFolderId == id))
                active.PendingFolderId = SessionOrganization.Unfiled;
        });
    }

    private void MoveHistory(HistoryEntry session, string target)
    {
        if (_project is null || _organization is null) return;
        if (target == SessionOrganization.Bin &&
            MessageBox.Show(this, UiText.T("binConfirm", session.Title),
                UiText.T("bin"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        EditFolders(() =>
        {
            if (target == "__restore__") _organization.Restore(_project, session.Id);
            else _organization.Move(_project, session.Id, target);
        });
    }

    private void MoveLive(LiveItem session, string target)
    {
        if (_project is null || _organization is null ||
            !session.ProjectPath.Equals(_project, StringComparison.OrdinalIgnoreCase) ||
            session.CompletionSignalPath is null) return;
        // Running chats must not disappear into the collapsed Bin. Close the
        // process first, then use the history menu to archive it.
        if (target == SessionOrganization.Bin)
        {
            FooterStatus.Text = UiText.T("activeMoveDenied");
            return;
        }
        if (target != SessionOrganization.Unfiled &&
            !_organization.Folders(_project).Any(x => x.Id == target))
        {
            ShowError(UiText.T("folderNotFound"));
            return;
        }
        if (session.ClaudeSessionId is { } stableId)
        {
            EditFolders(() =>
            {
                _organization.Move(session.ProjectPath, stableId, target);
                session.PendingFolderId = null;
            });
        }
        else
        {
            // Codex first needs to report its own stable thread ID. Keep the
            // selected folder in memory until the session-scoped hook fires.
            session.PendingFolderId = target;
        }
        _collapsedFolders.Remove(SectionKey(target.Length == 0 ? "__unfiled__" : target));
        UpdateSidebar(preferredTerminalId: session.Id);
    }

    private bool InBin(HistoryEntry session) =>
        _project is not null && _organization?.FolderOf(_project, session.Id)
            == SessionOrganization.Bin;

    // Only conversations from the selected project can be moved. The drag
    // payload is local to this ListBox; no external file paths are accepted.
    private void ConversationsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingRow = null;
        var source = e.OriginalSource as DependencyObject;
        var container = source is null ? null :
            ItemsControl.ContainerFromElement(ConversationsList, source) as ListBoxItem;
        if (container?.DataContext is not SidebarRow row) return;
        if (row.History is { } history &&
            history.Directory.Equals(_project, StringComparison.OrdinalIgnoreCase))
            _draggingRow = row;
        else if (row.Live is { CompletionSignalPath: not null } live &&
                 live.ProjectPath.Equals(_project, StringComparison.OrdinalIgnoreCase))
            _draggingRow = row;

        if (_draggingRow is not null)
            _dragStart = e.GetPosition(ConversationsList);
    }

    private void ConversationsList_PreviewMouseMove(
        object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingRow is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggingRow = null;
            return;
        }
        var position = e.GetPosition(ConversationsList);
        if (Math.Abs(position.X - _dragStart.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) <= SystemParameters.MinimumVerticalDragDistance)
            return;

        try
        {
            System.Windows.DragDrop.DoDragDrop(
                ConversationsList,
                new System.Windows.DataObject("AITerminalsLocalSession", _draggingRow.Key),
                System.Windows.DragDropEffects.Move);
        }
        finally { _draggingRow = null; }
    }

    private string? DropDestination(object originalSource)
    {
        var source = originalSource as DependencyObject;
        var target = source is null ? null :
            ItemsControl.ContainerFromElement(ConversationsList, source) as ListBoxItem;
        return target?.DataContext is SidebarRow { IsHeader: true, FolderId: { } folderId }
            ? folderId : null;
    }

    private void ConversationsList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        var destination = DropDestination(e.OriginalSource);
        var row = _draggingRow;
        var valid = row is not null && destination is not null &&
            e.Data.GetDataPresent("AITerminalsLocalSession") &&
            (row.History is not null ||
             (row.Live is not null && destination != SessionOrganization.Bin));
        e.Effects = valid ? System.Windows.DragDropEffects.Move :
            System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void ConversationsList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var row = _draggingRow;
        var destination = DropDestination(e.OriginalSource);
        var valid = row is not null && destination is not null &&
            e.Data.GetDataPresent("AITerminalsLocalSession") &&
            (row.History is not null ||
             (row.Live is not null && destination != SessionOrganization.Bin));
        if (valid && row!.History is { } historical)
            MoveHistory(historical, destination!);
        else if (valid && row!.Live is { } active)
            MoveLive(active, destination!);
        e.Effects = valid ? System.Windows.DragDropEffects.Move :
            System.Windows.DragDropEffects.None;
        e.Handled = true;
        _draggingRow = null;
    }

    private void ConversationsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = source is null ? null :
            ItemsControl.ContainerFromElement(ConversationsList, source) as ListBoxItem;
        if (container?.DataContext is not SidebarRow row || _organization is null ||
            _project is null)
        { e.Handled = true; return; }

        var menu = ConversationsList.ContextMenu ?? new ContextMenu();
        menu.Items.Clear();
        MenuItem Item(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            return item;
        }

        if (row.IsHeader && row.FolderId is { } folderId &&
            folderId != SessionOrganization.Bin)
        {
            var targetFolderId = folderId;
            var newChat = new MenuItem { Header = UiText.T("newConversationInFolder") };
            newChat.Click += async (_, _) => await StartSessionAsync(virtualFolderId: targetFolderId);
            menu.Items.Add(newChat);
            if (folderId.Length > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item(UiText.T("folderRename"), () => RenameFolder(folderId)));
                menu.Items.Add(Item(UiText.T("folderDelete"), () => RemoveFolder(folderId)));
            }
        }
        else if (row.History is { } conversation)
        {
            if (InBin(conversation))
                menu.Items.Add(Item(UiText.T("restore"),
                    () => MoveHistory(conversation, "__restore__")));
            else
            {
                var submenu = new MenuItem { Header = UiText.T("folderSelect") };
                submenu.Items.Add(Item(UiText.T("unorganized"),
                    () => MoveHistory(conversation, SessionOrganization.Unfiled)));
                foreach (var folder in _organization.Folders(_project))
                {
                    var destination = folder.Id;
                    submenu.Items.Add(Item(folder.Name,
                        () => MoveHistory(conversation, destination)));
                }
                menu.Items.Add(submenu);
                menu.Items.Add(new Separator());
                menu.Items.Add(Item(UiText.T("moveToBin"),
                    () => MoveHistory(conversation, SessionOrganization.Bin)));
            }
        }

        else if (row.Live is { CompletionSignalPath: not null } active &&
                 active.ProjectPath.Equals(_project, StringComparison.OrdinalIgnoreCase))
        {
            var submenu = new MenuItem { Header = UiText.T("folderSelect") };
            submenu.Items.Add(Item(UiText.T("unorganized"),
                () => MoveLive(active, SessionOrganization.Unfiled)));
            foreach (var folder in _organization.Folders(_project))
            {
                var targetFolder = folder.Id;
                submenu.Items.Add(Item(folder.Name,
                    () => MoveLive(active, targetFolder)));
            }
            menu.Items.Add(submenu);
        }

        if (menu.Items.Count == 0) { e.Handled = true; return; }
        ConversationsList.ContextMenu = menu;
    }

    private void ShowError(string message)
    {
        FooterStatus.Text = message;
        System.Windows.MessageBox.Show(this, message, TerminalBrand.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
        => await SetProjectAsync(ProjectCombo.Text);
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = UiText.T("openProject"),
            ShowNewFolderButton = false
        };
        if (_project is not null) dlg.SelectedPath = _project;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            await SetProjectAsync(dlg.SelectedPath);
    }
    private async void NewSession_Click(object sender, RoutedEventArgs e) => await StartSessionAsync();
    private void Usage_Click(object sender, RoutedEventArgs e)
    {
        var popup = new UsageWindow(_project) { Owner = this };
        popup.ShowDialog();
    }
    private void ShowConversationSearch()
    {
        ConversationFilter.Visibility = Visibility.Visible;
        ConversationFilter.Focus();
        ConversationFilter.SelectAll();
    }
    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationFilter.Visibility != Visibility.Visible)
            ShowConversationSearch();
        else
        {
            ConversationFilter.Clear();
            ConversationFilter.Visibility = Visibility.Collapsed;
        }
    }
    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();
    private void ConversationFilter_TextChanged(object sender, TextChangedEventArgs e) => UpdateSidebar();
    private async void ResumeHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationsList.SelectedItem is SidebarRow { History: { } selected })
        {
            if (InBin(selected))
                FooterStatus.Text = UiText.T("restoreFirst");
            else
                await StartSessionAsync(selected);
        }
        else if (ConversationsList.SelectedItem is SidebarRow { Live: { } active })
        {
            if (active.Status == "Terminata")
            {
                var old = _history.FirstOrDefault(x => x.Provider == active.Provider &&
                    x.Id.Equals(active.ClaudeSessionId, StringComparison.OrdinalIgnoreCase));
                if (old is null)
                    ShowError(UiText.T("noTranscript"));
                else
                    await StartSessionAsync(old);
            }
            else ShowActiveTerminal(active);
        }
    }
    private async void ConversationsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ConversationsList.SelectedItem is SidebarRow { History: { } selected })
        {
            if (InBin(selected))
                FooterStatus.Text = UiText.T("restoreFirst");
            else
                await StartSessionAsync(selected);
        }
    }
    private void ConversationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSidebar) return;
        if (ConversationsList.SelectedItem is SidebarRow { IsHeader: true } section)
        {
            if (!_collapsedFolders.Add(section.Key))
                _collapsedFolders.Remove(section.Key);
            UpdateSidebar();
            return;
        }
        if (ConversationsList.SelectedItem is SidebarRow { Live: { } active })
        {
            ShowActiveTerminal(active);
            MarkRead(active);
        }
    }
    private async void CloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationsList.SelectedItem is not SidebarRow { Live: { } item }) return;
        if (item.Status != "Terminata")
        {
            var response = System.Windows.MessageBox.Show(this,
                UiText.T("closeSessionPrompt", item.Provider),
                UiText.T("closeSessionTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (response != MessageBoxResult.Yes) return;
        }
        _sessions.Remove(item.Id);
        _live.Remove(item);
        item.Terminal.Dispose();
        DeleteCompletionSignal(item);
        Send(new { type = "remove", id = item.Id });
        UpdateSidebar(preferredTerminalId: _live.LastOrDefault()?.Id);
        if (_live.Count == 0)
        {
            _visibleTerminalId = null;
            ActiveTitle.Text = UiText.T("noConversation");
        }
        // Refresh immediately so a closed running chat appears in its same
        // persisted folder, without a 15-second detour to Unorganized.
        await RefreshHistoryAsync(silent: true);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closing && _live.Any(x => x.Status is "In esecuzione" or "Avvio" or "Errore I/O"))
        {
            var response = System.Windows.MessageBox.Show(this,
                UiText.T("closeAppPrompt", TerminalBrand.Provider),
                UiText.T("sessionsActive"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (response != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        _closing = true;
        _flushTimer.Stop();
        _titleRefreshTimer.Stop();
        _completionTimer.Stop();
        _completionToast?.Close();
        foreach (var item in _live)
        {
            item.Terminal.Dispose();
            DeleteCompletionSignal(item);
        }
        _sessions.Clear();
    }

    public sealed record SidebarRow(
        string Key, string Title, string Detail, LiveItem? Live, HistoryEntry? History,
        bool IsUnread = false, bool IsHeader = false, string? FolderId = null,
        Thickness Indent = default);

    public sealed class LiveItem : INotifyPropertyChanged
    {
        private string _status = "Avvio";
        public string Id { get; }
        private string _label = "";
        public string Label
        {
            get => _label;
            set
            {
                if (_label == value) return;
                _label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }
        public string? ClaudeSessionId { get; set; }
        public string? PendingFolderId { get; set; }
        public string? CompletionSignalPath { get; }
        public int NotificationsRead { get; set; }
        public HashSet<string> SeenNotifications { get; } = new(StringComparer.Ordinal);
        public bool Unread { get; set; }
        public bool WaitingForInput { get; set; }
        public AttentionSignals Attention { get; } = new();
        public string ProjectPath { get; }
        public string Provider { get; }
        public ConPtySession Terminal { get; }
        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public LiveItem(string id, string label, string? claudeId, string projectPath,
            ConPtySession terminal, string provider, string? completionSignalPath)
            => (Id, Label, ClaudeSessionId, ProjectPath, Terminal, Provider, CompletionSignalPath) =
                (id, label, claudeId, projectPath, terminal, provider, completionSignalPath);
    }
}
