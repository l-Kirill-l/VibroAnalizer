using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.General;

/// <summary>
/// Измеряет уровень сигнала и его статистические моменты: СКЗ, пик, размах,
/// постоянную составляющую, крест-фактор, эксцесс и асимметрию.
/// </summary>
public sealed class LevelStatisticsAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 4096;
    private const double Epsilon = 1e-24;

    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _rms;
    private double _peak;
    private double _peakToPeak;
    private double _dcOffset;
    private double _crestFactor;
    private double _kurtosis;
    private double _excessKurtosis;
    private double _skewness;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Уровень и статистика";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Общие измерения";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Считает основные параметры уровня и форму распределения отсчётов: СКЗ, пик, размах, "
        + "постоянную составляющую, крест-фактор, эксцесс и асимметрию. Помогает быстро оценить "
        + "громкость, перегрузку и характер сигнала (синус, шум, импульсы).";

    /// <summary>Среднеквадратическое значение.</summary>
    public double Rms => Volatile.Read(ref _rms);

    /// <summary>Максимальное абсолютное значение.</summary>
    public double Peak => Volatile.Read(ref _peak);

    /// <summary>Размах сигнала (максимум минус минимум).</summary>
    public double PeakToPeak => Volatile.Read(ref _peakToPeak);

    /// <summary>Постоянная составляющая.</summary>
    public double DcOffset => Volatile.Read(ref _dcOffset);

    /// <summary>Отношение пика к СКЗ.</summary>
    public double CrestFactor => Volatile.Read(ref _crestFactor);

    /// <summary>Эксцесс распределения отсчётов.</summary>
    public double Kurtosis => Volatile.Read(ref _kurtosis);

    /// <summary>Избыточный эксцесс (Kurtosis − 3).</summary>
    public double ExcessKurtosis => Volatile.Read(ref _excessKurtosis);

    /// <summary>Асимметрия распределения отсчётов.</summary>
    public double Skewness => Volatile.Read(ref _skewness);

    /// <inheritdoc />
    public string XLabel => "Время";

    /// <inheritdoc />
    public string YLabel => "Уровень";

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

        var count = samples.Length;
        if (count == 0)
        {
            Publish(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var mean = SignalMath.Mean(samples);
        var rms = SignalMath.Rms(samples);
        var peak = SignalMath.Peak(samples);
        var peakToPeak = SignalMath.PeakToPeak(samples);

        var sum2 = 0.0;
        var sum3 = 0.0;
        var sum4 = 0.0;

        for (var i = 0; i < count; i++)
        {
            var deviation = samples[i] - mean;
            var square = deviation * deviation;
            sum2 += square;
            sum3 += square * deviation;
            sum4 += square * square;
        }

        var variance = sum2 / count;
        var skewness = 0.0;
        var kurtosis = 0.0;

        if (variance > Epsilon)
        {
            var deviationRms = Math.Sqrt(variance);
            skewness = sum3 / count / (variance * deviationRms);
            kurtosis = sum4 / count / (variance * variance);
        }

        var crestFactor = rms > Epsilon ? peak / rms : 0.0;
        var excessKurtosis = variance > Epsilon ? kurtosis - 3.0 : 0.0;

        if (!double.IsFinite(skewness))
        {
            skewness = 0.0;
        }

        if (!double.IsFinite(kurtosis))
        {
            kurtosis = 0.0;
        }

        if (!double.IsFinite(excessKurtosis))
        {
            excessKurtosis = 0.0;
        }

        Publish(rms, peak, peakToPeak, mean, crestFactor, kurtosis, excessKurtosis, skewness);
        MarkUpdated();
    }

    private void Publish(
        double rms,
        double peak,
        double peakToPeak,
        double dcOffset,
        double crestFactor,
        double kurtosis,
        double excessKurtosis,
        double skewness)
    {
        Volatile.Write(ref _rms, rms);
        Volatile.Write(ref _peak, peak);
        Volatile.Write(ref _peakToPeak, peakToPeak);
        Volatile.Write(ref _dcOffset, dcOffset);
        Volatile.Write(ref _crestFactor, crestFactor);
        Volatile.Write(ref _kurtosis, kurtosis);
        Volatile.Write(ref _excessKurtosis, excessKurtosis);
        Volatile.Write(ref _skewness, skewness);

        _readouts = new[]
        {
            new AnalyzerReadout("СКЗ", rms, "ед."),
            new AnalyzerReadout("Пик", peak, "ед."),
            new AnalyzerReadout("Размах", peakToPeak, "ед."),
            new AnalyzerReadout("Постоянная", dcOffset, "ед."),
            new AnalyzerReadout("Крест-фактор", crestFactor, string.Empty),
            new AnalyzerReadout("Куртозис", kurtosis, string.Empty),
            new AnalyzerReadout("Эксцесс", excessKurtosis, string.Empty),
            new AnalyzerReadout("Асимметрия", skewness, string.Empty)
        };
    }
}
