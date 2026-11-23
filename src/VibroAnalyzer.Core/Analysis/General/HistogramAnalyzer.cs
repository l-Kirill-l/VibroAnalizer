using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.General;

/// <summary>
/// Строит гистограмму распределения мгновенных значений сигнала.
/// </summary>
public sealed class HistogramAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 8192;
    private const int BinCount = 128;
    private const double Epsilon = 1e-15;

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _mean;
    private double _standardDeviation;
    private double _minimum;
    private double _maximum;
    private double _median;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Гистограмма амплитуд";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Общие измерения";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Раскладывает мгновенные значения сигнала по 128 интервалам от минимума до максимума "
        + "и показывает относительную частоту попадания в каждый интервал. По форме гистограммы "
        + "легко отличить синус, шум и импульсный процесс.";

    /// <summary>Среднее значение отсчётов.</summary>
    public double Mean => Volatile.Read(ref _mean);

    /// <summary>Среднеквадратическое отклонение.</summary>
    public double StandardDeviation => Volatile.Read(ref _standardDeviation);

    /// <summary>Минимальное значение.</summary>
    public double Minimum => Volatile.Read(ref _minimum);

    /// <summary>Максимальное значение.</summary>
    public double Maximum => Volatile.Read(ref _maximum);

    /// <summary>Медиана.</summary>
    public double Median => Volatile.Read(ref _median);

    /// <summary>Число интервалов гистограммы.</summary>
    public int Bins => BinCount;

    /// <inheritdoc />
    public string XLabel => "Амплитуда";

    /// <inheritdoc />
    public string YLabel => "Относительная частота";

    /// <inheritdoc />
    public double[] X => _x;

    /// <inheritdoc />
    public double[] Y => _y;

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
            _x = Array.Empty<double>();
            _y = Array.Empty<double>();
            PublishReadouts(0.0, 0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var mean = SignalMath.Mean(samples);
        var minimum = samples[0];
        var maximum = samples[0];

        for (var i = 1; i < count; i++)
        {
            if (samples[i] < minimum)
            {
                minimum = samples[i];
            }

            if (samples[i] > maximum)
            {
                maximum = samples[i];
            }
        }

        var variance = 0.0;
        for (var i = 0; i < count; i++)
        {
            var deviation = samples[i] - mean;
            variance += deviation * deviation;
        }

        variance /= count;

        var sorted = new double[count];
        Array.Copy(samples, sorted, count);
        Array.Sort(sorted);
        var median = count % 2 == 0
            ? 0.5 * (sorted[count / 2 - 1] + sorted[count / 2])
            : sorted[count / 2];

        var x = new double[BinCount];
        var y = new double[BinCount];
        var counts = new int[BinCount];

        var range = maximum - minimum;
        if (range > Epsilon)
        {
            for (var i = 0; i < count; i++)
            {
                var normalized = (samples[i] - minimum) / range;
                var index = Math.Clamp((int)(normalized * BinCount), 0, BinCount - 1);
                counts[index]++;
            }

            for (var i = 0; i < BinCount; i++)
            {
                x[i] = minimum + (i + 0.5) * range / BinCount;
                y[i] = (double)counts[i] / count;
            }
        }
        else
        {
            // Постоянный сигнал: вся масса попадает в средний интервал.
            var middle = BinCount / 2;
            counts[middle] = count;

            for (var i = 0; i < BinCount; i++)
            {
                x[i] = minimum;
                y[i] = (double)counts[i] / count;
            }
        }

        _x = x;
        _y = y;

        PublishReadouts(mean, Math.Sqrt(variance), minimum, maximum, median);
        MarkUpdated();
    }

    private void PublishReadouts(
        double mean,
        double standardDeviation,
        double minimum,
        double maximum,
        double median)
    {
        Volatile.Write(ref _mean, mean);
        Volatile.Write(ref _standardDeviation, standardDeviation);
        Volatile.Write(ref _minimum, minimum);
        Volatile.Write(ref _maximum, maximum);
        Volatile.Write(ref _median, median);

        _readouts = new[]
        {
            new AnalyzerReadout("Среднее", mean, "ед."),
            new AnalyzerReadout("СКО", standardDeviation, "ед."),
            new AnalyzerReadout("Минимум", minimum, "ед."),
            new AnalyzerReadout("Максимум", maximum, "ед."),
            new AnalyzerReadout("Медиана", median, "ед.")
        };
    }
}
