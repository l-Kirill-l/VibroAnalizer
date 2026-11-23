using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.General;

/// <summary>
/// Частота переходов через ноль с гистерезисом. Простая оценка
/// преобладающей частоты и «гладкости» сигнала.
/// </summary>
public sealed class ZeroCrossingAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized, IAnalyzerToggleable
{
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private readonly AnalyzerParameter[] _parameters;
    private readonly AnalyzerToggle[] _toggles;

    public ZeroCrossingAnalyzer()
    {
        _parameters = new[]
        {
            new AnalyzerParameter(
                "Порог гистерезиса",
                0.0,
                0.5,
                () => ThresholdRatio,
                value => ThresholdRatio = Math.Clamp(value, 0.0, 0.5),
                "от пика"),
        };

        _toggles = new[]
        {
            new AnalyzerToggle(
                "Вычитать DC",
                () => RemoveDc,
                value => RemoveDc = value),
        };
    }

    /// <summary>Порог срабатывания относительно пика.</summary>
    public double ThresholdRatio { get; set; } = 0.05;

    /// <summary>Вычитать постоянную составляющую.</summary>
    public bool RemoveDc { get; set; } = true;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => _parameters;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerToggle> Toggles => _toggles;

    /// <inheritdoc />
    public override string Title => "Переходы через ноль";

    /// <inheritdoc />
    public override string Category => "Общие измерения";

    /// <inheritdoc />
    public override string Description =>
        "Считает переходы сигнала через ноль с гистерезисом и оценивает преобладающую "
        + "частоту как половину частоты переходов. Полезно для быстрой проверки тона, "
        + "речи, вибрации и «гладкости» сигнала.";

    /// <inheritdoc />
    public string XLabel => "Время, с";

    /// <inheritdoc />
    public string YLabel => "Сигнал";

    /// <inheritdoc />
    public double[] X => Array.Empty<double>();

    /// <inheritdoc />
    public double[] Y => Array.Empty<double>();

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(4096, sampleRate / 4);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length < 2 || sampleRate <= 0)
        {
            Publish(0.0, 0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var mean = RemoveDc ? SignalMath.Mean(samples) : 0.0;
        var peak = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            peak = Math.Max(peak, Math.Abs(samples[index] - mean));
        }

        var threshold = peak * Math.Clamp(ThresholdRatio, 0.0, 0.5);
        if (threshold <= 1e-15)
        {
            threshold = 0.0;
        }

        var state = 0;
        var crossings = 0;

        for (var index = 0; index < samples.Length; index++)
        {
            var value = samples[index] - mean;
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
                crossings++;
                state = -1;
            }
            else if (state < 0 && value >= threshold)
            {
                crossings++;
                state = 1;
            }
        }

        var duration = (double)samples.Length / sampleRate;
        var crossingRate = duration > 0.0 ? crossings / duration : 0.0;
        var frequency = crossingRate * 0.5;
        var rms = SignalMath.Rms(samples);

        Publish(
            crossingRate,
            frequency,
            rms,
            peak,
            threshold);
        MarkUpdated();
    }

    private void Publish(
        double crossingRate,
        double frequency,
        double rms,
        double peak,
        double threshold)
    {
        _readouts = new[]
        {
            new AnalyzerReadout("Переходы", crossingRate, "1/с"),
            new AnalyzerReadout("Оценка частоты", frequency, "Гц"),
            new AnalyzerReadout("СКЗ", rms, "ед."),
            new AnalyzerReadout("Пик", peak, "ед."),
            new AnalyzerReadout("Порог", threshold, "ед."),
        };
    }
}
