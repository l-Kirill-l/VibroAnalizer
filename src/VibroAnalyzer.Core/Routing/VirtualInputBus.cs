using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Calibration;
using VibroAnalyzer.Core.Dsp;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Routing;

/// <summary>
/// Виртуальный вход: объединяет несколько логических входов в один сигнал.
/// Позволяет усреднять датчики, складывать полосы НЧ+ВЧ и строить другие
/// производные каналы, к которым привязываются обычные анализаторы.
/// </summary>
public sealed class VirtualInputBus : ISignalBus, IDisposable
{
    private const int HistorySeconds = 5;
    private const int MaxQueueSeconds = 2;
    private const int MaxChunkPerSecond = 50;

    private readonly object _sync = new();
    private readonly List<AnalyzerBase> _analyzers = new();
    private readonly FusionSource[] _sources;

    private int _sampleRate;
    private SignalRingBuffer _rawBuffer;
    private SignalRingBuffer _demodBuffer;
    private AnalyzerHost _analyzersHost;
    private BiquadFilter?[] _filters;
    private Queue<double>[] _queues = Array.Empty<Queue<double>>();
    private long[] _lastTotals = Array.Empty<long>();
    private long _sequence;
    private bool _disposed;

    public VirtualInputBus(
        string name,
        IEnumerable<FusionSource> sources,
        FusionMode mode,
        int sampleRate,
        CalibrationProfile? calibration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sources);

        var list = sources.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("Виртуальному входу нужен хотя бы один источник.", nameof(sources));
        }

        if (mode == FusionMode.Difference && list.Count < 2)
        {
            throw new ArgumentException("Для разности нужно не меньше двух источников.", nameof(sources));
        }

        foreach (var source in list)
        {
            if (source is null || source.Source is null)
            {
                throw new ArgumentException("Источник виртуального входа не задан.", nameof(sources));
            }
        }

        Name = name;
        Mode = mode;
        _sources = list.ToArray();
        Calibration = calibration ?? new CalibrationProfile();
        Demodulation = new DemodulationChain();

        var rate = Math.Max(1, sampleRate);
        (_rawBuffer, _demodBuffer, _analyzersHost, _filters) = Build(rate);
        _sampleRate = rate;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; }

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

    public bool IsVirtual => true;

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

    /// <summary>Способ объединения источников.</summary>
    public FusionMode Mode { get; set; }

    /// <summary>Источники виртуального входа.</summary>
    public IReadOnlyList<FusionSource> Sources => _sources;

    /// <summary>Краткое описание для интерфейса.</summary>
    public string Description
    {
        get
        {
            var names = string.Join(" + ", _sources.Select(source => source.Source.Name));
            return $"{Mode}: {names}";
        }
    }

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
            (_rawBuffer, _demodBuffer, _analyzersHost, _filters) = Build(rate);
            _sampleRate = rate;
            _sequence = 0;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _rawBuffer.Clear();
            _demodBuffer.Clear();
            ResetSourceQueues();
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

    /// <summary>
    /// Проверяет, зависит ли этот виртуальный вход от указанной шины
    /// (напрямую или через другие виртуальные входы).
    /// </summary>
    public bool DependsOn(Guid busId)
    {
        foreach (var source in _sources)
        {
            if (source.Source.Id == busId)
            {
                return true;
            }

            if (source.Source is VirtualInputBus virtualSource && virtualSource.DependsOn(busId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Пересчитывает смешанный сигнал только из новых отсчётов источников.
    /// Вызывается таймером движка, но может вызываться и вручную (например, в тестах).
    /// </summary>
    public void Update()
    {
        lock (_sync)
        {
            if (_disposed || _sampleRate <= 0)
            {
                return;
            }

            AcquireNewSamples();

            var maxChunk = Math.Max(64, _sampleRate / MaxChunkPerSecond);
            var maxQueue = Math.Max(maxChunk, _sampleRate * MaxQueueSeconds);

            while (true)
            {
                var available = int.MaxValue;

                for (var index = 0; index < _queues.Length; index++)
                {
                    available = Math.Min(available, _queues[index].Count);
                }

                if (available <= 0)
                {
                    break;
                }

                var frames = Math.Min(available, maxChunk);
                var snapshots = new double[_sources.Length][];

                for (var index = 0; index < _sources.Length; index++)
                {
                    var snapshot = new double[frames];
                    var queue = _queues[index];

                    for (var i = 0; i < frames; i++)
                    {
                        snapshot[i] = queue.Dequeue();
                    }

                    var filter = _filters[index];
                    if (filter is not null)
                    {
                        for (var i = 0; i < snapshot.Length; i++)
                        {
                            snapshot[i] = filter.Process(snapshot[i]);
                        }
                    }

                    snapshots[index] = snapshot;
                }

                var mixed = Combine(snapshots);
                WriteBlock(mixed, _sampleRate);

                for (var index = 0; index < _queues.Length; index++)
                {
                    while (_queues[index].Count > maxQueue)
                    {
                        _queues[index].Dequeue();
                    }
                }
            }
        }
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
            _queues = Array.Empty<Queue<double>>();
            _lastTotals = Array.Empty<long>();
        }
    }

    private void ResetSourceQueues()
    {
        _queues = new Queue<double>[_sources.Length];
        _lastTotals = new long[_sources.Length];

        for (var index = 0; index < _sources.Length; index++)
        {
            _queues[index] = new Queue<double>();
            _lastTotals[index] = _sources[index].Source.RawBuffer.TotalFramesWritten;
        }
    }

    private void AcquireNewSamples()
    {        for (var index = 0; index < _sources.Length; index++)
        {
            var buffer = _sources[index].Source.RawBuffer;
            var total = buffer.TotalFramesWritten;
            var last = _lastTotals[index];

            if (total <= last)
            {
                continue;
            }

            var count = (int)Math.Min(total - last, buffer.CapacityFrames);
            if (count <= 0)
            {
                _lastTotals[index] = total;
                continue;
            }

            var samples = buffer.Snapshot(count, 0);
            var queue = _queues[index];

            for (var i = 0; i < samples.Length; i++)
            {
                queue.Enqueue(samples[i]);
            }

            _lastTotals[index] = total;
        }
    }

    private double[] Combine(double[][] snapshots)
    {
        var frames = snapshots[0].Length;
        var mixed = new double[frames];
        var gains = new double[_sources.Length];

        for (var index = 0; index < _sources.Length; index++)
        {
            gains[index] = double.IsFinite(_sources[index].Gain) ? _sources[index].Gain : 1.0;
        }

        switch (Mode)
        {
            case FusionMode.Average:
                for (var index = 0; index < snapshots.Length; index++)
                {
                    var snapshot = snapshots[index];
                    for (var i = 0; i < frames; i++)
                    {
                        mixed[i] += snapshot[i];
                    }
                }

                for (var i = 0; i < frames; i++)
                {
                    mixed[i] /= snapshots.Length;
                }

                break;

            case FusionMode.Weighted:
                var weightSum = 0.0;
                for (var index = 0; index < snapshots.Length; index++)
                {
                    var snapshot = snapshots[index];
                    var gain = gains[index];
                    weightSum += Math.Abs(gain);

                    for (var i = 0; i < frames; i++)
                    {
                        mixed[i] += snapshot[i] * gain;
                    }
                }

                if (weightSum > 1e-12)
                {
                    for (var i = 0; i < frames; i++)
                    {
                        mixed[i] /= weightSum;
                    }
                }

                break;

            case FusionMode.Difference:
                for (var index = 0; index < snapshots.Length; index++)
                {
                    var snapshot = snapshots[index];
                    var gain = gains[index];
                    var sign = index == 0 ? 1.0 : -1.0;

                    for (var i = 0; i < frames; i++)
                    {
                        mixed[i] += sign * snapshot[i] * gain;
                    }
                }

                break;

            case FusionMode.Minimum:
                for (var i = 0; i < frames; i++)
                {
                    mixed[i] = double.PositiveInfinity;
                }

                for (var index = 0; index < snapshots.Length; index++)
                {
                    var snapshot = snapshots[index];
                    var gain = gains[index];

                    for (var i = 0; i < frames; i++)
                    {
                        mixed[i] = Math.Min(mixed[i], snapshot[i] * gain);
                    }
                }

                break;

            case FusionMode.Maximum:
                for (var i = 0; i < frames; i++)
                {
                    mixed[i] = double.NegativeInfinity;
                }

                for (var index = 0; index < snapshots.Length; index++)
                {
                    var snapshot = snapshots[index];
                    var gain = gains[index];

                    for (var i = 0; i < frames; i++)
                    {
                        mixed[i] = Math.Max(mixed[i], snapshot[i] * gain);
                    }
                }

                break;

            default:
                // Sum и BandMerge: суммы с учётом весов.
                for (var index = 0; index < snapshots.Length; index++)
                {
                    var snapshot = snapshots[index];
                    var gain = gains[index];

                    for (var i = 0; i < frames; i++)
                    {
                        mixed[i] += snapshot[i] * gain;
                    }
                }

                break;
        }

        return mixed;
    }

    private void WriteBlock(double[] mixed, int sampleRate)
    {
        var frames = mixed.Length;
        var samples = new float[frames];

        for (var i = 0; i < frames; i++)
        {
            samples[i] = (float)Math.Clamp(mixed[i], -1.0, 1.0);
        }

        var raw = new SignalBlock(
            samples,
            1,
            sampleRate,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow);

        _rawBuffer.Write(raw);
        WriteDemodulated(raw);
    }

    private (
        SignalRingBuffer Raw,
        SignalRingBuffer Demod,
        AnalyzerHost Host,
        BiquadFilter?[] Filters) Build(int sampleRate)
    {
        var capacity = Math.Max(sampleRate * HistorySeconds, 4096);
        var raw = new SignalRingBuffer(1, capacity);
        var demod = new SignalRingBuffer(1, capacity);
        var host = new AnalyzerHost(raw, demod, Calibration);
        var filters = new BiquadFilter?[_sources.Length];

        ResetSourceQueues();

        for (var index = 0; index < _sources.Length; index++)
        {
            var source = _sources[index];
            filters[index] = source.Band switch
            {
                FusionBand.LowPass => BiquadFilter.CreateLowPass(sampleRate, source.CutoffHz),
                FusionBand.HighPass => BiquadFilter.CreateHighPass(sampleRate, source.CutoffHz),
                _ => null
            };
        }

        foreach (var analyzer in _analyzers)
        {
            host.Add(analyzer);
        }

        return (raw, demod, host, filters);
    }

    private void WriteDemodulated(SignalBlock raw)
    {
        if (!Demodulation.Enabled)
        {
            _demodBuffer.Write(raw);
            return;
        }

        var values = Demodulation.Process(raw);
        var samples = new float[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            samples[i] = (float)Math.Clamp(values[i], -1.0, 1.0);
        }

        _demodBuffer.Write(new SignalBlock(
            samples,
            1,
            raw.SampleRate,
            raw.Sequence,
            raw.Timestamp));
    }
}
