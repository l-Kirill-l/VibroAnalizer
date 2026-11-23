using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Medicine;

/// <summary>
/// Пульс и RR-интервалы: огибающая, поиск ударов и статистика интервалов.
/// </summary>
public sealed class PulseAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized, IAnalyzerToggleable
{
    private const int WindowFrames = 16384;
    private const double MinPeakDistanceSeconds = 0.3;

    private volatile double[] _times = Array.Empty<double>();
    private volatile double[] _envelope = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private readonly AnalyzerParameter[] _parameters;
    private readonly AnalyzerToggle[] _toggles;

    public PulseAnalyzer()
    {
        _parameters = new[]
        {
            new AnalyzerParameter(
                "ФВЧ полосы",
                0.1,
                5.0,
                () => Filters.HighPassHz,
                value => Filters.HighPassHz = value,
                "Гц"),
            new AnalyzerParameter(
                "ФНЧ полосы",
                2.0,
                20.0,
                () => Filters.LowPassHz,
                value => Filters.LowPassHz = value,
                "Гц"),
            new AnalyzerParameter(
                "Срез огибающей",
                1.0,
                20.0,
                () => CutoffHz,
                value => CutoffHz = value,
                "Гц"),
        };

        _toggles = new[]
        {
            new AnalyzerToggle(
                "Полосовой фильтр 0.5–10 Гц",
                () => Filters.BandPassEnabled,
                value => Filters.BandPassEnabled = value),
            new AnalyzerToggle(
                "Режекция 50 Гц",
                () => Filters.Notch50Enabled,
                value => Filters.Notch50Enabled = value),
            new AnalyzerToggle(
                "Режекция 60 Гц",
                () => Filters.Notch60Enabled,
                value => Filters.Notch60Enabled = value),
        };
    }

    /// <summary>Отключаемые фильтры пульсового сигнала.</summary>
    public AnalysisFilterSettings Filters { get; } = new()
    {
        HighPassHz = 0.5,
        LowPassHz = 10.0,
    };

    /// <summary>Частота среза фильтра огибающей, Гц.</summary>
    public double CutoffHz { get; set; } = 5.0;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => _parameters;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerToggle> Toggles => _toggles;

    /// <inheritdoc />
    public override string Title => "Пульс и RR-интервалы";

    /// <inheritdoc />
    public override string Category => "Медицина";

    /// <inheritdoc />
    public override string Description =>
        "Выделяет огибающую пульсового сигнала и находит отдельные удары с минимальным интервалом 0,3 с. "
        + "По найденным ударам считает пульс, средний RR-интервал и его разброс — простейший контроль ритма.";

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
        => Math.Max(WindowFrames, sampleRate * 8);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0, 0);
            MarkUpdated();
            return;
        }

        var count = samples.Length;
        var filtered = Filters.Apply(samples, sampleRate);
        var envelopeFilter = BiquadFilter.CreateLowPass(sampleRate, CutoffHz);
        var envelope = new double[count];
        var times = new double[count];

        for (var i = 0; i < count; i++)
        {
            envelope[i] = envelopeFilter.Process(Math.Abs(filtered[i]));
            times[i] = (double)i / sampleRate;
        }

        var peaks = DetectPeaks(envelope, sampleRate);

        var meanRrMs = 0.0;
        var sdRrMs = 0.0;
        var pulseBpm = 0.0;

        if (peaks.Count >= 2)
        {
            var intervals = new double[peaks.Count - 1];
            var sum = 0.0;

            for (var i = 0; i < intervals.Length; i++)
            {
                intervals[i] = (peaks[i + 1] - peaks[i]) * 1000.0 / sampleRate;
                sum += intervals[i];
            }

            meanRrMs = sum / intervals.Length;

            if (intervals.Length > 1)
            {
                var variance = 0.0;
                for (var i = 0; i < intervals.Length; i++)
                {
                    var delta = intervals[i] - meanRrMs;
                    variance += delta * delta;
                }

                sdRrMs = Math.Sqrt(variance / (intervals.Length - 1));
            }

            if (meanRrMs > 0.0)
            {
                pulseBpm = 60000.0 / meanRrMs;
            }
        }

        Publish(times, envelope, pulseBpm, meanRrMs, sdRrMs, peaks.Count);
        MarkUpdated();
    }

    private static List<int> DetectPeaks(double[] envelope, int sampleRate)
    {
        var peaks = new List<int>();
        if (envelope.Length < 3 || sampleRate <= 0)
        {
            return peaks;
        }

        var maximum = SignalMath.Peak(envelope);
        if (maximum < 1e-12)
        {
            return peaks;
        }

        var threshold = maximum * 0.3;
        var minDistance = Math.Max(1, (int)Math.Round(MinPeakDistanceSeconds * sampleRate));

        for (var i = 1; i < envelope.Length - 1; i++)
        {
            var value = envelope[i];
            if (value < threshold || value < envelope[i - 1] || value <= envelope[i + 1])
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
            else if (value > envelope[last])
            {
                peaks[^1] = i;
            }
        }

        return peaks;
    }

    private void Publish(
        double[] times,
        double[] envelope,
        double pulseBpm,
        double meanRrMs,
        double sdRrMs,
        int beats)
    {
        _times = times;
        _envelope = envelope;
        _readouts = new[]
        {
            new AnalyzerReadout("Пульс", pulseBpm, "уд/мин"),
            new AnalyzerReadout("Средний RR", meanRrMs, "мс"),
            new AnalyzerReadout("СКО RR", sdRrMs, "мс"),
            new AnalyzerReadout("Число ударов", beats, "шт."),
        };
    }
}
