using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Vibration;

/// <summary>
/// Порядковый анализ: спектр пересчитывается в порядки относительно частоты вращения.
/// </summary>
public sealed class OrderAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized
{
    private const int WindowFrames = 16384;
    private const double MinOrder = 0.5;
    private const double MaxOrder = 20.0;
    private const double MinLevelDb = -240.0;
    private const double MinRpm = 1.0;
    private const double MaxRpm = 100000.0;

    private volatile double[] _orders = Array.Empty<double>();
    private volatile double[] _levelsDb = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <summary>Частота вращения вала, об/мин.</summary>
    public double Rpm { get; set; } = 1500.0;

    /// <inheritdoc />
    public override string Title => "Порядковый анализ";

    /// <inheritdoc />
    public override string Category => "Вибрация и механика";

    /// <inheritdoc />
    public override string Description =>
        "Переводит спектр в порядки — частоты, кратные частоте вращения (1×, 2×, …). "
        + "Порядковая шкала не зависит от оборотов, поэтому удобна для поиска дисбаланса, "
        + "несоосности и дефектов зубчатых передач.";

    /// <inheritdoc />
    public string XLabel => "Порядок, ×";

    /// <inheritdoc />
    public string YLabel => "дБFS (ампл.)";

    /// <inheritdoc />
    public double[] X => _orders;

    /// <inheritdoc />
    public double[] Y => _levelsDb;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => new AnalyzerParameter[]
    {
        new("Обороты", MinRpm, MaxRpm, () => Rpm,
            value => Rpm = double.IsFinite(value) ? Math.Clamp(value, MinRpm, MaxRpm) : 1500.0, "об/мин"),
    };

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate) => WindowFrames;

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        var shaftFrequency = Rpm / 60.0;

        if (samples is null || samples.Length == 0 || sampleRate <= 0 || shaftFrequency <= 0.0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, MinLevelDb, MinLevelDb);
            MarkUpdated();
            return;
        }

        var spectrum = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann);
        var frequencies = spectrum.Frequencies;
        var levels = spectrum.MagnitudesDb;

        var minFrequency = MinOrder * shaftFrequency;
        var maxFrequency = MaxOrder * shaftFrequency;

        var orders = new List<double>();
        var selectedLevels = new List<double>();

        for (var i = 0; i < frequencies.Length; i++)
        {
            var frequency = frequencies[i];
            if (frequency >= minFrequency && frequency <= maxFrequency)
            {
                orders.Add(frequency / shaftFrequency);
                selectedLevels.Add(levels[i]);
            }
        }

        if (orders.Count == 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, MinLevelDb, MinLevelDb);
            MarkUpdated();
            return;
        }

        var dominantIndex = 0;
        for (var i = 1; i < selectedLevels.Count; i++)
        {
            if (selectedLevels[i] > selectedLevels[dominantIndex])
            {
                dominantIndex = i;
            }
        }

        var level1X = LevelNear(frequencies, levels, shaftFrequency);
        var level2X = LevelNear(frequencies, levels, 2.0 * shaftFrequency);

        Publish(orders.ToArray(), selectedLevels.ToArray(), orders[dominantIndex], level1X, level2X);
        MarkUpdated();
    }

    private void Publish(
        double[] orders,
        double[] levelsDb,
        double dominantOrder,
        double level1X,
        double level2X)
    {
        _orders = orders;
        _levelsDb = levelsDb;
        _readouts = new[]
        {
            new AnalyzerReadout("Доминирующий порядок", dominantOrder, "×"),
            new AnalyzerReadout("Уровень 1x", level1X, "дБFS (ампл.)"),
            new AnalyzerReadout("Уровень 2x", level2X, "дБFS (ампл.)"),
        };
    }

    /// <summary>
    /// Берёт максимальный уровень в окрестности одного бина от заданной частоты.
    /// </summary>
    private static double LevelNear(double[] frequencies, double[] levelsDb, double frequency)
    {
        if (frequencies.Length == 0 || levelsDb.Length == 0 || frequency < 0.0 || frequency > frequencies[^1])
        {
            return MinLevelDb;
        }

        var nearest = 0;
        var bestDistance = Math.Abs(frequencies[0] - frequency);
        for (var i = 1; i < frequencies.Length; i++)
        {
            var distance = Math.Abs(frequencies[i] - frequency);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = i;
            }
        }

        var start = Math.Max(0, nearest - 1);
        var end = Math.Min(levelsDb.Length - 1, nearest + 1);
        var level = MinLevelDb;
        for (var i = start; i <= end; i++)
        {
            if (levelsDb[i] > level)
            {
                level = levelsDb[i];
            }
        }

        return level;
    }
}
