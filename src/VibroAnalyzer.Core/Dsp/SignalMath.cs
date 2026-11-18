namespace VibroAnalyzer.Core.Dsp;

/// <summary>
/// Базовые измерения сигнала во временной области.
/// </summary>
public static class SignalMath
{
    private const double MinAmplitude = 1e-12;
    private const double HysteresisRatio = 0.1;

    /// <summary>Среднее значение.</summary>
    public static double Mean(ReadOnlySpan<double> samples)
    {
        if (samples.Length == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += sample;
        }

        return sum / samples.Length;
    }

    /// <summary>Среднеквадратичное значение.</summary>
    public static double Rms(ReadOnlySpan<double> samples)
    {
        if (samples.Length == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Максимальное абсолютное значение.</summary>
    public static double Peak(ReadOnlySpan<double> samples)
    {
        var peak = 0.0;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak;
    }

    /// <summary>Размах (максимум минус минимум).</summary>
    public static double PeakToPeak(ReadOnlySpan<double> samples)
    {
        if (samples.Length == 0)
        {
            return 0.0;
        }

        var min = samples[0];
        var max = samples[0];

        for (var i = 1; i < samples.Length; i++)
        {
            var sample = samples[i];
            if (sample < min)
            {
                min = sample;
            }

            if (sample > max)
            {
                max = sample;
            }
        }

        return max - min;
    }

    /// <summary>
    /// Оценка частоты по переходам через ноль с гистерезисом 10 % от пика.
    /// Время перехода уточняется линейной интерполяцией.
    /// </summary>
    public static double EstimateFrequencyHz(ReadOnlySpan<double> samples, int sampleRate)
    {
        if (sampleRate <= 0 || samples.Length < 2)
        {
            return 0.0;
        }

        var peak = Peak(samples);
        if (peak <= 0.0)
        {
            return 0.0;
        }

        var threshold = peak * HysteresisRatio;
        var state = 0;
        var crossings = 0;
        var firstCrossing = 0.0;
        var lastCrossing = 0.0;

        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];

            if (state == 0)
            {
                if (value >= threshold)
                {
                    state = 1;
                }
                else if (value <= -threshold)
                {
                    state = -1;
                }

                continue;
            }

            if (state > 0 && value <= -threshold)
            {
                state = -1;
                RegisterCrossing(FindCrossing(samples, i), ref crossings, ref firstCrossing, ref lastCrossing);
            }
            else if (state < 0 && value >= threshold)
            {
                state = 1;
                RegisterCrossing(FindCrossing(samples, i), ref crossings, ref firstCrossing, ref lastCrossing);
            }
        }

        if (crossings < 2)
        {
            return 0.0;
        }

        var span = lastCrossing - firstCrossing;
        if (span <= 0.0)
        {
            return 0.0;
        }

        // Между соседними переходами через ноль проходит половина периода.
        return (crossings - 1) * sampleRate / (2.0 * span);
    }

    /// <summary>Амплитуда в дБ относительно полной шкалы.</summary>
    public static double DbFs(double amplitude)
    {
        return 20.0 * Math.Log10(Math.Max(Math.Abs(amplitude), MinAmplitude));
    }

    /// <summary>Амплитуда из дБ относительно полной шкалы.</summary>
    public static double FromDbFs(double db)
    {
        return Math.Pow(10.0, db / 20.0);
    }

    /// <summary>Вычитает постоянную составляющую из буфера на месте.</summary>
    public static void RemoveDcInPlace(Span<double> samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        var mean = Mean(samples);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] -= mean;
        }
    }

    private static void RegisterCrossing(
        double position,
        ref int crossings,
        ref double firstCrossing,
        ref double lastCrossing)
    {
        if (crossings == 0)
        {
            firstCrossing = position;
        }
        else
        {
            lastCrossing = position;
        }

        crossings++;
    }

    /// <summary>
    /// Находит момент перехода через ноль около индекса срабатывания гистерезиса.
    /// Идёт назад до смены знака и линейно интерполирует положение нуля.
    /// </summary>
    private static double FindCrossing(ReadOnlySpan<double> samples, int index)
    {
        var j = index;

        while (j > 0 && Math.Sign(samples[j]) == Math.Sign(samples[j - 1]))
        {
            j--;
        }

        if (j <= 0)
        {
            return index;
        }

        var previous = samples[j - 1];
        var current = samples[j];
        var denominator = current - previous;

        if (Math.Abs(denominator) < 1e-15)
        {
            return j;
        }

        var fraction = -previous / denominator;
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        return j - 1 + fraction;
    }
}
