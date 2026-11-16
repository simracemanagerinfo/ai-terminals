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
        _currentProject = currentProject;
        if (currentProject is null || !Directory.Exists(currentProject))
            ScopeFilter.IsEnabled = false;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        ScanStatus.Text = "Analisi in corso...";
        ErrorLabel.Text = "";
        try
        {
            var data = await Task.Run(() => UsageScanner.Scan());
            _rows = data.Where(row => row.Provider == TerminalBrand.Provider).ToArray();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Impossibile leggere i dati locali: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void ApplyFilters()
    {
        if (!IsLoaded) return;
        var provider = (ProviderFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tutti";
        var scope = (ScopeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tutti i progetti";
        var filtered = _rows.Where(row =>
            (provider == "Tutti" || row.Provider == provider) &&
            (scope != "Progetto corrente" ||
             (_currentProject is not null &&
              string.Equals(row.Project.TrimEnd(Path.DirectorySeparatorChar),
                            _currentProject.TrimEnd(Path.DirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase)))).ToArray();

        UsageGrid.ItemsSource = filtered;
        long total = filtered.Sum(x => x.Total);
        TotalLabel.Text = "Token totali: " + total.ToString("N0");
        InputLabel.Text = "Input: " + filtered.Sum(x => x.Input).ToString("N0");
        CacheReadLabel.Text = "Cache letta: " + filtered.Sum(x => x.CacheRead).ToString("N0");
        CacheWriteLabel.Text = "Cache scritta: " + filtered.Sum(x => x.CacheWrite).ToString("N0");
        OutputLabel.Text = "Output: " + filtered.Sum(x => x.Output).ToString("N0");
        ReasoningLabel.Text = "Ragionamento: " + filtered.Sum(x => x.Reasoning).ToString("N0");
        ScanStatus.Text = filtered.Length + " sessioni / " + _rows.Count + " con dati disponibili";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
