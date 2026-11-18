namespace VibroAnalyzer.Core.Signals;

/// <summary>
/// Описание формата сигнала: частота дискретизации и число каналов.
/// </summary>
public readonly record struct SignalFormat(int SampleRate, int Channels)
{
    public bool IsValid => SampleRate > 0 && Channels > 0;
}

/// <summary>
/// Неизменяемый блок отсчётов. Samples хранит чередующиеся каналы (interleaved).
/// </summary>
public sealed class SignalBlock
{
    public float[] Samples { get; }
    public int FrameCount { get; }
    public int Channels { get; }
    public int SampleRate { get; }
    public long Sequence { get; }
    public DateTimeOffset Timestamp { get; }

    public SignalBlock(
        float[] samples,
        int channels,
        int sampleRate,
        long sequence = 0,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Число каналов должно быть больше нуля.");
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        if (samples.Length % channels != 0)
        {
            throw new ArgumentException("Длина буфера должна делиться на число каналов.", nameof(samples));
        }

        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
        FrameCount = samples.Length / channels;
        Sequence = sequence;
        Timestamp = timestamp ?? DateTimeOffset.UtcNow;
    }

    public double DurationSeconds => (double)FrameCount / SampleRate;

    public SignalFormat Format => new(SampleRate, Channels);

    public double GetSample(int frame, int channel)
    {
        if ((uint)frame >= (uint)FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if ((uint)channel >= (uint)Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        return Samples[frame * Channels + channel];
    }

    public void CopyChannelTo(int channel, Span<double> destination)
    {
        if ((uint)channel >= (uint)Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        if (destination.Length < FrameCount)
        {
            throw new ArgumentException("Приёмный буфер короче блока.", nameof(destination));
        }

        for (var i = 0; i < FrameCount; i++)
        {
            destination[i] = Samples[i * Channels + channel];
        }
    }

    public double[] ToChannelDouble(int channel)
    {
        var result = new double[FrameCount];
        CopyChannelTo(channel, result);
        return result;
    }
}
