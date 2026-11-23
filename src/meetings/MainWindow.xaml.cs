using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
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
    private string? _savedTranscriptText;
    private string _workspaceRoot = WorkspaceStore.LoadPreferredRoot();
    private string? _mockJsonPath;
    private string? _htmlPath;
    private string? _textPath;
    private Process? _activeProcess;
    private CancellationTokenSource? _cancelSource;
    private AudioCaptureSession? _audioCapture;
    private string? _lastAudioFolder;
    private string? _whisperModel;
    private Process? _transcribeProcess;
    private CancellationTokenSource? _transcribeCts;
    private bool _closingTranscribe;
    private DateTime _audioStartedUtc;
    private readonly DispatcherTimer _audioTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _stoppingAudio;
    private bool _closingAudio;
    private bool _recordingPreferencesReady;
    private bool _savedKeepOriginalAudio;
    private bool _keepAudioForCurrentRecording;

    public MainWindow()
    {
        InitializeComponent();
        ProviderInput.SelectedIndex = 0;
        MinutesStyleInput.SelectedIndex = 1;
        StyleInput.Value = 80;
        WorkspaceInput.Text = _workspaceRoot;
        var defaultModel = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AI-Terminals", "MeetingNotes", "Models", "ggml-base.bin");
        if (File.Exists(defaultModel))
        {
            _whisperModel = defaultModel;
            WhisperModelInput.Text = defaultModel;
        }
        _audioTimer.Tick += AudioTimer_Tick;
        _savedKeepOriginalAudio = RecordingPreferenceStore.Load().KeepOriginalAudio;
        KeepOriginalAudioInput.IsChecked = _savedKeepOriginalAudio;
        _recordingPreferencesReady = true;
    }

    private string Provider =>
        (ProviderInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Claude";

    private string MinutesStyle =>
        (MinutesStyleInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Bilanciato";

    private void KeepOriginalAudio_Changed(object sender, RoutedEventArgs e)
    {
        if (!_recordingPreferencesReady)
            return;

        var currentValue = KeepOriginalAudioInput.IsChecked == true;
        if (currentValue == _savedKeepOriginalAudio)
            return;

        try
        {
            RecordingPreferenceStore.Save(new RecordingPreferences(currentValue));
            _savedKeepOriginalAudio = currentValue;
            StatusText.Text = currentValue
                ? "Impostazione salvata: i WAV originali saranno conservati."
                : "Impostazione salvata: elimino i WAV solo dopo una trascrizione automatica riuscita.";
        }
        catch (Exception ex)
        {
            _recordingPreferencesReady = false;
            KeepOriginalAudioInput.IsChecked = _savedKeepOriginalAudio;
            _recordingPreferencesReady = true;
            MessageBox.Show(this, ex.Message, "Impossibile salvare le impostazioni",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_audioCapture is not null || _cancelSource is not null || _transcribeCts is not null)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Scegli dove conservare registrazioni, trascrizioni e minute",
            InitialDirectory = Directory.Exists(_workspaceRoot) ? _workspaceRoot :
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var folder = WorkspaceStore.ValidateRoot(dialog.FolderName);
            WorkspaceStore.SavePreferredRoot(folder);
            _workspaceRoot = folder;
            WorkspaceInput.Text = folder;
            _transcriptPath = null;
            _savedTranscriptText = null;
            TranscriptInput.Text = string.IsNullOrWhiteSpace(TranscriptEditor.Text)
                ? "Nessun TXT selezionato" : "Testo in memoria: usa Salva TXT";
            StatusText.Text = "Cartella di lavoro selezionata. Nessun file preesistente spostato.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cartella di lavoro non valida",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseTranscript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importa una trascrizione",
            Filter = "Trascrizioni di testo (*.txt)|*.txt|Tutti i file (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(WorkspaceStore.Transcripts(_workspaceRoot))
                ? WorkspaceStore.Transcripts(_workspaceRoot) : _workspaceRoot
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length > WorkspaceStore.MaxTranscriptBytes)
                throw new InvalidDataException("Il file supera il limite di 2 MiB.");
            var text = File.ReadAllText(file.FullName, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("La trascrizione selezionata è vuota.");

            TranscriptEditor.Text = text;
            _transcriptPath = file.FullName;
            _savedTranscriptText = null;
            TranscriptInput.Text = file.FullName;
            if (TitleInput.Text == "Riunione di lavoro")
                TitleInput.Text = Path.GetFileNameWithoutExtension(file.Name);
            StatusText.Text = "TXT importato. Puoi modificarlo e salvarne una copia nella cartella scelta.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Impossibile importare la trascrizione",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewTranscript_Click(object sender, RoutedEventArgs e)
    {
        TranscriptEditor.Clear();
        _transcriptPath = null;
        _savedTranscriptText = null;
        TranscriptInput.Text = "Nuovo testo da salvare";
        StatusText.Text = "Scrivi o incolla una trascrizione e premi Salva TXT.";
        TranscriptEditor.Focus();
    }

    private string SaveTranscriptToWorkspace()
    {
        var text = TranscriptEditor.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Scrivi o importa il testo della trascrizione.");

        if (_savedTranscriptText == text && !string.IsNullOrWhiteSpace(_transcriptPath)
            && File.Exists(_transcriptPath))
            return _transcriptPath;

        var saved = WorkspaceStore.CreateTranscript(_workspaceRoot, TitleInput.Text, text);
        _transcriptPath = saved;
        _savedTranscriptText = text;
        TranscriptInput.Text = saved;
        return saved;
    }

    private void SaveTranscript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SaveTranscriptToWorkspace();
            StatusText.Text = "TXT salvato nella cartella di lavoro: " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Impossibile salvare il TXT",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleziona un modello Whisper GGML multilingua",
            Filter = "Modelli GGML (*.bin)|*.bin",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (Path.GetFileName(dialog.FileName).Contains(".en.", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Seleziona un modello multilingua (es. ggml-base.bin), non .en.",
                "Modello non adatto all'italiano", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _whisperModel = dialog.FileName;
        WhisperModelInput.Text = dialog.FileName;
        SttStatusText.Text = "Modello selezionato. L'elaborazione sarà effettuata sul PC.";
    }

    private void BrowseRecording_Click(object sender, RoutedEventArgs e)
    {
        var root = WorkspaceStore.Recordings(_workspaceRoot);
        var dialog = new OpenFileDialog
        {
            Title = "Scegli un WAV registrato da Meeting Notes",
            Filter = "Audio WAV (*.wav)|*.wav",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(root) ? root : _workspaceRoot
        };
        if (dialog.ShowDialog(this) != true) return;
        _lastAudioFolder = Path.GetDirectoryName(dialog.FileName);
        RecordingInput.Text = _lastAudioFolder ?? dialog.FileName;
        SttStatusText.Text = "Registrazione selezionata. Se presenti, puoi elaborare entrambe le tracce.";
    }

    private async void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (_transcribeCts is not null || _cancelSource is not null || _audioCapture is not null)
            return;

        if (string.IsNullOrWhiteSpace(_lastAudioFolder) || !Directory.Exists(_lastAudioFolder))
        {
            MessageBox.Show(this, "Prima registra un audio oppure scegli un WAV.",
                "Registrazione mancante", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = _lastAudioFolder;
        var micPath = Path.Combine(folder, "microfono.wav");
        var pcPath = Path.Combine(folder, "audio-pc.wav");
        string? mic = SttMicrophone.IsChecked == true && File.Exists(micPath) ? micPath : null;
        string? pc = SttSystem.IsChecked == true && File.Exists(pcPath) ? pcPath : null;

        if (mic is null && pc is null)
        {
            var wavs = Directory.GetFiles(folder, "*.wav");
            if (wavs.Length == 1)
            {
                if (SttMicrophone.IsChecked == true) mic = wavs[0];
                else if (SttSystem.IsChecked == true) pc = wavs[0];
            }
        }
        if (mic is null && pc is null)
        {
            MessageBox.Show(this, "Nessuna traccia WAV corrisponde alle sorgenti selezionate.",
                "Audio non trovato", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // File importati o ritrascritti manualmente non vengono cancellati.
        await TranscribeRecordingAsync(folder, mic, pc, automatic: false, keepOriginal: true);
    }

    /// <summary>
    /// Only a successful, fully persisted TXT from a freshly recorded session
    /// permits automatic cleanup of that session's exact WAV paths.
    /// </summary>
    private async Task TranscribeRecordingAsync(
        string recordingFolder, string? microphonePath, string? systemPath,
        bool automatic, bool keepOriginal)
    {
        if (_transcribeCts is not null || _cancelSource is not null || _audioCapture is not null)
            return;

        if (string.IsNullOrWhiteSpace(_whisperModel) || !File.Exists(_whisperModel))
        {
            SttStatusText.Text = automatic
                ? "Modello Whisper mancante: trascrizione automatica non avviata; WAV conservati."
                : "Seleziona un modello Whisper multilingua prima di trascrivere.";
            if (automatic)
                AudioStatusText.Text = "Registrazione salvata. Whisper non configurato: WAV conservati, seleziona il modello per riprovare.";
            if (!automatic)
                MessageBox.Show(this, "Seleziona ggml-base.bin (o un modello multilingua .bin) prima di trascrivere.",
                    "Modello offline mancante", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var exe = Path.Combine(AppContext.BaseDirectory, "transcriber", "MeetingTranscriber.exe");
        if (!File.Exists(exe))
        {
            SttStatusText.Text = "Whisper non disponibile: WAV conservati.";
            if (!automatic)
                MessageBox.Show(this, "Il motore Whisper non è presente nel pacchetto.",
                    "Motore STT mancante", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (microphonePath is null && systemPath is null)
        {
            SttStatusText.Text = "Nessuna sorgente audio valida: WAV conservati.";
            return;
        }

        // Il riconoscimento su CPU di una lunga riunione può richiedere ben
        // più di 30 minuti. Il tasto Annulla resta sempre disponibile.
        _transcribeCts = new CancellationTokenSource(TimeSpan.FromHours(4));
        TranscribeButton.IsEnabled = false;
        CancelTranscribeButton.IsEnabled = true;
        BrowseWorkspaceButton.IsEnabled = false;
        StartAudioButton.IsEnabled = false;
        GenerateButton.IsEnabled = false;
        KeepOriginalAudioInput.IsEnabled = false;
        SttStatusText.Text = automatic
            ? "Registrazione terminata. Trascrizione offline automatica in corso…"
            : "Trascrizione CPU in corso… nessun upload.";
        if (automatic)
            AudioStatusText.Text = "WAV salvati; Whisper sta creando il TXT. Non chiudere l'app.";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
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
            args.Add("--model"); args.Add(_whisperModel);
            args.Add("--output"); args.Add(WorkspaceStore.Transcripts(_workspaceRoot));
            if (microphonePath is not null) { args.Add("--microphone"); args.Add(microphonePath); }
            if (systemPath is not null) { args.Add("--system"); args.Add(systemPath); }

            if (!process.Start())
                throw new InvalidOperationException("Impossibile avviare Whisper.");
            _transcribeProcess = process;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(_transcribeCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                await process.WaitForExitAsync();
                throw;
            }
            var output = await stdout;
            _ = await stderr; // Never expose transcript content in logs/UI.
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    "Whisper non ha terminato correttamente la trascrizione. Controlla modello e WAV.");

            using var json = JsonDocument.Parse(output);
            var result = json.RootElement;
            var path = result.GetProperty("TranscriptPath").GetString();
            var segments = result.GetProperty("Segments").GetInt32();
            if (segments < 1 || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidDataException("Whisper non ha prodotto un TXT valido.");

            var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException("Il TXT è vuoto: i WAV vengono conservati.");
            if (Encoding.UTF8.GetByteCount(content) > WorkspaceStore.MaxTranscriptBytes)
                throw new InvalidDataException("Il TXT supera 2 MiB: WAV conservati.");

            // Present the transcript and its persisted path before deletion.
            TranscriptEditor.Text = content;
            TranscriptInput.Text = path;
            _transcriptPath = path;
            _savedTranscriptText = content;
            SttStatusText.Text = "Trascrizione completata e salvata. Controlla il testo prima di generare la minuta.";
            StatusText.Text = "TXT offline pronto per la revisione; la minuta non parte automaticamente.";

            if (automatic && !keepOriginal)
            {
                try
                {
                    var deleted = RecordingAudioRetention.DeleteAfterSuccessfulTranscript(
                        WorkspaceStore.Recordings(_workspaceRoot), recordingFolder,
                        new[] { microphonePath, systemPath },
                        WorkspaceStore.Transcripts(_workspaceRoot), path, keepOriginalAudio: false);
                    AudioStatusText.Text = $"TXT salvato. Eliminati {deleted} WAV originali come da impostazioni.";
                    OpenAudioFolderButton.IsEnabled =
                        Directory.Exists(recordingFolder) &&
                        Directory.EnumerateFileSystemEntries(recordingFolder).Any();
                    RecordingInput.Text = "Trascrizione salvata · WAV originali eliminati";
                    _lastAudioFolder = null;
                }
                catch (Exception ex)
                {
                    AudioStatusText.Text = "TXT salvato, ma eliminazione WAV non riuscita: " +
                        ex.Message + " Verifica i file audio nella cartella.";
                    OpenAudioFolderButton.IsEnabled = Directory.Exists(recordingFolder);
                }
            }
            else if (automatic)
            {
                AudioStatusText.Text = "TXT salvato. WAV conservati come richiesto nelle impostazioni.";
            }
        }
        catch (OperationCanceledException)
        {
            SttStatusText.Text = "Trascrizione annullata o scaduta. WAV conservati; nessun upload.";
            if (automatic)
                AudioStatusText.Text = "Whisper interrotto: WAV originali conservati per un nuovo tentativo.";
        }
        catch (Exception ex)
        {
            SttStatusText.Text = "Trascrizione non riuscita. WAV originali conservati.";
            if (automatic)
                AudioStatusText.Text = "Trascrizione fallita; i file audio sono stati conservati.";
            MessageBox.Show(this, ex.Message, "Whisper offline",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _transcribeProcess = null;
            _transcribeCts?.Dispose();
            _transcribeCts = null;
            TranscribeButton.IsEnabled = true;
            CancelTranscribeButton.IsEnabled = false;
            KeepOriginalAudioInput.IsEnabled = _audioCapture is null;
            BrowseWorkspaceButton.IsEnabled = _cancelSource is null && _audioCapture is null;
            StartAudioButton.IsEnabled = _audioCapture is null;
            GenerateButton.IsEnabled = _cancelSource is null;
            if (_closingTranscribe)
                Close();
        }
    }

    private void CancelTranscribe_Click(object sender, RoutedEventArgs e)
    {
        SttStatusText.Text = "Annullamento trascrizione in corso…";
        _transcribeCts?.Cancel();
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
        GenerateButton.IsEnabled = !busy && _transcribeCts is null;
        CancelButton.IsEnabled = busy;
        TranscribeButton.IsEnabled = !busy && _transcribeCts is null;
        ProviderInput.IsEnabled = !busy;
        MinutesStyleInput.IsEnabled = !busy;
        BrowseWorkspaceButton.IsEnabled = !busy && _audioCapture is null;
        SaveTranscriptButton.IsEnabled = !busy;
        NewTranscriptButton.IsEnabled = !busy;
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_transcribeCts is not null)
            return;
        if (string.IsNullOrWhiteSpace(TranscriptEditor.Text))
        {
            MessageBox.Show(this, "Importa o scrivi una trascrizione prima di generare la minuta.", "Meeting Notes",
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

        string transcriptToUse;
        try { transcriptToUse = SaveTranscriptToWorkspace(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Impossibile salvare il TXT",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
                "-TranscriptPath", transcriptToUse,
                "-OutputDirectory", WorkspaceStore.Minutes(_workspaceRoot),
                "-Provider", Provider,
                "-MeetingTitle", TitleInput.Text,
                "-MinutesStyle", MinutesStyle,
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

    private void StartAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_audioCapture is not null || _transcribeCts is not null || _cancelSource is not null)
            return;
        if (RecordingPermission.IsChecked != true)
        {
            MessageBox.Show(this,
                "Conferma prima che la registrazione di questa prova sia consentita dalle policy aziendali.",
                "Registrazione audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var mic = CaptureMicrophone.IsChecked == true;
        var system = CaptureSystem.IsChecked == true;
        if (!mic && !system)
        {
            MessageBox.Show(this, "Scegli microfono, audio del PC o entrambi.",
                "Registrazione audio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var root = WorkspaceStore.Recordings(_workspaceRoot);
            _audioCapture = AudioCaptureSession.Start(root, mic, system);
            _keepAudioForCurrentRecording = KeepOriginalAudioInput.IsChecked == true;
            KeepOriginalAudioInput.IsEnabled = false;
            _lastAudioFolder = _audioCapture.Folder;
            _audioStartedUtc = DateTime.UtcNow;
            CaptureMicrophone.IsEnabled = false;
            CaptureSystem.IsEnabled = false;
            BrowseWorkspaceButton.IsEnabled = false;
            StartAudioButton.IsEnabled = false;
            StopAudioButton.IsEnabled = true;
            OpenAudioFolderButton.IsEnabled = false;
            _audioTimer.Start();
            AudioStatusText.Text = "● Registrazione locale in corso · 00:00:00. Nessun upload.";
        }
        catch (Exception ex)
        {
            AudioStatusText.Text = "Avvio audio non riuscito";
            MessageBox.Show(this, ex.Message, "Dispositivo audio non disponibile",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AudioTimer_Tick(object? sender, EventArgs e)
    {
        if (_audioCapture is null || _stoppingAudio)
            return;
        var elapsed = DateTime.UtcNow - _audioStartedUtc;
        if (elapsed >= TimeSpan.FromMinutes(120))
        {
            _ = StopAudioAsync();
            return;
        }
        AudioStatusText.Text = "● Registrazione locale in corso · " +
            elapsed.ToString(@"hh\:mm\:ss") + ". Nessun upload.";
    }

    private async void StopAudio_Click(object sender, RoutedEventArgs e)
    {
        await StopAudioAsync();
    }

    private async Task StopAudioAsync()
    {
        if (_audioCapture is null || _stoppingAudio)
            return;
        _stoppingAudio = true;
        var session = _audioCapture;
        _audioTimer.Stop();
        StopAudioButton.IsEnabled = false;
        AudioStatusText.Text = "Finalizzazione dei file WAV…";
        AudioCaptureResult? completed = null;
        try
        {
            var result = await session.StopAsync();
            completed = result;
            _lastAudioFolder = result.Folder;
            OpenAudioFolderButton.IsEnabled = true;
            RecordingInput.Text = _lastAudioFolder ?? "Registrazione completata";
            AudioStatusText.Text = result.CapturedBytes > 0
                ? "Registrazione salvata. Apri i file WAV per ascoltare entrambe le sorgenti."
                : "Registrazione terminata senza campioni audio. Verifica dispositivi e volume.";
        }
        catch (Exception ex)
        {
            AudioStatusText.Text = "Registrazione interrotta: verificare i file audio locali.";
            MessageBox.Show(this, ex.Message, "Registrazione audio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            OpenAudioFolderButton.IsEnabled = _lastAudioFolder is not null &&
                Directory.Exists(_lastAudioFolder);
        }
        finally
        {
            _audioCapture = null;
            _stoppingAudio = false;
            CaptureMicrophone.IsEnabled = true;
            CaptureSystem.IsEnabled = true;
            StartAudioButton.IsEnabled = _transcribeCts is null;
            BrowseWorkspaceButton.IsEnabled = _cancelSource is null && _transcribeCts is null;
            KeepOriginalAudioInput.IsEnabled = _transcribeCts is null;
            RecordingPermission.IsChecked = false;
        }

        if (completed is not null && completed.CapturedBytes > 0)
        {
            // Transcribe the exact tracks of the completed session, not whichever
            // files happen to be selected in the manual STT panel.
            await TranscribeRecordingAsync(
                completed.Folder, completed.MicrophonePath, completed.SystemPath,
                automatic: true, keepOriginal: _keepAudioForCurrentRecording);
        }
        else if (completed is not null)
        {
            SttStatusText.Text = "Nessun campione audio utile: trascrizione automatica non avviata. WAV conservati.";
        }
    }

    private void OpenAudioFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastAudioFolder is not null && Directory.Exists(_lastAudioFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastAudioFolder}\"")
            {
                UseShellExecute = true
            });
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_transcribeCts is not null)
        {
            e.Cancel = true;
            _closingTranscribe = true;
            _transcribeCts.Cancel();
            return;
        }
        if (_audioCapture is not null)
        {
            e.Cancel = true;
            if (_closingAudio)
                return;
            _closingAudio = true;
            await StopAudioAsync();
            Close();
            return;
        }
        base.OnClosing(e);
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
        _transcribeCts?.Cancel();
        if (_transcribeProcess is { HasExited: false })
        {
            try { _transcribeProcess.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        if (_activeProcess is { HasExited: false })
        {
            try { _activeProcess.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        base.OnClosed(e);
    }
}
