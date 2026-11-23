using NAudio.Wave;

namespace MeetingTranscriber;

/// <summary>Mono converter for loopback sources with 1, 2 or more channels.</summary>
internal sealed class MonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buffer = Array.Empty<float>();

    public MonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        if (_channels < 1 || _channels > 32)
            throw new InvalidOperationException("Unsupported channel count: " + _channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        var needed = checked(count * _channels);
        if (_buffer.Length < needed) _buffer = new float[needed];
        var read = _source.Read(_buffer, 0, needed);
        var frames = read / _channels;
        for (var i = 0; i < frames; i++)
        {
            double sum = 0;
            var baseOffset = i * _channels;
            for (var c = 0; c < _channels; c++)
                sum += _buffer[baseOffset + c];
            buffer[offset + i] = (float)(sum / _channels);
        }
        return frames;
    }
}
