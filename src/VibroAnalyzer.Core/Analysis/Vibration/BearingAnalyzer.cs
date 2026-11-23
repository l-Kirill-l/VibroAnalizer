using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Vibration;

/// <summary>
/// Анализатор подшипниковых частот: рассчитывает ожидаемые частоты дефектов и их уровни в спектре.
/// </summary>
public sealed class BearingAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized
{
    private const int WindowFrames = 16384;
    private const double MinLevelDb = -240.0;
    private const double MinRpm = 1.0;
    private const double MaxRpm = 100000.0;

    private volatile double[] _frequencies = Array.Empty<double>();
    private volatile double[] _levelsDb = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <summary>Частота вращения вала, об/мин.</summary>
    public double Rpm { get; set; } = 1500.0;

    /// <summary>Множитель наружной обоймы (BPFO) относительно 1×.</summary>
    public double BpfoMultiplier { get; set; } = 3.5;

    /// <summary>Множитель внутренней обоймы (BPFI) относительно 1×.</summary>
    public double BpfiMultiplier { get; set; } = 5.4;

    /// <summary>Множитель тела качения (BSF) относительно 1×.</summary>
    public double BsfMultiplier { get; set; } = 2.4;

    /// <summary>Множитель сепаратора (FTF) относительно 1×.</summary>
    public double FtfMultiplier { get; set; } = 0.4;

    /// <inheritdoc />
    public override string Title => "Подшипниковые частоты";

    /// <inheritdoc />
    public override string Category => "Вибрация и механика";

    /// <inheritdoc />
    public override string Description =>
        "По оборотам и геометрическим множителям рассчитывает ожидаемые частоты дефектов подшипника "
        + "(BPFO, BPFI, BSF, FTF) и берёт их уровни из спектра сигнала. Помогает быстро проверить, "
        + "совпадают ли пики вибрации с характерными частотами подшипника.";

    /// <inheritdoc />
    public string XLabel => "Частота, Гц";

    /// <inheritdoc />
    public string YLabel => "дБFS (ампл.)";

    /// <inheritdoc />
    public double[] X => _frequencies;

    /// <inheritdoc />
    public double[] Y => _levelsDb;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => new AnalyzerParameter[]
    {
        new("Обороты", MinRpm, MaxRpm, () => Rpm,
            value => Rpm = Sanitize(value, MinRpm, MaxRpm, 1500.0), "об/мин"),
        new("BPFO ×", 1.0, 20.0, () => BpfoMultiplier,
            value => BpfoMultiplier = Sanitize(value, 1.0, 20.0, 3.5), ""),
        new("BPFI ×", 1.0, 20.0, () => BpfiMultiplier,
            value => BpfiMultiplier = Sanitize(value, 1.0, 20.0, 5.4), ""),
        new("BSF ×", 1.0, 20.0, () => BsfMultiplier,
            value => BsfMultiplier = Sanitize(value, 1.0, 20.0, 2.4), ""),
        new("FTF ×", 0.1, 2.0, () => FtfMultiplier,
            value => FtfMultiplier = Sanitize(value, 0.1, 2.0, 0.4), ""),
    };

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate) => WindowFrames;

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        var shaftFrequency = Rpm / 60.0;
        var bpfo = shaftFrequency * BpfoMultiplier;
        var bpfi = shaftFrequency * BpfiMultiplier;
        var bsf = shaftFrequency * BsfMultiplier;
        var ftf = shaftFrequency * FtfMultiplier;

        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            Publish(
                Array.Empty<double>(),
                Array.Empty<double>(),
                bpfo, MinLevelDb,
                bpfi, MinLevelDb,
                bsf, MinLevelDb,
                ftf, MinLevelDb);
            MarkUpdated();
            return;
        }

        var spectrum = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann);
        var frequencies = spectrum.Frequencies;
        var levels = spectrum.MagnitudesDb;

        Publish(
            frequencies,
            levels,
            bpfo, LevelNear(frequencies, levels, bpfo),
            bpfi, LevelNear(frequencies, levels, bpfi),
            bsf, LevelNear(frequencies, levels, bsf),
            ftf, LevelNear(frequencies, levels, ftf));

        MarkUpdated();
    }

    private void Publish(
        double[] frequencies,
        double[] levelsDb,
        double bpfo,
        double bpfoDb,
        double bpfi,
        double bpfiDb,
        double bsf,
        double bsfDb,
        double ftf,
        double ftfDb)
    {
        _frequencies = frequencies;
        _levelsDb = levelsDb;
        _readouts = new[]
        {
            new AnalyzerReadout("BPFO", bpfo, "Гц"),
            new AnalyzerReadout("BPFO уровень", bpfoDb, "дБFS (ампл.)"),
            new AnalyzerReadout("BPFI", bpfi, "Гц"),
            new AnalyzerReadout("BPFI уровень", bpfiDb, "дБFS (ампл.)"),
            new AnalyzerReadout("BSF", bsf, "Гц"),
            new AnalyzerReadout("BSF уровень", bsfDb, "дБFS (ампл.)"),
            new AnalyzerReadout("FTF", ftf, "Гц"),
            new AnalyzerReadout("FTF уровень", ftfDb, "дБFS (ампл.)"),
        };
    }

    /// <summary>
    /// Берёт максимальный уровень в окрестности одного бина от заданной частоты.
    /// </summary>
    private static double LevelNear(double[] frequencies, double[] levelsDb, double frequency)
    {
        if (frequencies.Length == 0 || levelsDb.Length == 0 || frequency < 0.0)
        {
            return MinLevelDb;
        }

        if (frequency > frequencies[^1])
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

    private static double Sanitize(double value, double minimum, double maximum, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}
