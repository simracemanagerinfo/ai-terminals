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
using WinForms = System.Windows.Forms;

namespace ClaudeTerminal.Host;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LiveItem> _live = [];
    private readonly ObservableCollection<SidebarRow> _sidebar = [];
    private bool _updatingSidebar;
    private string? _visibleTerminalId;
    private readonly Dictionary<string, LiveItem> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Id, string Text)> _output = new();
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _titleRefreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WinForms.NotifyIcon? _notificationIcon;
    private string? _notificationSessionId;
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
        if (TerminalBrand.Provider == "Codex")
        {
            _completionTimer.Tick += (_, _) => PollCodexCompletions();
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
            catch (Exception ex) { ShowError("Impossibile inizializzare il terminale: " + ex.Message); }
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
        FooterStatus.Text = "Caricamento terminale xterm...";
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
                    FooterStatus.Text = "Renderer pronto. Apri due conversazioni per verificare lo switching.";
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
                            FooterStatus.Text = "Appunti troppo grandi per incollarli nel terminale.";
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
            FooterStatus.Text = "Errore comunicazione terminale: " + ex.Message;
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
            ShowError("Scegli un progetto e attendi che il renderer sia pronto.");
            return;
        }
        var provider = TerminalBrand.Provider;
        if (existing is not null && existing.Provider != provider)
        {
            ShowError("La conversazione appartiene a un altro agente.");
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
                FooterStatus.Text = "Conversazione già attiva: mostrato il terminale esistente.";
                return;
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(existing.Directory, _project))
            {
                ShowError("La conversazione selezionata non appartiene al progetto attivo.");
                return;
            }
        }

        var script = Path.Combine(AppContext.BaseDirectory, "scripts",
            TerminalBrand.Launcher);
        if (!diagnosticShell && !File.Exists(script))
        {
            ShowError("Script Claude mancante nel pacchetto: " + script);
            return;
        }
        // Claude supports --session-id; Codex currently creates its own UUID.
        // Never invent a Codex identity that the CLI has not actually accepted.
        var claudeId = diagnosticShell ? null : existing?.Id ??
            (provider == "Claude" ? Guid.NewGuid().ToString("D") : null);
        var id = Guid.NewGuid().ToString("N");
        var completionSignalPath = !diagnosticShell && provider == "Codex"
            ? Path.Combine(Path.GetTempPath(), "aiterminals-codex-" + id + ".signals")
            : null;
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
        var pty = new ConPtySession("pwsh.exe", command, _project);
        var label = diagnosticShell
            ? ("Shell diagnostica " + DateTime.Now.ToString("HH:mm:ss"))
            : existing?.Title ?? ("Nuova chat " + DateTime.Now.ToString("HH:mm:ss"));
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
                FooterStatus.Text = "Connessione ConPTY interrotta; processo da verificare: " + message;
        });
        pty.Exited += code => Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !_sessions.ContainsKey(id)) return;
            item.Status = "Terminata";
            UpdateSidebar();
            Send(new { type = "exited", id, code });
            if (_visibleTerminalId == id)
                FooterStatus.Text = code == -1
                    ? "Il processo si è chiuso; codice di uscita non disponibile."
                    : "Processo terminato (codice " + code + ").";
        });

        try
        {
            await pty.StartAsync(_columns, _rows);
            // A fast-exiting command can signal Exited before StartAsync returns.
            if (item.Status == "Avvio")
                item.Status = "In esecuzione";
            UpdateSidebar();
            FooterStatus.Text = "Terminale " + TerminalBrand.Provider + " integrato avviato.";
        }
        catch (Exception ex)
        {
            item.Status = "Errore";
            UpdateSidebar();
            Send(new { type = "output", id, data = "\r\n[HOST] " + ex.Message + "\r\n" });
            pty.Dispose();
            FooterStatus.Text = "Errore avvio " + provider + ": " + ex.Message;
        }
    }

    private async Task SetProjectAsync(string path)
    {
        if (!Directory.Exists(path)) { ShowError("La directory non esiste: " + path); return; }
        _project = Path.GetFullPath(path);
        ProjectCombo.Text = _project;
        SessionHistory.SaveProject(_project);
        NewSessionButton.IsEnabled = _webReady;
        SideStatus.Text = "Progetto: " + _project + "\n" +
                          "Le sessioni già aperte restano attive cambiando progetto.";
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
        if (!silent) FooterStatus.Text = "Indicizzazione conversazioni locali...";
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
            if (!silent) FooterStatus.Text = items.Length + " conversazioni nello storico del progetto.";
        }
        catch (Exception ex)
        {
            if (!silent) ShowError("Errore storico: " + ex.Message);
        }
    }

    private void PollCodexCompletions()
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
                ShowCompletionNotification(session);
        }
        if (changed) UpdateSidebar();
    }

    private void ShowCompletionNotification(LiveItem session)
    {
        try
        {
            if (_notificationIcon is null)
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
                    ?? System.Drawing.SystemIcons.Information;
                _notificationIcon = new WinForms.NotifyIcon
                {
                    Icon = icon,
                    Text = "Codex Terminal",
                    Visible = true
                };
                _notificationIcon.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (_notificationSessionId is null ||
                        !_sessions.TryGetValue(_notificationSessionId, out var target)) return;
                    if (WindowState == System.Windows.WindowState.Minimized)
                        WindowState = System.Windows.WindowState.Normal;
                    Activate();
                    UpdateSidebar(preferredTerminalId: target.Id);
                    MarkRead(target);
                });
            }

            _notificationSessionId = session.Id;
            _notificationIcon.BalloonTipTitle = "Codex · Risposta completata";
            _notificationIcon.BalloonTipText = session.Label.Length <= 200
                ? session.Label : session.Label[..197] + "...";
            _notificationIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _notificationIcon.ShowBalloonTip(5000);
        }
        catch (Exception)
        {
            // Windows notifications are best-effort (Focus Assist may suppress
            // them). The persistent unread highlight is still available.
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

    private void UpdateSidebar(string? preferredTerminalId = null)
    {
        var previousKey = (ConversationsList.SelectedItem as SidebarRow)?.Key;
        var selectedKey = preferredTerminalId is null
            ? previousKey
            : "live:" + preferredTerminalId;
        var search = ConversationFilter.Text?.Trim() ?? "";
        var rows = new List<SidebarRow>();

        // Ogni processo ConPTY compare una volta sola, anche cambiando progetto.
        foreach (var active in _live)
        {
            var title = active.Unread ? "● " + active.Label : active.Label;
            var detail = active.Provider + " · " + active.Status + " · " + Path.GetFileName(active.ProjectPath) +
                (active.Unread ? " · Risposta pronta" : "");
            if (search.Length > 0 &&
                !title.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !detail.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(new SidebarRow("live:" + active.Id, title, detail, active, null, active.Unread));
        }

        // Lo storico è limitato al progetto scelto. Evita doppioni delle chat riprese.
        var resumedIds = _live.Where(x => x.ClaudeSessionId is not null)
                              .Select(x => x.Provider + ":" + x.ClaudeSessionId)
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var historical in _history)
        {
            if (resumedIds.Contains(historical.Provider + ":" + historical.Id)) continue;
            var title = historical.Title;
            var detail = historical.Provider + " · Storico · " + historical.Modified;
            if (search.Length > 0 &&
                !title.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !historical.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(new SidebarRow("history:" + historical.Provider + ":" + historical.Id, title, detail, null, historical));
        }

        _updatingSidebar = true;
        try
        {
            _sidebar.Clear();
            foreach (var row in rows) _sidebar.Add(row);
            ConversationsList.SelectedItem = _sidebar.FirstOrDefault(x => x.Key == selectedKey);
        }
        finally { _updatingSidebar = false; }

        LiveCount.Text = "  " + _live.Count(x => x.Status is "Avvio" or "In esecuzione" or "Errore I/O") + " attive";
        if (preferredTerminalId is not null &&
            _live.FirstOrDefault(x => x.Id == preferredTerminalId) is { } selected)
            ShowActiveTerminal(selected);
    }

    private void ShowActiveTerminal(LiveItem item)
    {
        _visibleTerminalId = item.Id;
        ActiveTitle.Text = item.Provider + " · " + item.Label + " — " + Path.GetFileName(item.ProjectPath);
        Send(new { type = "select", id = item.Id });
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
            Description = "Seleziona il progetto Claude",
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
            await StartSessionAsync(selected);
        else if (ConversationsList.SelectedItem is SidebarRow { Live: { } active })
        {
            if (active.Status == "Terminata")
            {
                var old = _history.FirstOrDefault(x => x.Provider == active.Provider &&
                    x.Id.Equals(active.ClaudeSessionId, StringComparison.OrdinalIgnoreCase));
                if (old is null)
                    ShowError("Nessun transcript disponibile: la chat potrebbe non avere ancora messaggi.");
                else
                    await StartSessionAsync(old);
            }
            else ShowActiveTerminal(active);
        }
    }
    private async void ConversationsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ConversationsList.SelectedItem is SidebarRow { History: { } selected })
            await StartSessionAsync(selected);
    }
    private void ConversationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSidebar) return;
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
                "Terminare questa conversazione e il suo processo " + item.Provider + "? Il lavoro in corso verrà interrotto.",
                "Chiudi sessione", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
            ActiveTitle.Text = "Nessuna conversazione";
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closing && _live.Any(x => x.Status is "In esecuzione" or "Avvio" or "Errore I/O"))
        {
            var response = System.Windows.MessageBox.Show(this,
                "Chiudere il terminale terminerà tutte le sessioni " + TerminalBrand.Provider + " in esecuzione. Vuoi continuare?",
                "Sessioni ancora attive", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (response != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        _closing = true;
        _flushTimer.Stop();
        _titleRefreshTimer.Stop();
        _completionTimer.Stop();
        _notificationIcon?.Dispose();
        foreach (var item in _live)
        {
            item.Terminal.Dispose();
            DeleteCompletionSignal(item);
        }
        _sessions.Clear();
    }

    public sealed record SidebarRow(
        string Key, string Title, string Detail, LiveItem? Live, HistoryEntry? History, bool IsUnread = false);

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
