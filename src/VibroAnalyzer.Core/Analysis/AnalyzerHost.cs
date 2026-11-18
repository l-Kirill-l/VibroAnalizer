using VibroAnalyzer.Core.Calibration;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Analysis;

/// <summary>
/// Периодически передаёт снимки сигнала подключённым анализаторам.
/// Ошибка отдельного анализатора не прерывает работу остальных.
/// </summary>
public sealed class AnalyzerHost : IDisposable
{
    private readonly object _sync = new();
    private readonly SignalRingBuffer _raw;
    private readonly SignalRingBuffer _demod;
    private readonly CalibrationProfile _calibration;

    private AnalyzerBase[] _analyzers = Array.Empty<AnalyzerBase>();
    private Timer? _timer;
    private int _sampleRate;
    private int _running;
    private int _ticking;
    private bool _disposed;

    /// <summary>
    /// Создаёт хост анализаторов над буферами исходного и демодулированного сигнала.
    /// </summary>
    public AnalyzerHost(SignalRingBuffer raw, SignalRingBuffer demod, CalibrationProfile calibration)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(demod);
        ArgumentNullException.ThrowIfNull(calibration);

        _raw = raw;
        _demod = demod;
        _calibration = calibration;
    }

    /// <summary>Потокобезопасный снимок списка анализаторов.</summary>
    public IReadOnlyList<AnalyzerBase> Analyzers => Volatile.Read(ref _analyzers);

    /// <summary>Частота дискретизации, заданная при последнем запуске.</summary>
    public int SampleRate => Volatile.Read(ref _sampleRate);

    /// <summary>Добавляет анализатор в обработку.</summary>
    public void Add(AnalyzerBase analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        lock (_sync)
        {
            var current = _analyzers;
            var next = new AnalyzerBase[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = analyzer;
            Volatile.Write(ref _analyzers, next);
        }
    }

    /// <summary>Удаляет анализатор из обработки (первое вхождение).</summary>
    public void Remove(AnalyzerBase analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        lock (_sync)
        {
            var current = _analyzers;
            var index = Array.IndexOf(current, analyzer);
            if (index < 0)
            {
                return;
            }

            var next = new AnalyzerBase[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            Volatile.Write(ref _analyzers, next);
        }
    }

    /// <summary>Удаляет все анализаторы.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            Volatile.Write(ref _analyzers, Array.Empty<AnalyzerBase>());
        }
    }

    /// <summary>
    /// Запускает периодическую обработку. Повторный вызов не создаёт второй таймер.
    /// </summary>
    public void Start(int sampleRate, int intervalMs = 50)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        if (intervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "Интервал обработки должен быть больше нуля.");
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _sampleRate, sampleRate);
            Volatile.Write(ref _running, 1);

            if (_timer is null)
            {
                // Первый тик выполняется сразу, чтобы UI получил данные без задержки.
                _timer = new Timer(OnTimer, null, 0, intervalMs);
            }
            else
            {
                _timer.Change(intervalMs, intervalMs);
            }
        }
    }

    /// <summary>Останавливает периодическую обработку. Повторный вызов безопасен.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            Volatile.Write(ref _running, 0);
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Останавливает обработку и освобождает таймер. Повторный вызов безопасен.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Volatile.Write(ref _running, 0);
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimer(object? state)
    {
        if (Volatile.Read(ref _running) == 0)
        {
            return;
        }

        // Таймер может вызывать обработчик повторно, если предыдущий тик ещё идёт.
        if (Interlocked.Exchange(ref _ticking, 1) != 0)
        {
            return;
        }

        try
        {
            var sampleRate = Volatile.Read(ref _sampleRate);
            if (sampleRate <= 0)
            {
                return;
            }

            var analyzers = Volatile.Read(ref _analyzers);
            foreach (var analyzer in analyzers)
            {
                if (!analyzer.IsEnabled)
                {
                    continue;
                }

                try
                {
                    var buffer = analyzer.Source == AnalyzerSource.Demodulated ? _demod : _raw;
                    var channel = Math.Clamp(analyzer.Channel, 0, buffer.Channels - 1);
                    var frames = analyzer.GetWindowFrames(sampleRate);
                    if (frames <= 0)
                    {
                        continue;
                    }

                    var data = buffer.Snapshot(frames, channel);
                    analyzer.Calibration = _calibration;
                    analyzer.Process(data, sampleRate);
                }
                catch (Exception)
                {
                    // Сбой одного анализатора не должен останавливать таймер и остальные анализаторы.
                }
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }
}
