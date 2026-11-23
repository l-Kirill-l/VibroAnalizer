using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Calibration;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Recording;
using VibroAnalyzer.Core.Routing;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Playback;

/// <summary>
/// Шина воспроизведения: отдаёт записанные отсчёты в те же кольцевые
/// буферы и анализаторы, что и обычная логическая шина.
/// </summary>
public sealed class PlaybackBus : ISignalBus, IDisposable
{
    private const int HistorySeconds = 20;

    private readonly object _sync = new();
    private readonly float[] _raw;
    private readonly float[]? _demod;
    private readonly SignalRingBuffer _rawBuffer;
    private readonly SignalRingBuffer _demodBuffer;
    private readonly AnalyzerHost _analyzersHost;
    private readonly int _sampleRate;

    private long _sequence;
    private bool _disposed;

    public PlaybackBus(RecordingBusInfo info, float[] raw, float[]? demod, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(raw);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        _raw = raw;
        _demod = demod;
        _sampleRate = sampleRate;

        Id = info.Id != Guid.Empty ? info.Id : Guid.NewGuid();
        Name = string.IsNullOrWhiteSpace(info.Name) ? "Запись" : info.Name;
        Kind = info.Kind ?? "";
        Calibration = new CalibrationProfile();
        Demodulation = new DemodulationChain();

        var capacity = Math.Max(sampleRate * HistorySeconds, 4096);
        _rawBuffer = new SignalRingBuffer(1, capacity);
        _demodBuffer = new SignalRingBuffer(1, capacity);
        _analyzersHost = new AnalyzerHost(_rawBuffer, _demodBuffer, Calibration);
    }

    public Guid Id { get; }

    public string Name { get; }

    public int SampleRate => _sampleRate;

    public int Channels => 1;

    public bool IsVirtual => false;

    public CalibrationProfile Calibration { get; }

    public SignalRingBuffer RawBuffer => _rawBuffer;

    public SignalRingBuffer DemodBuffer => _demodBuffer;

    public AnalyzerHost Analyzers => _analyzersHost;

    public DemodulationChain Demodulation { get; }

    /// <summary>Тип исходной шины: input, virtual или output.</summary>
    public string Kind { get; }

    /// <summary>Запускает анализаторы над буферами воспроизведения.</summary>
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

    /// <summary>Останавливает анализаторы. Повторный вызов безопасен.</summary>
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
            _analyzersHost.Add(analyzer);
        }
    }

    public void RemoveAnalyzer(AnalyzerBase analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        lock (_sync)
        {
            _analyzersHost.Remove(analyzer);
        }
    }

    /// <summary>
    /// Записывает в буферы кадры записи, начиная с абсолютной позиции.
    /// Если демодулированного файла нет, демодулированный буфер повторяет сырой.
    /// </summary>
    internal void WriteFrames(double positionFrames, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var start = double.IsFinite(positionFrames)
                ? (long)Math.Floor(positionFrames)
                : 0;

            var rawSamples = new float[frames];
            var demodSamples = new float[frames];

            for (var i = 0; i < frames; i++)
            {
                var index = start + i;
                var inRange = index >= 0 && index < _raw.Length;
                var rawValue = inRange ? _raw[index] : 0f;

                rawSamples[i] = rawValue;

                if (_demod is not null && index >= 0 && index < _demod.Length)
                {
                    demodSamples[i] = _demod[index];
                }
                else
                {
                    demodSamples[i] = rawValue;
                }
            }

            var sequence = Interlocked.Increment(ref _sequence);
            var timestamp = DateTimeOffset.UtcNow;

            _rawBuffer.Write(new SignalBlock(rawSamples, 1, _sampleRate, sequence, timestamp));
            _demodBuffer.Write(new SignalBlock(demodSamples, 1, _sampleRate, sequence, timestamp));
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
        }
    }
}
