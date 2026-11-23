using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.General;

/// <summary>
/// Оценка шумового пола и отношения сигнал/шум по распределению
/// кратковременных СКЗ.
/// </summary>
public sealed class NoiseFloorAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized, IAnalyzerToggleable
{
    private const double MinLevelDb = -240.0;

    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private readonly AnalyzerParameter[] _parameters;
    private readonly AnalyzerToggle[] _toggles;

    public NoiseFloorAnalyzer()
    {
        _parameters = new[]
        {
            new AnalyzerParameter(
                "Кадр",
                5,
                100,
                () => FrameMilliseconds,
                value => FrameMilliseconds = Math.Clamp(value, 5, 100),
                "мс"),
            new AnalyzerParameter(
                "Шумовой процентиль",
                1,
                50,
                () => NoisePercentile,
                value => NoisePercentile = Math.Clamp(value, 1, 50),
                "%"),
            new AnalyzerParameter(
                "Сигнальный процентиль",
                50,
                99,
                () => SignalPercentile,
                value => SignalPercentile = Math.Clamp(value, 50, 99),
                "%"),
        };

        _toggles = new[]
        {
            new AnalyzerToggle(
                "Вычитать DC",
                () => RemoveDc,
                value => RemoveDc = value),
        };
    }

    /// <summary>Длительность кадра, мс.</summary>
    public double FrameMilliseconds { get; set; } = 20.0;

    /// <summary>Процентиль шумового пола.</summary>
    public double NoisePercentile { get; set; } = 10.0;

    /// <summary>Процентиль сигнального уровня.</summary>
    public double SignalPercentile { get; set; } = 95.0;

    /// <summary>Вычитать DC из кадра.</summary>
    public bool RemoveDc { get; set; } = true;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => _parameters;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerToggle> Toggles => _toggles;

    /// <inheritdoc />
    public override string Title => "Шумовой пол и SNR";

    /// <inheritdoc />
    public override string Category => "Общие измерения";

    /// <inheritdoc />
    public override string Description =>
        "Разбивает окно на кадры, считает СКЗ каждого кадра и оценивает шумовой пол "
        + "по нижнему процентилю, уровень сигнала — по верхнему. Разность даёт SNR "
        + "в децибелах.";

    /// <inheritdoc />
    public string XLabel => "Уровень кадра, дБFS";

    /// <inheritdoc />
    public string YLabel => "Частота";

    /// <inheritdoc />
    public double[] X => Array.Empty<double>();

    /// <inheritdoc />
    public double[] Y => Array.Empty<double>();

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(16384, sampleRate * 2);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            Publish(MinLevelDb, MinLevelDb, 0.0, MinLevelDb, 0);
            MarkUpdated();
            return;
        }

        var frameFrames = Math.Max(64, (int)Math.Round(sampleRate * FrameMilliseconds / 1000.0));
        frameFrames = Math.Min(frameFrames, samples.Length);
        var levels = new List<double>();

        for (var start = 0; start + frameFrames <= samples.Length; start += frameFrames)
        {
            var frame = new double[frameFrames];
            Array.Copy(samples, start, frame, 0, frameFrames);

            if (RemoveDc)
            {
                var mean = SignalMath.Mean(frame);
                for (var index = 0; index < frame.Length; index++)
                {
                    frame[index] -= mean;
                }
            }

            var rms = SignalMath.Rms(frame);
            levels.Add(SignalMath.DbFs(rms));
        }

        if (levels.Count == 0)
        {
            Publish(MinLevelDb, MinLevelDb, 0.0, MinLevelDb, 0);
            MarkUpdated();
            return;
        }

        levels.Sort();
        var noise = Percentile(levels, NoisePercentile);
        var signal = Percentile(levels, SignalPercentile);
        var snr = signal - noise;
        var peak = SignalMath.DbFs(SignalMath.Peak(samples));

        Publish(noise, signal, snr, peak, levels.Count);
        MarkUpdated();
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return MinLevelDb;
        }

        var position = Math.Clamp(percentile, 0.0, 100.0) / 100.0 * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] * (1.0 - fraction) + sorted[upper] * fraction;
    }

    private void Publish(
        double noiseFloorDb,
        double signalDb,
        double snrDb,
        double peakDb,
        int frames)
    {
        _readouts = new[]
        {
            new AnalyzerReadout("Шумовой пол", noiseFloorDb, "дБFS"),
            new AnalyzerReadout("Уровень сигнала", signalDb, "дБFS"),
            new AnalyzerReadout("SNR", snrDb, "дБ"),
            new AnalyzerReadout("Пик", peakDb, "дБFS"),
            new AnalyzerReadout("Кадров", frames, "шт."),
        };
    }
}
