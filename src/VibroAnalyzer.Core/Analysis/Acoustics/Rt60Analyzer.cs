using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Acoustics;

/// <summary>
/// Оценка времени реверберации RT60 по спаду огибающей сигнала.
/// </summary>
public sealed class Rt60Analyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 16384;
    private const double MinLevelDb = -120.0;

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _rt60Seconds;
    private double _slopeDbPerSecond;
    private double _reliability;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Время реверберации RT60";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Акустика";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Строит кривую спада энергии Schroeder (обратное интегрирование квадрата импульсного отклика, "
        + "ISO 3382) и оценивает RT60 по наклону в диапазоне −5…−35 дБ (T30; при нехватке диапазона — T20). "
        + "Предназначен для импульсных измерений помещения, а не для произвольного непрерывного сигнала.";

    /// <summary>Оценка RT60, с; 0 — спад не найден.</summary>
    public double Rt60Seconds => Volatile.Read(ref _rt60Seconds);

    /// <summary>Наклон спада, дБ/с.</summary>
    public double SlopeDbPerSecond => Volatile.Read(ref _slopeDbPerSecond);

    /// <summary>Достоверность оценки 0…1.</summary>
    public double Reliability => Volatile.Read(ref _reliability);

    /// <inheritdoc />
    public string XLabel => "Время, с";

    /// <inheritdoc />
    public string YLabel => "Уровень, дБ";

    /// <inheritdoc />
    public double[] X => _x;

    /// <inheritdoc />
    public double[] Y => _y;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowSize, sampleRate * 10);

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
            Publish(0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var x = new double[count];
        var y = new double[count];
        var energy = new double[count];
        var cumulative = 0.0;

        var mean = SignalMath.Mean(samples);
        var noisePower = EstimateNoisePower(samples, mean);

        // Schroeder EDC: E(t) = ∫_t^∞ h²(τ)dτ. Интегрируем справа налево.
        // Постоянную составляющую удаляем, а оценку шумового пола вычитаем,
        // чтобы хвост шума не «удлинял» реверберацию.
        for (var i = count - 1; i >= 0; i--)
        {
            var value = samples[i] - mean;
            var sampleEnergy = Math.Max(value * value - noisePower, 0.0);
            cumulative += sampleEnergy;
            energy[i] = cumulative;
            x[i] = (double)i / sampleRate;
        }

        var total = energy[0];

        if (total <= 1e-30)
        {
            Array.Fill(y, MinLevelDb);
            _x = x;
            _y = y;
            Publish(0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        for (var i = 0; i < count; i++)
        {
            y[i] = Math.Max(10.0 * Math.Log10(Math.Max(energy[i] / total, 1e-12)), MinLevelDb);
        }

        var index5 = -1;
        var index25 = -1;
        var index35 = -1;

        for (var i = 0; i < count; i++)
        {
            if (index5 < 0 && y[i] <= -5.0)
            {
                index5 = i;
            }

            if (index5 >= 0 && index25 < 0 && y[i] <= -25.0)
            {
                index25 = i;
            }

            if (index5 >= 0 && y[i] <= -35.0)
            {
                index35 = i;
                break;
            }
        }

        var rt60 = 0.0;
        var slope = 0.0;
        var reliability = 0.0;
        var endIndex = index35 >= 0 ? index35 : index25;

        if (index5 >= 0 && endIndex > index5)
        {
            var (regressionSlope, rSquared) = LinearRegression(x, y, index5, endIndex);

            if (regressionSlope < -1e-9)
            {
                slope = regressionSlope;
                rt60 = Math.Clamp(-60.0 / slope, 0.0, 60.0);
                reliability = Math.Clamp(rSquared, 0.0, 1.0);

                // Если диапазон −35 дБ не достигнут, это T20 — оценка менее надёжна.
                if (index35 < 0)
                {
                    reliability *= 0.7;
                }

                if (!double.IsFinite(rt60))
                {
                    rt60 = 0.0;
                    reliability = 0.0;
                }
            }
        }

        _x = x;
        _y = y;

        Publish(rt60, slope, reliability);
        MarkUpdated();
    }

    private static double EstimateNoisePower(double[] samples, double mean)
    {
        var count = samples.Length;
        if (count < 8)
        {
            return 0.0;
        }

        var tailCount = Math.Max(4, count / 10);
        var sum = 0.0;

        for (var i = count - tailCount; i < count; i++)
        {
            var value = samples[i] - mean;
            sum += value * value;
        }

        return sum / tailCount;
    }

    private static (double Slope, double RSquared) LinearRegression(        double[] x,
        double[] y,
        int start,
        int end)
    {
        var count = end - start + 1;
        if (count < 2)
        {
            return (0.0, 0.0);
        }

        var meanX = 0.0;
        var meanY = 0.0;

        for (var i = start; i <= end; i++)
        {
            meanX += x[i];
            meanY += y[i];
        }

        meanX /= count;
        meanY /= count;

        var covariance = 0.0;
        var varianceX = 0.0;
        var varianceY = 0.0;

        for (var i = start; i <= end; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        if (varianceX <= 1e-24)
        {
            return (0.0, 0.0);
        }

        var slope = covariance / varianceX;
        var rSquared = varianceY > 1e-24
            ? covariance * covariance / (varianceX * varianceY)
            : 0.0;

        return (slope, rSquared);
    }

    private void Publish(double rt60Seconds, double slopeDbPerSecond, double reliability)
    {
        Volatile.Write(ref _rt60Seconds, rt60Seconds);
        Volatile.Write(ref _slopeDbPerSecond, slopeDbPerSecond);
        Volatile.Write(ref _reliability, reliability);

        _readouts = new[]
        {
            new AnalyzerReadout("RT60", rt60Seconds, "с"),
            new AnalyzerReadout("Наклон", slopeDbPerSecond, "дБ/с"),
            new AnalyzerReadout("Достоверность", reliability, string.Empty)
        };
    }
}
