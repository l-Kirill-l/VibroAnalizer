namespace VibroAnalyzer.Core.Dsp;

/// <summary>
/// БИХ-фильтр второго порядка (RBJ). Реализация в прямой форме II.
/// </summary>
public sealed class BiquadFilter
{
    private const double MinQ = 1e-3;
    private const double MaxQ = 1e3;

    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;

    private double _z1;
    private double _z2;

    private BiquadFilter(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    /// <summary>Фильтр нижних частот (RBJ low-pass).</summary>
    public static BiquadFilter CreateLowPass(int sampleRate, double cutoffHz, double q = 0.70710678)
    {
        Prepare(sampleRate, cutoffHz, out var cosW, out var sinW);
        var alpha = sinW / (2.0 * ClampQ(q));

        var b0 = (1.0 - cosW) / 2.0;
        var b1 = 1.0 - cosW;
        var b2 = (1.0 - cosW) / 2.0;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cosW;
        var a2 = 1.0 - alpha;

        return new BiquadFilter(b0, b1, b2, a0, a1, a2);
    }

    /// <summary>Фильтр верхних частот (RBJ high-pass).</summary>
    public static BiquadFilter CreateHighPass(int sampleRate, double cutoffHz, double q = 0.70710678)
    {
        Prepare(sampleRate, cutoffHz, out var cosW, out var sinW);
        var alpha = sinW / (2.0 * ClampQ(q));

        var b0 = (1.0 + cosW) / 2.0;
        var b1 = -(1.0 + cosW);
        var b2 = (1.0 + cosW) / 2.0;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cosW;
        var a2 = 1.0 - alpha;

        return new BiquadFilter(b0, b1, b2, a0, a1, a2);
    }

    /// <summary>Режекторный фильтр (RBJ notch).</summary>
    public static BiquadFilter CreateNotch(int sampleRate, double frequencyHz, double q = 30.0)
    {
        Prepare(sampleRate, frequencyHz, out var cosW, out var sinW);
        var alpha = sinW / (2.0 * ClampQ(q));

        var b0 = 1.0;
        var b1 = -2.0 * cosW;
        var b2 = 1.0;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cosW;
        var a2 = 1.0 - alpha;

        return new BiquadFilter(b0, b1, b2, a0, a1, a2);
    }

    /// <summary>Обрабатывает один отсчёт.</summary>
    public double Process(double sample)
    {
        var output = _b0 * sample + _z1;
        _z1 = _b1 * sample - _a1 * output + _z2;
        _z2 = _b2 * sample - _a2 * output;
        return output;
    }

    /// <summary>Сбрасывает внутреннее состояние фильтра.</summary>
    public void Reset()
    {
        _z1 = 0.0;
        _z2 = 0.0;
    }

    private static void Prepare(int sampleRate, double frequencyHz, out double cosW, out double sinW)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var clamped = ClampFrequency(frequencyHz, sampleRate);
        var w0 = 2.0 * Math.PI * clamped / sampleRate;
        cosW = Math.Cos(w0);
        sinW = Math.Sin(w0);
    }

    private static double ClampFrequency(double frequencyHz, int sampleRate)
    {
        var max = sampleRate * 0.499;
        var min = Math.Min(1e-3, max);

        if (double.IsNaN(frequencyHz))
        {
            return min;
        }

        return Math.Clamp(frequencyHz, min, max);
    }

    private static double ClampQ(double q)
    {
        if (double.IsNaN(q))
        {
            return MinQ;
        }

        return Math.Clamp(q, MinQ, MaxQ);
    }
}
