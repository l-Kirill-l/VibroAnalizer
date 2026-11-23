using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Medicine;

/// <summary>
/// Дыхание: медленный сигнал сглаживается ФНЧ 1 Гц, затем ищутся его экстремумы.
/// </summary>
public sealed class RespirationAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized, IAnalyzerToggleable
{
    private const int WindowFrames = 16384;
    private const double MinBreathDistanceSeconds = 0.5;

    private volatile double[] _times = Array.Empty<double>();
    private volatile double[] _envelope = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private readonly AnalyzerParameter[] _parameters;
    private readonly AnalyzerToggle[] _toggles;

    public RespirationAnalyzer()
    {
        _parameters = new[]
        {
            new AnalyzerParameter(
                "ФВЧ полосы",
                0.01,
                0.2,
                () => Filters.HighPassHz,
                value => Filters.HighPassHz = value,
                "Гц"),
            new AnalyzerParameter(
                "ФНЧ полосы",
                0.2,
                2.0,
                () => Filters.LowPassHz,
                value => Filters.LowPassHz = value,
                "Гц"),
        };

        _toggles = new[]
        {
            new AnalyzerToggle(
                "Полосовой фильтр дыхания",
                () => Filters.BandPassEnabled,
                value => Filters.BandPassEnabled = value),
        };
    }

    /// <summary>Отключаемые фильтры дыхательного сигнала.</summary>
    public AnalysisFilterSettings Filters { get; } = new()
    {
        HighPassHz = 0.05,
        LowPassHz = 1.0,
        MainsNotchEnabled = false,
    };

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => _parameters;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerToggle> Toggles => _toggles;

    /// <inheritdoc />
    public override string Title => "Дыхание";

    /// <inheritdoc />
    public override string Category => "Медицина";

    /// <inheritdoc />
    public override string Description =>
        "Сглаживает сигнал фильтром 1 Гц и ищет медленные вдохи-выдохи по локальным максимумам. "
        + "Оценивает частоту дыхания, средний период и амплитуду — для контроля дыхания по датчику движения или потока.";

    /// <inheritdoc />
    public string XLabel => "Время, с";

    /// <inheritdoc />
    public string YLabel => "Огибающая";

    /// <inheritdoc />
    public double[] X => _times;

    /// <inheritdoc />
    public double[] Y => _envelope;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowFrames, sampleRate * 20);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var count = samples.Length;
        var filtered = Filters.Apply(samples, sampleRate);
        var smoothed = new double[count];
        var times = new double[count];
        var envelope = new double[count];

        for (var i = 0; i < count; i++)
        {
            smoothed[i] = filtered[i];
            times[i] = (double)i / sampleRate;
        }

        var mean = SignalMath.Mean(smoothed);
        for (var i = 0; i < count; i++)
        {
            smoothed[i] -= mean;
            envelope[i] = Math.Abs(smoothed[i]);
        }

        var peaks = DetectBreaths(smoothed, sampleRate);

        var ratePerMinute = 0.0;
        var periodSeconds = 0.0;

        if (peaks.Count >= 2)
        {
            var sum = 0.0;
            for (var i = 0; i < peaks.Count - 1; i++)
            {
                sum += peaks[i + 1] - peaks[i];
            }

            periodSeconds = sum / (peaks.Count - 1) / sampleRate;
            if (periodSeconds > 0.0)
            {
                ratePerMinute = 60.0 / periodSeconds;
            }
        }

        var amplitude = SignalMath.PeakToPeak(smoothed) * 0.5;

        Publish(times, envelope, ratePerMinute, periodSeconds, amplitude);
        MarkUpdated();
    }

    private static List<int> DetectBreaths(double[] signal, int sampleRate)
    {
        var peaks = new List<int>();
        if (signal.Length < 3 || sampleRate <= 0)
        {
            return peaks;
        }

        var maximum = SignalMath.Peak(signal);
        if (maximum < 1e-12)
        {
            return peaks;
        }

        var threshold = maximum * 0.25;
        var minDistance = Math.Max(1, (int)Math.Round(MinBreathDistanceSeconds * sampleRate));

        for (var i = 1; i < signal.Length - 1; i++)
        {
            var value = signal[i];
            if (value < threshold || value < signal[i - 1] || value <= signal[i + 1])
            {
                continue;
            }

            if (peaks.Count == 0)
            {
                peaks.Add(i);
                continue;
            }

            var last = peaks[^1];
            if (i - last >= minDistance)
            {
                peaks.Add(i);
            }
            else if (value > signal[last])
            {
                peaks[^1] = i;
            }
        }

        return peaks;
    }

    private void Publish(
        double[] times,
        double[] envelope,
        double ratePerMinute,
        double periodSeconds,
        double amplitude)
    {
        _times = times;
        _envelope = envelope;
        _readouts = new[]
        {
            new AnalyzerReadout("Частота дыхания", ratePerMinute, "в мин"),
            new AnalyzerReadout("Период", periodSeconds, "с"),
            new AnalyzerReadout("Амплитуда", amplitude, "ед."),
        };
    }
}
