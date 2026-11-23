using System.IO;
using NAudio.Wave;

namespace MeetingNotes;

/// <summary>
/// Test locale di registrazione. I due dispositivi vengono acquisiti in WAV
/// separati: nessuna trascrizione, nessun invio a servizi IA.
/// </summary>
public sealed class AudioCaptureSession
{
    private readonly List<AudioTrack> _tracks = new();
    private bool _finished;
    public string Folder { get; }
    public string? MicrophonePath { get; }
    public string? SystemPath { get; }

    private AudioCaptureSession(string folder, bool microphone, bool system)
    {
        Folder = folder;
        if (microphone)
            MicrophonePath = Path.Combine(folder, "microfono.wav");
        if (system)
            SystemPath = Path.Combine(folder, "audio-pc.wav");
    }

    public static AudioCaptureSession Start(string root, bool microphone, bool system)
    {
        if (!microphone && !system)
            throw new ArgumentException("Seleziona almeno una sorgente audio.");
        var folder = Path.Combine(root,
            "rec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var session = new AudioCaptureSession(folder, microphone, system);
        try
        {
            // Se una sorgente fallisce, interrompiamo l'intera registrazione,
            // invece di produrre senza avviso una registrazione incompleta.
            if (microphone)
                session._tracks.Add(new AudioTrack(session.MicrophonePath!, isLoopback: false));
            if (system)
                session._tracks.Add(new AudioTrack(session.SystemPath!, isLoopback: true));
            foreach (var track in session._tracks)
                track.Start();
            return session;
        }
        catch
        {
            foreach (var track in session._tracks)
                track.DisposeSafely();
            throw;
        }
    }

    public async Task<AudioCaptureResult> StopAsync()
    {
        if (_finished)
            throw new InvalidOperationException("Registrazione già terminata.");
        _finished = true;
        foreach (var track in _tracks)
            track.RequestStop();

        var errors = new List<Exception>();
        foreach (var track in _tracks)
        {
            try { await track.FinishAsync(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0)
            throw new AggregateException("Errore durante la chiusura dei file audio.", errors);

        return new AudioCaptureResult(
            Folder,
            MicrophonePath,
            SystemPath,
            _tracks.Sum(t => t.BytesCaptured));
    }

    private sealed class AudioTrack
    {
        private readonly WasapiRecorder _recorder;
        private readonly object _writerLock = new();
        private readonly TaskCompletionSource<bool> _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WaveFileWriter? _writer;
        private Exception? _writeError;
        private long _bytes;
        private bool _started;
        private bool _stopRequested;

        public long BytesCaptured => Interlocked.Read(ref _bytes);

        public AudioTrack(string path, bool isLoopback)
        {
            _recorder = isLoopback
                ? new WasapiRecorderBuilder().WithLoopbackCapture().Build()
                : new WasapiRecorderBuilder().Build();
            try
            {
                _writer = new WaveFileWriter(path, _recorder.WaveFormat);
                _recorder.DataAvailable += (data, flags, devicePosition, qpcPosition) =>
                {
                    // I dati WASAPI sono validi soltanto durante questo callback.
                    lock (_writerLock)
                    {
                        if (_writer is null || _writeError is not null)
                            return;
                        try
                        {
                            _writer.Write(data);
                            Interlocked.Add(ref _bytes, data.Length);
                        }
                        catch (Exception ex)
                        {
                            // Non propagare eccezioni dal thread di cattura audio.
                            _writeError = ex;
                        }
                    }
                };
                _recorder.RecordingStopped += (_, _) => _stopped.TrySetResult(true);
            }
            catch
            {
                _writer?.Dispose();
                _recorder.Dispose();
                throw;
            }
        }

        public void Start()
        {
            _recorder.StartRecording();
            _started = true;
        }

        public void RequestStop()
        {
            if (_stopRequested)
                return;
            _stopRequested = true;
            if (!_started)
            {
                _stopped.TrySetResult(true);
                return;
            }
            try { _recorder.StopRecording(); }
            catch (Exception ex) { _stopped.TrySetException(ex); }
        }

        public async Task FinishAsync()
        {
            RequestStop();
            try
            {
                await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                try { await _recorder.DisposeAsync(); }
                finally
                {
                    lock (_writerLock)
                    {
                        _writer?.Dispose();
                        _writer = null;
                    }
                }
            }
            if (_writeError is not null)
                throw new IOException("Scrittura del file WAV non riuscita.", _writeError);
        }

        public void DisposeSafely()
        {
            RequestStop();
            try { _recorder.Dispose(); }
            finally
            {
                lock (_writerLock)
                {
                    _writer?.Dispose();
                    _writer = null;
                }
            }
        }
    }
}

public sealed record AudioCaptureResult(
    string Folder,
    string? MicrophonePath,
    string? SystemPath,
    long CapturedBytes);
