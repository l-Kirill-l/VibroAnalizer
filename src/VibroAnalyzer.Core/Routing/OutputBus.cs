using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Calibration;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Routing;

/// <summary>
/// Логический выход: модуляционная цепь на физическом канале.
/// Выход также является шиной анализа: сгенерированный сигнал пишется
/// в мониторный буфер, к которому подключаются осциллограф, спектр и другие
/// анализаторы.
/// </summary>
public sealed class OutputBus : ISignalBus, IDisposable
{
    private const int HistorySeconds = 20;

    private readonly object _sync = new();
    private readonly List<AnalyzerBase> _analyzers = new();

    private int _sampleRate = 48000;
    private SignalRingBuffer _rawBuffer;
    private SignalRingBuffer _demodBuffer;
    private AnalyzerHost _analyzersHost;
    private long _sequence;
    private bool _disposed;

    public OutputBus(string name, string deviceId, int physicalChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (physicalChannel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalChannel));
        }

        Name = name;
        DeviceId = deviceId;
        PhysicalChannel = physicalChannel;
        Modulation = new ModulationChain();
        Calibration = new CalibrationProfile();
        Demodulation = new DemodulationChain();

        (_rawBuffer, _demodBuffer, _analyzersHost) = Build(_sampleRate);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; }

    public string DeviceId { get; }

    public int PhysicalChannel { get; }

    /// <summary>Признак участия выхода в миксе устройства.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Усиление выхода перед микшированием.</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>Цепь несущей и модуляции этого выхода.</summary>
    public ModulationChain Modulation { get; }

    /// <summary>Краткое описание для интерфейса.</summary>
    public string Description => $"канал {PhysicalChannel + 1}";

    public int SampleRate
    {
        get
        {
            lock (_sync)
            {
                return _sampleRate;
            }
        }
    }

    public int Channels => 1;

    public bool IsVirtual => false;

    public CalibrationProfile Calibration { get; }

    public SignalRingBuffer RawBuffer
    {
        get
        {
            lock (_sync)
            {
                return _rawBuffer;
            }
        }
    }

    public SignalRingBuffer DemodBuffer
    {
        get
        {
            lock (_sync)
            {
                return _demodBuffer;
            }
        }
    }

    /// <summary>Сырой мониторный буфер выхода (совместимость с записью).</summary>
    public SignalRingBuffer MonitorBuffer => RawBuffer;

    public AnalyzerHost Analyzers
    {
        get
        {
            lock (_sync)
            {
                return _analyzersHost;
            }
        }
    }

    public DemodulationChain Demodulation { get; }

    /// <summary>Пересоздаёт буферы под частоту устройства.</summary>
    public void Configure(int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var rate = Math.Max(1, sampleRate);
            if (rate == _sampleRate)
            {
                return;
            }

            _analyzersHost.Dispose();
            (_rawBuffer, _demodBuffer, _analyzersHost) = Build(rate);
            _sampleRate = rate;
            _sequence = 0;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _analyzersHost.Start(_sampleRate);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _analyzersHost.Stop();
        }
    }

    public void AddAnalyzer(AnalyzerBase analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_analyzers.Contains(analyzer))
            {
                return;
            }

            _analyzers.Add(analyzer);
            _analyzersHost.Add(analyzer);
        }
    }

    public void RemoveAnalyzer(AnalyzerBase analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        lock (_sync)
        {
            _analyzers.Remove(analyzer);
            _analyzersHost.Remove(analyzer);
        }
    }

    /// <summary>Формирует моно-блок выхода и пишет его в мониторные буферы.</summary>
    public float[] GenerateMonoBlock(int sampleRate, int frames)
    {
        if (frames <= 0)
        {
            return Array.Empty<float>();
        }

        if (SampleRate != sampleRate)
        {
            Configure(sampleRate);
        }

        var block = Modulation.GenerateBlock(sampleRate, frames, 1);
        var result = new float[frames];
        var gain = !Enabled || !double.IsFinite(Gain) ? 0.0 : Gain;

        for (var i = 0; i < frames; i++)
        {
            result[i] = (float)Math.Clamp(block.Samples[i] * gain, -1.0, 1.0);
        }

        var raw = new SignalBlock(
            result,
            1,
            sampleRate,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow);

        _rawBuffer.Write(raw);

        if (Demodulation.Enabled)
        {
            var values = Demodulation.Process(raw);
            var samples = new float[values.Length];

            for (var i = 0; i < values.Length; i++)
            {
                samples[i] = (float)Math.Clamp(values[i], -1.0, 1.0);
            }

            _demodBuffer.Write(new SignalBlock(
                samples,
                1,
                sampleRate,
                raw.Sequence,
                raw.Timestamp));
        }
        else
        {
            _demodBuffer.Write(raw);
        }

        return result;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _analyzersHost.Dispose();
            _analyzers.Clear();
        }
    }

    private (SignalRingBuffer Raw, SignalRingBuffer Demod, AnalyzerHost Host) Build(int sampleRate)
    {
        var capacity = Math.Max(sampleRate * HistorySeconds, 4096);
        var raw = new SignalRingBuffer(1, capacity);
        var demod = new SignalRingBuffer(1, capacity);
        var host = new AnalyzerHost(raw, demod, Calibration);

        foreach (var analyzer in _analyzers)
        {
            host.Add(analyzer);
        }

        return (raw, demod, host);
    }
}

/// <summary>
/// Собирает моно-сигналы логических выходов в интерливленный блок устройства.
/// </summary>
public static class OutputMixer
{
    public static float[] Mix(
        IReadOnlyList<OutputBus> buses,
        int frames,
        int sampleRate,
        int deviceChannels)
    {
        ArgumentNullException.ThrowIfNull(buses);

        if (frames <= 0 || deviceChannels <= 0)
        {
            return Array.Empty<float>();
        }

        var result = new float[frames * deviceChannels];

        foreach (var bus in buses)
        {
            if (!bus.Enabled || bus.PhysicalChannel >= deviceChannels)
            {
                continue;
            }

            var mono = bus.GenerateMonoBlock(sampleRate, frames);
            var offset = bus.PhysicalChannel;

            for (var frame = 0; frame < frames; frame++)
            {
                result[frame * deviceChannels + offset] += mono[frame];
            }
        }

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Math.Clamp(result[i], -1.0f, 1.0f);
        }

        return result;
    }
}
