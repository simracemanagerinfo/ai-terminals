using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace MeetingTranscriber;

internal static class Program
{
    private sealed record Segment(TimeSpan Start, string Speaker, string Text);
    private static string _stage = "inizializzazione";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            _stage = "argomenti e configurazione";
            var settings = Parse(args);
            if (settings.ContainsKey("self-test"))
            {
                SelfTest();
                Console.WriteLine("{\"ok\":true}");
                return 0;
            }

            var model = Value(settings, "model");
            var outputDir = Value(settings, "output");
            var audioPaths = new List<(string Path, string Source)>();
            if (settings.TryGetValue("microphone", out var mic) && !string.IsNullOrWhiteSpace(mic))
                audioPaths.Add((mic, "MICROFONO"));
            if (settings.TryGetValue("system", out var sys) && !string.IsNullOrWhiteSpace(sys))
                audioPaths.Add((sys, "AUDIO PC"));
            if (audioPaths.Count == 0)
                throw new ArgumentException("Specifica --microphone o --system.");

            if (!File.Exists(model) || !model.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Modello GGML .bin non trovato.");
            if (Path.GetFileName(model).Contains(".en.", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Serve un modello Whisper multilingua, non .en per il testo italiano.");

            _stage = "verifica dei file WAV";
            foreach (var (path, _) in audioPaths)
            {
                if (!File.Exists(path) || !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("File WAV non trovato.");
                if (new FileInfo(path).Length > 3L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("File audio troppo grande (limite 3 GiB).");
            }
            Directory.CreateDirectory(outputDir);

            // Model is loaded from local disk only. No model downloads or network calls.
            _stage = "caricamento modello Whisper";
            using var factory = WhisperFactory.FromPath(model);
            var segments = new List<Segment>();
            var skippedSources = new List<string>();
            foreach (var (source, speaker) in audioPaths)
            {
                var temp = Path.Combine(Path.GetTempPath(), "meeting-stt-" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    // WASAPI may produce stereo, surround, float, 44/48 kHz etc.
                    // Whisper expects signed PCM mono 16 kHz.
                    _stage = "conversione WAV " + speaker;
                    try
                    {
                        NormalizeToPcm16k(source, temp);
                    }
                    catch (InvalidDataException ex) when (ex.Message == "AUDIO_TOO_SHORT")
                    {
                        // A disconnected or silent WASAPI endpoint can produce
                        // a header-only WAV. Process the other recorded track.
                        skippedSources.Add(speaker);
                        continue;
                    }

                    _stage = "riconoscimento vocale " + speaker;
                    using var processor = factory.CreateBuilder().WithLanguage("it").Build();
                    await using var wav = File.OpenRead(temp);
                    await foreach (var part in processor.ProcessAsync(wav))
                    {
                        var text = part.Text?.Trim();
                        if (string.IsNullOrWhiteSpace(text))
                            continue;
                        segments.Add(new Segment(part.Start, speaker, text));
                    }
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    catch { /* deletion best effort, file stored in local temp */ }
                }
            }
            _stage = "verifica del risultato";
            if (segments.Count == 0)
                throw new InvalidOperationException("Nessuna voce riconosciuta. Verifica audio, durata e modello.");

            // Two tracks were started at almost the same time, but minor offsets
            // are possible. Speaker labels indicate the *device*, not a person.
            var lines = segments.OrderBy(s => s.Start)
                .ThenBy(s => s.Speaker, StringComparer.Ordinal)
                .Select(s => $"[{s.Start.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}] [{s.Speaker}] {s.Text}");
            var textResult = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            var transcriptPath = Path.Combine(outputDir,
                $"trascrizione-audio-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            _stage = "scrittura TXT";
            using (var fs = new FileStream(transcriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(textResult);
            }

            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                TranscriptPath = transcriptPath,
                Segments = segments.Count,
                Sources = audioPaths.Select(s => s.Source).ToArray(),
                SkippedSources = skippedSources.ToArray()
            }));
            return 0;
        }
        catch (Exception ex)
        {
            // Structured stage/type diagnostics, without raw meeting content or paths.
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                Code = "STT_FAILED",
                Stage = _stage,
                ErrorType = ex.GetType().Name
            }));
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--self-test")
            {
                map["self-test"] = "true";
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException("Argomenti CLI non validi.");
            map[args[i][2..]] = args[++i];
        }
        return map;
    }

    private static string Value(Dictionary<string, string> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Parametro --" + key + " mancante.");
        return value;
    }

    private static void NormalizeToPcm16k(string input, string output)
    {
        using var reader = new WaveFileReader(input);
        if (reader.TotalTime.TotalMilliseconds < 200)
            throw new InvalidDataException("AUDIO_TOO_SHORT");
        ISampleProvider mono = new MonoSampleProvider(reader.ToSampleProvider());
        ISampleProvider resampled = new WdlResamplingSampleProvider(mono, 16000);
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        WaveFileWriter.WriteWavFileToStream(target, resampled.ToWaveProvider16());
    }

    private static void SelfTest()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "stt-test-" + Guid.NewGuid().ToString("N") + ".wav");
        var destPath = sourcePath + ".converted.wav";
        try
        {
            using (var writer = new WaveFileWriter(sourcePath, new WaveFormat(48000, 16, 2)))
            {
                var source = new byte[48000 * 2 * 2];
                writer.Write(source, 0, source.Length);
            }
            NormalizeToPcm16k(sourcePath, destPath);
            using var r = new WaveFileReader(destPath);
            if (r.WaveFormat.SampleRate != 16000 || r.WaveFormat.Channels != 1 ||
                r.WaveFormat.BitsPerSample != 16 || r.Length <= 0)
                throw new InvalidDataException("Audio normalization self-test failed.");
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destPath)) File.Delete(destPath);
        }
    }
}
