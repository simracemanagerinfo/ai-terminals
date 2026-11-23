using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MeetingNotes;

public partial class MainWindow : Window
{
    private string? _transcriptPath;
    private string? _mockJsonPath;
    private string? _htmlPath;
    private string? _textPath;
    private Process? _activeProcess;
    private CancellationTokenSource? _cancelSource;

    private static string OutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AI-Terminals", "MeetingNotes");

    public MainWindow()
    {
        InitializeComponent();
        ProviderInput.SelectedIndex = 0;
        StyleInput.Value = 80;
    }

    private string Provider =>
        (ProviderInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Claude";

    private void BrowseTranscript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleziona una trascrizione",
            Filter = "Trascrizioni di testo (*.txt)|*.txt|Tutti i file (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        _transcriptPath = dialog.FileName;
        TranscriptInput.Text = dialog.FileName;
        if (TitleInput.Text == "Riunione di lavoro")
            TitleInput.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void BrowseMock_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleziona una risposta JSON di prova",
            Filter = "File JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        _mockJsonPath = dialog.FileName;
        MockInput.Text = dialog.FileName;
    }

    private void ProviderInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MockPanel is null || ConsentPanel is null || ConsentInput is null)
            return;
        var isMock = Provider == "Mock";
        MockPanel.Visibility = isMock ? Visibility.Visible : Visibility.Collapsed;
        ConsentPanel.Visibility = isMock ? Visibility.Collapsed : Visibility.Visible;
        ConsentInput.IsChecked = false;
    }

    private void StyleInput_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StyleValue is not null)
            StyleValue.Text = $"{(int)StyleInput.Value}%";
    }

    private void SetBusy(bool busy)
    {
        GenerateButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ProviderInput.IsEnabled = !busy;
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptPath is null || !File.Exists(_transcriptPath))
        {
            MessageBox.Show(this, "Seleziona prima una trascrizione TXT.", "Meeting Notes",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Provider == "Mock" && (_mockJsonPath is null || !File.Exists(_mockJsonPath)))
        {
            MessageBox.Show(this, "Per la prova offline seleziona anche il JSON di risposta.",
                "Meeting Notes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Provider != "Mock" && ConsentInput.IsChecked != true)
        {
            MessageBox.Show(this,
                "Prima di inviare una trascrizione all'IA, verifica che le policy aziendali lo consentano e seleziona la casella di autorizzazione.",
                "Conferma necessaria", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "Invoke-MeetingMinutes.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(this, "Script PowerShell non trovato nella distribuzione: " + script,
                "Meeting Notes", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetBusy(true);
        StatusText.Text = "Generazione in corso…";
        _cancelSource = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pwsh.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    WorkingDirectory = Path.GetTempPath()
                }
            };
            var args = process.StartInfo.ArgumentList;
            foreach (var arg in new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script,
                "-TranscriptPath", _transcriptPath,
                "-OutputDirectory", OutputDirectory,
                "-Provider", Provider,
                "-MeetingTitle", TitleInput.Text,
                "-StyleLevel", ((int)StyleInput.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-AsJson"
            })
                args.Add(arg);

            if (Provider == "Mock")
            {
                args.Add("-MockResponsePath");
                args.Add(_mockJsonPath!);
            }
            else
                args.Add("-AllowExternalProcessing");

            if (!process.Start())
                throw new InvalidOperationException("Impossibile avviare PowerShell 7.");
            _activeProcess = process;

            // Avviare le letture prima di attendere il processo evita blocchi
            // su buffer di stdout/stderr pieni.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(_cancelSource.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                await process.WaitForExitAsync();
                throw new OperationCanceledException("Elaborazione annullata o scaduta (10 minuti).");
            }

            var output = await stdoutTask;
            _ = await stderrTask; // Non esporre testo potenzialmente sensibile nella UI o nei log.
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Il generatore PowerShell è terminato con codice {process.ExitCode}. " +
                    "Controlla configurazione, login del CLI e formato dell'input.");

            using var json = JsonDocument.Parse(output);
            var result = json.RootElement;
            _htmlPath = result.GetProperty("HtmlPath").GetString();
            _textPath = result.GetProperty("TextPath").GetString();
            if (string.IsNullOrWhiteSpace(_htmlPath) || string.IsNullOrWhiteSpace(_textPath) ||
                !File.Exists(_htmlPath) || !File.Exists(_textPath))
                throw new InvalidDataException("File della minuta non trovati.");

            PreviewInput.Text = await File.ReadAllTextAsync(_textPath, Encoding.UTF8);
            CopyOutlookButton.IsEnabled = CopyTextButton.IsEnabled =
                OpenHtmlButton.IsEnabled = OpenFolderButton.IsEnabled = true;
            StatusText.Text = "Minuta generata · Verifica il testo prima di inviarlo";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Elaborazione annullata";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Generazione non riuscita";
            MessageBox.Show(this, ex.Message, "Meeting Notes",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _activeProcess = null;
            _cancelSource.Dispose();
            _cancelSource = null;
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Annullamento in corso…";
        _cancelSource?.Cancel();
    }

    private void OpenHtml_Click(object sender, RoutedEventArgs e)
    {
        if (_htmlPath is not null && File.Exists(_htmlPath))
            Process.Start(new ProcessStartInfo(_htmlPath) { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_htmlPath is null)
            return;
        var folder = Path.GetDirectoryName(_htmlPath);
        if (folder is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (_textPath is null || !File.Exists(_textPath))
            return;
        try
        {
            Clipboard.SetText(File.ReadAllText(_textPath, Encoding.UTF8));
            StatusText.Text = "Testo copiato negli appunti";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Impossibile accedere agli appunti");
        }
    }

    private void CopyOutlook_Click(object sender, RoutedEventArgs e)
    {
        if (_htmlPath is null || _textPath is null || !File.Exists(_htmlPath) || !File.Exists(_textPath))
            return;
        try
        {
            OutlookClipboard.Copy(
                File.ReadAllText(_htmlPath, Encoding.UTF8),
                File.ReadAllText(_textPath, Encoding.UTF8));
            StatusText.Text = "HTML per Outlook copiato: incolla nel corpo di una nuova email";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Impossibile copiare HTML");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancelSource?.Cancel();
        if (_activeProcess is { HasExited: false })
        {
            try { _activeProcess.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        base.OnClosed(e);
    }
}
