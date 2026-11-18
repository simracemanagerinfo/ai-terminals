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
    private readonly HashSet<string> _knownBinSections = new(StringComparer.OrdinalIgnoreCase);
    private SessionOrganization? _organization;
    private string? _organizationLoadError;
    private bool _applyingLanguage;
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
                        await active.Terminal.WriteAsync(dataValue.GetString() ?? "");
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
            Send(new { type = "output", id, data = value.ToString() });
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

    private async Task StartSessionAsync(HistoryEntry? existing = null, bool diagnosticShell = false)
    {
        if (!_webReady || _project is null)
        {
            ShowError(UiText.T("chooseProject"));
            return;
        }
        var provider = TerminalBrand.Provider;
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
        var item = new LiveItem(id, label, claudeId, _project, pty, provider, completionSignalPath);
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
            if (turns.Count == 0) continue;

            session.Unread = true;
            changed = true;
            if (!IsActive || WindowState == System.Windows.WindowState.Minimized)
                ShowCompletionToast(session);
        }
        if (changed) UpdateSidebar();
    }

    private void ShowCompletionToast(LiveItem session)
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
            });
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

    private void UpdateSidebar(string? preferredTerminalId = null)
    {
        var previousKey = (ConversationsList.SelectedItem as SidebarRow)?.Key;
        var selectedKey = preferredTerminalId is null
            ? previousKey : "live:" + preferredTerminalId;
        var search = ConversationFilter.Text?.Trim() ?? "";
        var rows = new List<SidebarRow>();

        void AddHeader(string key, string name, int count, string? folderId = null)
        {
            var collapsed = _collapsedFolders.Contains(key);
            var symbol = folderId == SessionOrganization.Bin ? "🪣" : "▣";
            rows.Add(new SidebarRow(key, (collapsed ? "▸ " : "▾ ") + symbol +
                "  " + name + " (" + count + ")", "", null, null,
                IsHeader: true, FolderId: folderId));
        }

        // Running processes remain accessible, even if their project differs
        // from the selected project. Only completed/history rows are filed.
        var visibleLive = _live.Where(active =>
        {
            var detail = active.Provider + " " + active.Status + " " + active.ProjectPath;
            return search.Length == 0 ||
                active.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                detail.Contains(search, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        if (visibleLive.Length > 0)
        {
            const string activeSection = "section:active";
            AddHeader(activeSection, UiText.T("liveConversations"), visibleLive.Length);
            if (!_collapsedFolders.Contains(activeSection))
                foreach (var active in visibleLive)
                {
                    var title = active.Unread ? "● " + active.Label : active.Label;
                    var detail = active.Provider + " · " + DisplayStatus(active.Status) +
                                 " · " + Path.GetFileName(active.ProjectPath) +
                                 (active.Unread ? " · " + UiText.T("responseReady") : "");
                    rows.Add(new SidebarRow("live:" + active.Id, title, detail, active, null,
                        IsUnread: active.Unread, Indent: new Thickness(15, 0, 0, 0)));
                }
        }

        var resumedIds = _live.Where(x => x.ClaudeSessionId is not null)
            .Select(x => x.Provider + ":" + x.ClaudeSessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var history = _history.Where(h => !resumedIds.Contains(h.Provider + ":" + h.Id)).ToArray();
        if (_project is not null)
        {
            void AddFolder(string folderId, string title)
            {
                var entries = history.Where(h =>
                    (_organization?.FolderOf(_project, h.Id) ?? SessionOrganization.Unfiled)
                        == folderId).ToArray();
                // Always show folder headers (including the Bin at the end).
                var matching = entries.Where(h =>
                    search.Length == 0 ||
                    h.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    h.Id.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
                var section = SectionKey(folderId.Length == 0 ? "__unfiled__" : folderId);
                AddHeader(section, title, entries.Length, folderId);
                if (_collapsedFolders.Contains(section)) return;
                foreach (var historical in matching)
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

            // The Bin is always the very last section.
            var binSection = SectionKey(SessionOrganization.Bin);
            if (_knownBinSections.Add(binSection))
                _collapsedFolders.Add(binSection);
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
                ? UiText.T("folderDuplicate") : UiText.T("folderInvalid"));
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
        EditFolders(() => _organization.RemoveFolder(_project, id));
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

    private bool InBin(HistoryEntry session) =>
        _project is not null && _organization?.FolderOf(_project, session.Id)
            == SessionOrganization.Bin;

    private void ConversationsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = source is null ? null :
            ItemsControl.ContainerFromElement(ConversationsList, source) as ListBoxItem;
        if (container?.DataContext is not SidebarRow row || _organization is null ||
            _project is null)
        { e.Handled = true; return; }

        var menu = new ContextMenu();
        MenuItem Item(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            return item;
        }

        if (row.IsHeader && row.FolderId is { Length: > 0 } folderId &&
            folderId != SessionOrganization.Bin)
        {
            menu.Items.Add(Item(UiText.T("folderRename"), () => RenameFolder(folderId)));
            menu.Items.Add(Item(UiText.T("folderDelete"), () => RemoveFolder(folderId)));
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
    private void CloseSession_Click(object sender, RoutedEventArgs e)
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
        public string? ClaudeSessionId { get; }
        public string? CompletionSignalPath { get; }
        public int NotificationsRead { get; set; }
        public HashSet<string> SeenNotifications { get; } = new(StringComparer.Ordinal);
        public bool Unread { get; set; }
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
