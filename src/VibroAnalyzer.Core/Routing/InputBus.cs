using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Calibration;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Routing;

/// <summary>
/// Логический вход: один физический канал аудиоустройства.
/// На один физический канал можно создать несколько логических входов,
/// например для разных анализаторов или разной обработки.
/// </summary>
public sealed class InputBus : ISignalBus, IDisposable
{
    private const int HistorySeconds = 5;

    private readonly object _sync = new();
    private readonly List<AnalyzerBase> _analyzers = new();

    private int _sampleRate;
    private SignalRingBuffer _rawBuffer;
    private SignalRingBuffer _demodBuffer;
    private AnalyzerHost _analyzersHost;
    private bool _disposed;

    public InputBus(
        string name,
        string deviceId,
        int physicalChannel,
        int sampleRate,
        CalibrationProfile? calibration = null)
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
        Calibration = calibration ?? new CalibrationProfile();
        Demodulation = new DemodulationChain();

        var rate = Math.Max(1, sampleRate);
        (_rawBuffer, _demodBuffer, _analyzersHost) = Build(rate);
        _sampleRate = rate;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; }

    public string DeviceId { get; }

    public int PhysicalChannel { get; }

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

    /// <summary>Краткое описание для интерфейса.</summary>
    public string Description => $"канал {PhysicalChannel + 1}";

    /// <summary>
    /// Пересоздаёт буферы и хост анализаторов под новую частоту.
    /// Вызывать только при остановленном измерении.
    /// </summary>
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
        }
    }

    /// <summary>Запускает обработку анализаторами.</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rawBuffer.Clear();
            _demodBuffer.Clear();
            _analyzersHost.Start(_sampleRate);
        }
    }

    /// <summary>Останавливает обработку анализаторами.</summary>
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

    /// <summary>
    /// Выделяет физический канал из блока устройства и записывает его
    /// в буферы сырого и демодулированного сигнала.
    /// </summary>
    public void Process(SignalBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (PhysicalChannel >= block.Channels)
        {
            return;
        }

        var frames = block.FrameCount;
        var mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            mono[frame] = block.Samples[frame * block.Channels + PhysicalChannel];
        }

        var raw = new SignalBlock(
            mono,
            1,
            block.SampleRate,
            block.Sequence,
            block.Timestamp);

        RawBuffer.Write(raw);
        WriteDemodulated(raw);
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

    private void WriteDemodulated(SignalBlock raw)
    {
        var demod = DemodBuffer;

        if (!Demodulation.Enabled)
        {
            // Пока демодуляция выключена, источник «Демодулированный»
            // повторяет сырой сигнал.
            demod.Write(raw);
            return;
        }

        var values = Demodulation.Process(raw);
        var samples = new float[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            samples[i] = (float)Math.Clamp(values[i], -1.0, 1.0);
        }

        demod.Write(new SignalBlock(
            samples,
            1,
            raw.SampleRate,
            raw.Sequence,
            raw.Timestamp));
    }
}
