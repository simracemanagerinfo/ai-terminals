using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeTerminal.Host;

public partial class UsageWindow : Window
{
    private readonly string? _currentProject;
    private IReadOnlyList<UsageRow> _rows = [];
    private bool _busy;

    public UsageWindow(string? currentProject)
    {
        InitializeComponent();
        ApplyLanguage();
        _currentProject = currentProject;
        if (currentProject is null || !Directory.Exists(currentProject))
            ScopeFilter.IsEnabled = false;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private void ApplyLanguage()
    {
        Title = UiText.T("usageTitle") + " — " + TerminalBrand.Provider;
        UsageRefreshButton.Content = UiText.T("usageRefresh");
        UsageCloseButton.Content = UiText.T("close");
        UsageTitleLabel.Text = UiText.T("usageTitle");
        UsageSubtitleLabel.Text = UiText.T("usageSubtitle",
            TerminalBrand.Provider == "Claude" ? "Claude Code" : "Codex CLI");
        AgentLabel.Text = UiText.T("agent");
        ScopeLabel.Text = UiText.T("scope");
        ((ComboBoxItem)ScopeFilter.Items[0]).Content = UiText.T("allProjects");
        ((ComboBoxItem)ScopeFilter.Items[1]).Content = UiText.T("currentProject");
        ScanStatus.Text = UiText.T("readLocal");
        UsageInfoLabel.Text = UiText.T("usageInfo");
        string[] keys =
        [
            "agent", "project", "session", "model", "inputHeader",
            "cacheReadHeader", "cacheWriteHeader", "outputHeader",
            "reasoningHeader", "total", "lastActivity"
        ];
        for (var i = 0; i < keys.Length && i < UsageGrid.Columns.Count; ++i)
            UsageGrid.Columns[i].Header = UiText.T(keys[i]);
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        ScanStatus.Text = UiText.T("scanning");
        ErrorLabel.Text = "";
        try
        {
            var data = await Task.Run(() => UsageScanner.Scan());
            _rows = data.Where(row => row.Provider == TerminalBrand.Provider).ToArray();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = UiText.T("readLocalFailed", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ApplyFilters()
    {
        if (!IsLoaded) return;
        var currentProjectOnly = ScopeFilter.SelectedIndex == 1;
        var filtered = _rows.Where(row =>
            (!currentProjectOnly ||
             (_currentProject is not null &&
              string.Equals(row.Project.TrimEnd(Path.DirectorySeparatorChar),
                            _currentProject.TrimEnd(Path.DirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase)))).ToArray();

        UsageGrid.ItemsSource = filtered;
        long total = filtered.Sum(x => x.Total);
        TotalLabel.Text = UiText.T("totalTokens", total.ToString("N0"));
        InputLabel.Text = UiText.T("input", filtered.Sum(x => x.Input).ToString("N0"));
        CacheReadLabel.Text = UiText.T("cacheRead", filtered.Sum(x => x.CacheRead).ToString("N0"));
        CacheWriteLabel.Text = UiText.T("cacheWrite", filtered.Sum(x => x.CacheWrite).ToString("N0"));
        OutputLabel.Text = UiText.T("output", filtered.Sum(x => x.Output).ToString("N0"));
        ReasoningLabel.Text = UiText.T("reasoning", filtered.Sum(x => x.Reasoning).ToString("N0"));
        ScanStatus.Text = UiText.T("usageRows", filtered.Length, _rows.Count);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
