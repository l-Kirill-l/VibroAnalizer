namespace VibroAnalyzer.Core.Modulation;

/// <summary>
/// Форма несущего колебания.
/// </summary>
public enum CarrierWaveform
{
    Sine,
    Square,
    Triangle
}

/// <summary>
/// Генератор несущего колебания с нормированной фазой 0..1.
/// </summary>
public sealed class CarrierGenerator
{
    private const double TwoPi = Math.PI * 2.0;
    private const double MinFrequencyHz = 1e-6;

    private double _frequencyHz = 1000.0;
    private double _amplitude = 1.0;
    private double _phase;

    /// <summary>
    /// Частота несущей в герцах. Значение всегда положительно.
    /// </summary>
    public double FrequencyHz
    {
        get => _frequencyHz;
        set => _frequencyHz = double.IsFinite(value) && value > MinFrequencyHz ? value : MinFrequencyHz;
    }

    /// <summary>
    /// Амплитуда несущей в диапазоне 0..1.
    /// </summary>
    public double Amplitude
    {
        get => _amplitude;
        set => _amplitude = double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Форма несущего колебания.
    /// </summary>
    public CarrierWaveform Waveform { get; set; } = CarrierWaveform.Sine;

    /// <summary>
    /// Текущая фаза, нормированная в диапазон 0..1 (1 эквивалентна 0).
    /// </summary>
    public double Phase
    {
        get => _phase;
        set => _phase = WrapPhase(value);
    }

    /// <summary>
    /// Сбрасывает фазу генератора в ноль.
    /// </summary>
    public void Reset()
    {
        _phase = 0.0;
    }

    /// <summary>
    /// Возвращает следующий отсчёт и продвигает фазу.
    /// </summary>
    public double NextSample(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var sample = RenderSample(_phase);
        _phase = WrapPhase(_phase + _frequencyHz / sampleRate);
        return sample;
    }

    /// <summary>
    /// Заполняет приёмный буфер отсчётами, последовательно продвигая фазу.
    /// </summary>
    public void Fill(Span<double> destination, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        if (destination.IsEmpty)
        {
            return;
        }

        var increment = _frequencyHz / sampleRate;
        var phase = _phase;

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = RenderSample(phase);
            phase = WrapPhase(phase + increment);
        }

        _phase = phase;
    }

    private double RenderSample(double phase)
    {
        var value = Waveform switch
        {
            CarrierWaveform.Square => phase < 0.5 ? 1.0 : -1.0,
            CarrierWaveform.Triangle => RenderTriangle(phase),
            _ => Math.Sin(TwoPi * phase)
        };

        return value * _amplitude;
    }

    private static double RenderTriangle(double phase)
    {
        if (phase < 0.25)
        {
            return phase * 4.0;
        }

        if (phase < 0.75)
        {
            return 2.0 - phase * 4.0;
        }

        return phase * 4.0 - 4.0;
    }

    private static double WrapPhase(double phase)
    {
        if (!double.IsFinite(phase))
        {
            return 0.0;
        }

        phase -= Math.Floor(phase);

        // На случай потери точности при очень больших значениях.
        return phase >= 1.0 ? 0.0 : phase;
    }
}
