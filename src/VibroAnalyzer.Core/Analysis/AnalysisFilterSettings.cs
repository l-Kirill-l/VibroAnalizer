using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis;

/// <summary>
/// Отключаемые фильтры анализатора: полосовой фильтр и режекция сетевых
/// частот. Настройки применяются к копии входного окна, исходный массив
/// не изменяется.
/// </summary>
public sealed class AnalysisFilterSettings
{
    /// <summary>Включить ФВЧ/ФНЧ (полосовой фильтр).</summary>
    public bool BandPassEnabled { get; set; } = true;

    /// <summary>Частота среза ФВЧ, Гц. 0 — не применять.</summary>
    public double HighPassHz { get; set; }

    /// <summary>Частота среза ФНЧ, Гц. 0 — не применять.</summary>
    public double LowPassHz { get; set; }

    /// <summary>Включить режекцию сетевых частот.</summary>
    public bool MainsNotchEnabled { get; set; } = true;

    /// <summary>Режекция 50 Гц.</summary>
    public bool Notch50Enabled { get; set; } = true;

    /// <summary>Режекция 60 Гц.</summary>
    public bool Notch60Enabled { get; set; } = true;

    /// <summary>
    /// Применяет включённые фильтры. Если фильтры выключены, возвращает
    /// исходный массив без копирования.
    /// </summary>
    public double[] Apply(double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Length == 0 || sampleRate <= 0)
        {
            return samples;
        }

        var nyquist = sampleRate * 0.5;
        var highPassHz = BandPassEnabled ? HighPassHz : 0.0;
        var lowPassHz = BandPassEnabled ? LowPassHz : 0.0;
        var bandValid = highPassHz > 0.0
                        && lowPassHz > highPassHz
                        && highPassHz < nyquist * 0.99;
        var highPass = bandValid
            ? BiquadFilter.CreateHighPass(sampleRate, highPassHz)
            : null;
        var lowPass = bandValid
            ? BiquadFilter.CreateLowPass(sampleRate, Math.Min(lowPassHz, nyquist * 0.99))
            : null;

        var notches = new List<BiquadFilter>();
        if (MainsNotchEnabled)
        {
            if (Notch50Enabled && 50.0 < nyquist * 0.99)
            {
                notches.Add(BiquadFilter.CreateNotch(sampleRate, 50.0));
            }

            if (Notch60Enabled && 60.0 < nyquist * 0.99)
            {
                notches.Add(BiquadFilter.CreateNotch(sampleRate, 60.0));
            }
        }

        if (highPass is null && lowPass is null && notches.Count == 0)
        {
            return samples;
        }

        var filtered = new double[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            var value = samples[index];
            if (highPass is not null)
            {
                value = highPass.Process(value);
            }

            if (lowPass is not null)
            {
                value = lowPass.Process(value);
            }

            foreach (var notch in notches)
            {
                value = notch.Process(value);
            }

            filtered[index] = value;
        }

        return filtered;
    }
}
