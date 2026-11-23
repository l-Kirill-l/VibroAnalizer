using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.General;

/// <summary>
/// Показывает медленный тренд сигнала через скользящее среднее и оценивает дрейф.
/// </summary>
public sealed class TrendAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 8192;

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _slopePerSecond;
    private double _drift;
    private double _start;
    private double _end;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Тренд и дрейф";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Общие измерения";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Сглаживает сигнал скользящим средним и показывает медленное изменение уровня во времени. "
        + "По наклону и величине дрейфа удобно контролировать уход нуля, нагрев и постепенные "
        + "изменения измеряемой величины.";

    /// <summary>Длительность окна скользящего среднего, с.</summary>
    public double MovingAverageSeconds { get; set; } = 0.1;

    /// <summary>Наклон тренда, единиц в секунду.</summary>
    public double SlopePerSecond => Volatile.Read(ref _slopePerSecond);

    /// <summary>Изменение уровня за окно, единиц.</summary>
    public double Drift => Volatile.Read(ref _drift);

    /// <summary>Уровень в начале окна.</summary>
    public double StartValue => Volatile.Read(ref _start);

    /// <summary>Уровень в конце окна.</summary>
    public double EndValue => Volatile.Read(ref _end);

    /// <inheritdoc />
    public string XLabel => "Время, с";

    /// <inheritdoc />
    public string YLabel => "Скользящее среднее";

    /// <inheritdoc />
    public double[] X => _x;

    /// <inheritdoc />
    public double[] Y => _y;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowSize, sampleRate * 2);

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
            Publish(0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var averageSeconds = MovingAverageSeconds > 0 ? MovingAverageSeconds : 0.1;
        var averageLength = Math.Max(1, (int)Math.Round(averageSeconds * sampleRate));

        var x = new double[count];
        var y = new double[count];
        var prefix = new double[count + 1];

        for (var i = 0; i < count; i++)
        {
            prefix[i + 1] = prefix[i] + samples[i];
            x[i] = (double)i / sampleRate;
        }

        for (var i = 0; i < count; i++)
        {
            var from = Math.Max(0, i - averageLength + 1);
            var length = i - from + 1;
            y[i] = (prefix[i + 1] - prefix[from]) / length;
        }

        var validStart = Math.Min(Math.Max(0, averageLength - 1), count - 1);
        var start = y[validStart];
        var end = y[count - 1];
        var drift = end - start;
        var slope = count > validStart + 1
            ? CalculateSlope(x, y, validStart)
            : 0.0;

        _x = x;
        _y = y;

        Publish(slope, drift, start, end);
        MarkUpdated();
    }

    private static double CalculateSlope(double[] x, double[] y, int startIndex)
    {
        var count = x.Length;

        if (count - startIndex < 2)
        {
            return 0.0;
        }

        var meanX = 0.0;
        var meanY = 0.0;

        for (var i = startIndex; i < count; i++)
        {
            meanX += x[i];
            meanY += y[i];
        }

        var length = count - startIndex;
        meanX /= length;
        meanY /= length;

        var covariance = 0.0;
        var varianceX = 0.0;

        for (var i = startIndex; i < count; i++)
        {
            var dx = x[i] - meanX;
            covariance += dx * (y[i] - meanY);
            varianceX += dx * dx;
        }

        return varianceX > 1e-24 ? covariance / varianceX : 0.0;
    }

    private void Publish(double slopePerSecond, double drift, double start, double end)
    {
        Volatile.Write(ref _slopePerSecond, slopePerSecond);
        Volatile.Write(ref _drift, drift);
        Volatile.Write(ref _start, start);
        Volatile.Write(ref _end, end);

        _readouts = new[]
        {
            new AnalyzerReadout("Наклон", slopePerSecond, "ед./с"),
            new AnalyzerReadout("Дрейф", drift, "ед."),
            new AnalyzerReadout("Начало", start, "ед."),
            new AnalyzerReadout("Конец", end, "ед.")
        };
    }
}
