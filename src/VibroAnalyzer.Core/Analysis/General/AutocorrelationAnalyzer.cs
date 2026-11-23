using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.General;

/// <summary>
/// Нормированная автокорреляция сигнала, вычисленная через БПФ.
/// </summary>
public sealed class AutocorrelationAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 8192;
    private const double Epsilon = 1e-24;

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _periodSeconds;
    private double _frequencyHz;
    private double _firstMaximum;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Автокорреляция";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Общие измерения";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Показывает, насколько сигнал похож сам на себя при сдвиге во времени. Первый максимум "
        + "после нулевой задержки даёт основной период и частоту, а форма кривой помогает выявить "
        + "периодические составляющие на фоне шума.";

    /// <summary>Основной период, с.</summary>
    public double PeriodSeconds => Volatile.Read(ref _periodSeconds);

    /// <summary>Основная частота, Гц.</summary>
    public double FrequencyHz => Volatile.Read(ref _frequencyHz);

    /// <summary>Значение коэффициента в первом максимуме после нуля.</summary>
    public double FirstMaximum => Volatile.Read(ref _firstMaximum);

    /// <inheritdoc />
    public string XLabel => "Задержка, с";

    /// <inheritdoc />
    public string YLabel => "Коэффициент";

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
        if (count < 4)
        {
            _x = Array.Empty<double>();
            _y = Array.Empty<double>();
            Publish(0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var mean = SignalMath.Mean(samples);
        var fftSize = NextPowerOfTwo(count * 2);
        var buffer = new Complex[fftSize];

        for (var i = 0; i < count; i++)
        {
            buffer[i] = new Complex(samples[i] - mean, 0.0);
        }

        Fourier.Forward(buffer, FourierOptions.NoScaling);

        for (var i = 0; i < buffer.Length; i++)
        {
            var magnitude = buffer[i].Magnitude;
            buffer[i] = new Complex(magnitude * magnitude, 0.0);
        }

        Fourier.Inverse(buffer, FourierOptions.NoScaling);

        var x = new double[count];
        var y = new double[count];

        var zeroLag = buffer[0].Real / fftSize;

        for (var lag = 0; lag < count; lag++)
        {
            var value = buffer[lag].Real / fftSize;
            x[lag] = (double)lag / sampleRate;
            y[lag] = zeroLag > Epsilon ? value / zeroLag : 0.0;
        }

        var (periodSeconds, frequencyHz, firstMaximum) = FindFirstMaximum(y, sampleRate);

        _x = x;
        _y = y;

        Publish(periodSeconds, frequencyHz, firstMaximum);
        MarkUpdated();
    }

    private static (double PeriodSeconds, double FrequencyHz, double FirstMaximum) FindFirstMaximum(
        double[] coefficients,
        int sampleRate)
    {
        if (coefficients.Length < 4)
        {
            return (0.0, 0.0, 0.0);
        }

        // Ищем первый локальный максимум после первого перехода автокорреляции через ноль.
        var searchStart = 1;
        for (var i = 1; i < coefficients.Length; i++)
        {
            if (coefficients[i] <= 0.0)
            {
                searchStart = i;
                break;
            }
        }

        if (searchStart < 2)
        {
            searchStart = 2;
        }

        for (var i = searchStart + 1; i < coefficients.Length - 1; i++)
        {
            var value = coefficients[i];

            if (value <= 0.1)
            {
                continue;
            }

            if (value <= coefficients[i - 1] || value < coefficients[i + 1])
            {
                continue;
            }

            var lag = (double)i;

            // Параболическая интерполяция уточняет положение максимума.
            var y0 = coefficients[i - 1];
            var y1 = coefficients[i];
            var y2 = coefficients[i + 1];
            var denominator = y0 - 2.0 * y1 + y2;

            if (Math.Abs(denominator) > Epsilon && double.IsFinite(denominator))
            {
                var delta = 0.5 * (y0 - y2) / denominator;
                lag += Math.Clamp(delta, -0.5, 0.5);
            }

            if (lag <= 0.0)
            {
                continue;
            }

            var period = lag / sampleRate;
            return (period, 1.0 / period, y1);
        }

        return (0.0, 0.0, 0.0);
    }

    private void Publish(double periodSeconds, double frequencyHz, double firstMaximum)
    {
        Volatile.Write(ref _periodSeconds, periodSeconds);
        Volatile.Write(ref _frequencyHz, frequencyHz);
        Volatile.Write(ref _firstMaximum, firstMaximum);

        _readouts = new[]
        {
            new AnalyzerReadout("Основной период", periodSeconds, "с"),
            new AnalyzerReadout("Частота", frequencyHz, "Гц"),
            new AnalyzerReadout("Первый максимум", firstMaximum, string.Empty)
        };
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }
}
