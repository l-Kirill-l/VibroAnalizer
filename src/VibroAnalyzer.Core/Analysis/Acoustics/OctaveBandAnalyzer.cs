using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Acoustics;

/// <summary>
/// Третьоктавный спектр: уровень в стандартных полосах 20 Гц … 20 кГц.
/// </summary>
public sealed class OctaveBandAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 16384;
    private const double MinLevelDb = -240.0;

    // Эквивалентная шумовая полоса окна Ханна.
    private const double HannEnbw = 1.5;

    private static readonly double[] Centers =
    {
        20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160,
        200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000,
        20000
    };

    // Точные центры по IEC 61260-1 (base-10): 1000 * 10^(n/10), n = -17..13.
    private static readonly double[] ExactCenters = Enumerable
        .Range(-17, Centers.Length)
        .Select(index => 1000.0 * Math.Pow(10.0, index / 10.0))
        .ToArray();

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _dominantCenterHz;
    private double _dominantLevelDb;
    private double _overallLevelDb;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Третьоктавные полосы";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Акустика";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Разбивает спектр на стандартные третьоктавные полосы с центрами от 20 Гц до 20 кГц "
        + "и показывает уровень каждой полосы в дБFS. Применяется для акустических измерений, "
        + "оценки шума и настройки эквалайзеров.";

    /// <summary>Центр доминирующей полосы, Гц.</summary>
    public double DominantCenterHz => Volatile.Read(ref _dominantCenterHz);

    /// <summary>Уровень доминирующей полосы, дБFS.</summary>
    public double DominantLevelDb => Volatile.Read(ref _dominantLevelDb);

    /// <summary>Общий уровень, дБFS.</summary>
    public double OverallLevelDb => Volatile.Read(ref _overallLevelDb);

    /// <summary>Центры стандартных третьоктавных полос, Гц.</summary>
    public static IReadOnlyList<double> StandardCenters => Centers;

    /// <inheritdoc />
    public string XLabel => "Центр полосы, Гц";

    /// <inheritdoc />
    public string YLabel => "Уровень, дБFS";

    /// <inheritdoc />
    public double[] X => _x;

    /// <inheritdoc />
    public double[] Y => _y;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowSize, sampleRate);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var centers = new double[ExactCenters.Length];
        Array.Copy(ExactCenters, centers, ExactCenters.Length);

        var levels = new double[ExactCenters.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            levels[i] = MinLevelDb;
        }

        if (samples.Length == 0)
        {
            _x = centers;
            _y = levels;
            Publish(0.0, MinLevelDb, MinLevelDb);
            MarkUpdated();
            return;
        }

        var result = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
        var frequencies = result.Frequencies;
        var magnitudes = result.Magnitudes;

        var nyquist = sampleRate / 2.0;
        var overallPower = 0.0;

        for (var k = 1; k < magnitudes.Length; k++)
        {
            overallPower += magnitudes[k] * magnitudes[k];
        }

        for (var band = 0; band < ExactCenters.Length; band++)
        {
            var center = ExactCenters[band];
            var edgeRatio = Math.Pow(10.0, 1.0 / 20.0);
            var lower = center / edgeRatio;
            var upper = center * edgeRatio;

            if (lower > nyquist)
            {
                levels[band] = MinLevelDb;
                continue;
            }

            upper = Math.Min(upper, nyquist);

            var power = 0.0;
            for (var k = 1; k < magnitudes.Length; k++)
            {
                var frequency = frequencies[k];
                if (frequency < lower || frequency > upper)
                {
                    continue;
                }

                power += magnitudes[k] * magnitudes[k];
            }

            levels[band] = PowerToDb(power);
        }

        var dominantIndex = 0;
        for (var i = 1; i < levels.Length; i++)
        {
            if (levels[i] > levels[dominantIndex])
            {
                dominantIndex = i;
            }
        }

        _x = centers;
        _y = levels;

        Publish(
            ExactCenters[dominantIndex],
            levels[dominantIndex],
            PowerToDb(overallPower));
        MarkUpdated();
    }

    private static double PowerToDb(double power)
    {
        var rms = Math.Sqrt(Math.Max(power, 0.0) / (2.0 * HannEnbw));
        return SignalMath.DbFs(rms);
    }

    private void Publish(double dominantCenterHz, double dominantLevelDb, double overallLevelDb)
    {
        Volatile.Write(ref _dominantCenterHz, dominantCenterHz);
        Volatile.Write(ref _dominantLevelDb, dominantLevelDb);
        Volatile.Write(ref _overallLevelDb, overallLevelDb);

        _readouts = new[]
        {
            new AnalyzerReadout("Доминирующая полоса", dominantCenterHz, "Гц"),
            new AnalyzerReadout("Уровень полосы", dominantLevelDb, "дБFS (СКЗ)"),
            new AnalyzerReadout("Общий уровень", overallLevelDb, "дБFS (СКЗ)")
        };
    }
}
