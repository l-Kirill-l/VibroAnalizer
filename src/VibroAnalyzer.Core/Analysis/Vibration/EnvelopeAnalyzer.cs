using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Vibration;

/// <summary>
/// Анализатор огибающей: модуль сигнала сглаживается фильтром нижних частот.
/// </summary>
public sealed class EnvelopeAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowFrames = 8192;
    private const double MinLevel = 1e-12;

    private volatile double[] _times = Array.Empty<double>();
    private volatile double[] _envelope = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <inheritdoc />
    public override string Title => "Огибающая";

    /// <inheritdoc />
    public override string Category => "Вибрация и механика";

    /// <inheritdoc />
    public override string Description =>
        "Строит огибающую сигнала: берёт модуль отсчётов и сглаживает его фильтром нижних частот. "
        + "По огибающей удобно находить амплитудную модуляцию, биения и периодические нагрузки узлов.";

    /// <summary>Частота среза сглаживающего ФНЧ, Гц.</summary>
    public double CutoffHz { get; set; } = 500.0;

    /// <inheritdoc />
    public string XLabel => "Время, с";

    /// <inheritdoc />
    public string YLabel => "Огибающая";

    /// <inheritdoc />
    public double[] X => _times;

    /// <inheritdoc />
    public double[] Y => _envelope;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate) => WindowFrames;

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var count = samples.Length;
        var filter = BiquadFilter.CreateLowPass(sampleRate, CutoffHz);
        var envelope = new double[count];
        for (var i = 0; i < count; i++)
        {
            envelope[i] = filter.Process(Math.Abs(samples[i]));
        }

        var times = new double[count];
        for (var i = 0; i < count; i++)
        {
            times[i] = (double)i / sampleRate;
        }

        // Отбрасываем переходный процесс фильтра, иначе он даёт ложную
        // низкочастотную «модуляцию» у сигнала с постоянной амплитудой.
        var transient = (int)Math.Round(5.0 * sampleRate / Math.Max(CutoffHz, 1.0));
        var skip = Math.Clamp(transient, 1, Math.Max(1, count / 2));
        var tail = new double[count - skip];
        Array.Copy(envelope, skip, tail, 0, tail.Length);

        var rms = SignalMath.Rms(tail);
        var peak = SignalMath.Peak(tail);
        var envelopeMean = SignalMath.Mean(tail);
        var envelopeRange = SignalMath.PeakToPeak(tail);
        var frequency = envelopeMean > 1e-12 && envelopeRange > envelopeMean * 0.05
            ? EstimateFrequency(tail, sampleRate, peak)
            : 0.0;

        Publish(times, envelope, rms, peak, frequency);
        MarkUpdated();
    }

    private void Publish(double[] times, double[] envelope, double rms, double peak, double frequency)
    {
        _times = times;
        _envelope = envelope;
        _readouts = new[]
        {
            new AnalyzerReadout("СКЗ огибающей", rms, "ед."),
            new AnalyzerReadout("Пик огибающей", peak, "ед."),
            new AnalyzerReadout("Частота огибающей", frequency, "Гц"),
        };
    }

    private static double EstimateFrequency(double[] envelope, int sampleRate, double peak)
    {
        if (envelope.Length < 8 || sampleRate <= 0 || peak < MinLevel)
        {
            return 0.0;
        }

        var spectrum = FftProcessor.Compute(envelope, sampleRate, WindowKind.Hann, removeDc: true);
        return spectrum.PeakFrequencyHz;
    }
}
