using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis;

/// <summary>
/// Анализатор временной формы сигнала. Результаты публикуются целыми массивами,
/// поэтому UI может читать их из другого потока без блокировок.
/// </summary>
public sealed class OscilloscopeAnalyzer : AnalyzerBase
{
    private volatile double[] _times = Array.Empty<double>();
    private volatile double[] _values = Array.Empty<double>();
    private double _vpp;
    private double _rms;
    private double _dcOffset;
    private double _frequencyHz;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Осциллограф";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Время";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Показывает временную форму сигнала: размах, СКЗ, постоянную составляющую и оценку частоты. "
        + "Базовый инструмент для проверки тракта, настройки уровня и поиска искажений.";

    /// <summary>Длительность отображаемого окна, с.</summary>
    public double TimeWindowSeconds { get; set; } = 0.02;

    /// <summary>Метки времени отсчётов, с.</summary>
    public double[] Times => _times;

    /// <summary>Откалиброванные значения сигнала.</summary>
    public double[] Values => _values;

    /// <summary>Размах сигнала.</summary>
    public double Vpp => Volatile.Read(ref _vpp);

    /// <summary>Среднеквадратическое значение сигнала.</summary>
    public double Rms => Volatile.Read(ref _rms);

    /// <summary>Постоянная составляющая сигнала.</summary>
    public double DcOffset => Volatile.Read(ref _dcOffset);

    /// <summary>Оценка частоты сигнала, Гц.</summary>
    public double FrequencyHz => Volatile.Read(ref _frequencyHz);

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            return 16;
        }

        return Math.Max(16, (int)Math.Round(TimeWindowSeconds * sampleRate));
    }

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var count = samples.Length;
        var times = new double[count];
        var values = new double[count];
        var calibration = Calibration;

        for (var i = 0; i < count; i++)
        {
            times[i] = (double)i / sampleRate;
            values[i] = calibration.ToDisplay(samples[i]);
        }

        var vpp = SignalMath.PeakToPeak(values);
        var rms = SignalMath.Rms(values);
        var dcOffset = SignalMath.Mean(values);
        var frequencyHz = SignalMath.EstimateFrequencyHz(samples, sampleRate);

        // Публикуем новые массивы целиком, чтобы читающий поток не увидел
        // частично обновлённые данные.
        _times = times;
        _values = values;
        Volatile.Write(ref _vpp, vpp);
        Volatile.Write(ref _rms, rms);
        Volatile.Write(ref _dcOffset, dcOffset);
        Volatile.Write(ref _frequencyHz, frequencyHz);

        MarkUpdated();
    }
}
