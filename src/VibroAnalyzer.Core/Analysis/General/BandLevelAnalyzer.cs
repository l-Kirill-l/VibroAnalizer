using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.General;

/// <summary>
/// Измеряет уровень сигнала в заданной полосе частот по спектру БПФ.
/// </summary>
public sealed class BandLevelAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized
{
    private const int WindowSize = 8192;
    private const double MinFrequencyHz = 1.0;
    private const double MaxFrequencyHz = 20000.0;
    private const double MinLevelDb = -240.0;

    // Эквивалентная шумовая полоса окна Ханна: сумма квадратов амплитуд
    // бинов даёт мощность, завышенную в ENBW раз.
    private const double HannEnbw = 1.5;

    private readonly AnalyzerParameter[] _parameters;

    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _lowHz = 20.0;
    private double _highHz = 20000.0;
    private double _bandRms;
    private double _bandLevelDb;
    private double _bandPeak;

    public BandLevelAnalyzer()
    {
        _parameters = new[]
        {
            new AnalyzerParameter(
                "Нижняя частота",
                MinFrequencyHz,
                MaxFrequencyHz,
                () => LowHz,
                value => LowHz = value,
                "Гц"),
            new AnalyzerParameter(
                "Верхняя частота",
                MinFrequencyHz,
                MaxFrequencyHz,
                () => HighHz,
                value => HighHz = value,
                "Гц")
        };
    }

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Уровень в полосе";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Общие измерения";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Суммирует мощность спектральных бинов в выбранной полосе и пересчитывает её обратно "
        + "в СКЗ и уровень дБFS. Позволяет следить за уровнем в узкой полосе, например за оборотной "
        + "частотой или полосой шума, не учитывая остальной спектр.";

    /// <summary>Нижняя граница полосы, Гц.</summary>
    public double LowHz
    {
        get => Volatile.Read(ref _lowHz);
        set => Volatile.Write(ref _lowHz, Math.Clamp(value, MinFrequencyHz, MaxFrequencyHz));
    }

    /// <summary>Верхняя граница полосы, Гц.</summary>
    public double HighHz
    {
        get => Volatile.Read(ref _highHz);
        set => Volatile.Write(ref _highHz, Math.Clamp(value, MinFrequencyHz, MaxFrequencyHz));
    }

    /// <summary>СКЗ сигнала в полосе.</summary>
    public double BandRms => Volatile.Read(ref _bandRms);

    /// <summary>Уровень сигнала в полосе, дБFS.</summary>
    public double BandLevelDb => Volatile.Read(ref _bandLevelDb);

    /// <summary>Максимальная амплитуда бина в полосе.</summary>
    public double BandPeak => Volatile.Read(ref _bandPeak);

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => _parameters;

    /// <inheritdoc />
    public string XLabel => "Частота, Гц";

    /// <inheritdoc />
    public string YLabel => "Уровень, дБFS";

    /// <inheritdoc />
    public double[] X => Array.Empty<double>();

    /// <inheritdoc />
    public double[] Y => Array.Empty<double>();

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate) => WindowSize;

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var low = Math.Min(LowHz, HighHz);
        var high = Math.Max(LowHz, HighHz);
        var nyquist = sampleRate / 2.0;

        high = Math.Min(high, nyquist);
        low = Math.Max(0.0, Math.Min(low, high));

        if (samples.Length == 0 || high <= low)
        {
            Publish(0.0, MinLevelDb, 0.0);
            MarkUpdated();
            return;
        }

        var result = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
        var frequencies = result.Frequencies;
        var magnitudes = result.Magnitudes;
        var binWidth = (double)sampleRate / result.FftSize;

        var actualStart = Math.Max(1, (int)Math.Floor(low / binWidth));
        var actualEnd = Math.Min(magnitudes.Length - 1, (int)Math.Ceiling(high / binWidth));

        if (actualEnd < actualStart)
        {
            // Узкая полоса может не содержать ни одного центра бина:
            // расширяем её на главный лепесток окна Ханна (±2 бина).
            actualStart = Math.Max(1, (int)Math.Round((low + high) * 0.5 / binWidth));
            actualEnd = actualStart;
        }

        var startBin = Math.Max(1, actualStart - 2);
        var endBin = Math.Min(magnitudes.Length - 1, actualEnd + 2);

        var powerSum = 0.0;
        var peak = 0.0;

        for (var k = startBin; k <= endBin; k++)
        {
            var magnitude = magnitudes[k];
            powerSum += magnitude * magnitude;
        }

        for (var k = actualStart; k <= actualEnd; k++)
        {
            var magnitude = magnitudes[k];
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        // Для синусоиды мощность бина равна A²/2; окно Ханна расширяет её в ENBW раз.
        var rms = Math.Sqrt(powerSum / (2.0 * HannEnbw));
        var levelDb = SignalMath.DbFs(rms);

        Publish(rms, levelDb, peak);
        MarkUpdated();
    }

    private void Publish(double bandRms, double bandLevelDb, double bandPeak)
    {
        Volatile.Write(ref _bandRms, bandRms);
        Volatile.Write(ref _bandLevelDb, bandLevelDb);
        Volatile.Write(ref _bandPeak, bandPeak);

        _readouts = new[]
        {
            new AnalyzerReadout("СКЗ полосы", bandRms, "ед."),
            new AnalyzerReadout("Уровень", bandLevelDb, "дБFS (СКЗ)"),
            new AnalyzerReadout("Пик", bandPeak, "ед.")
        };
    }
}
