using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Vibration;

/// <summary>
/// Спектр огибающей: полосовая фильтрация, преобразование Гильберта и БПФ
/// огибающей для поиска частоты амплитудной модуляции.
/// </summary>
public sealed class EnvelopeSpectrumAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized, IAnalyzerToggleable
{
    private const int MinLevelDb = -240;
    private const double MinValue = 1e-12;

    private volatile double[] _frequencies = Array.Empty<double>();
    private volatile double[] _levelsDb = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private readonly AnalyzerParameter[] _parameters;
    private readonly AnalyzerToggle[] _toggles;
    private long _lastProcessTicks;

    public EnvelopeSpectrumAnalyzer()
    {
        _parameters = new[]
        {
            new AnalyzerParameter(
                "Нижняя частота полосы",
                20,
                20000,
                () => Filters.HighPassHz,
                value => Filters.HighPassHz = value,
                "Гц"),
            new AnalyzerParameter(
                "Верхняя частота полосы",
                20,
                20000,
                () => Filters.LowPassHz,
                value => Filters.LowPassHz = value,
                "Гц"),
        };

        _toggles = new[]
        {
            new AnalyzerToggle(
                "Полосовой фильтр несущей",
                () => Filters.BandPassEnabled,
                value => Filters.BandPassEnabled = value),
        };
    }

    /// <summary>Полосовой фильтр перед демодуляцией.</summary>
    public AnalysisFilterSettings Filters { get; } = new()
    {
        HighPassHz = 1000,
        LowPassHz = 6000,
        MainsNotchEnabled = false,
    };

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => _parameters;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerToggle> Toggles => _toggles;

    /// <inheritdoc />
    public override string Title => "Спектр огибающей";

    /// <inheritdoc />
    public override string Category => "Вибрация и механика";

    /// <inheritdoc />
    public override string Description =>
        "Выделяет полосу резонанса, получает огибающую через преобразование Гильберта "
        + "и строит её спектр. Позволяет увидеть частоту повторения ударов, скрытую "
        + "в высокочастотной вибрации подшипников и зубчатых передач.";

    /// <inheritdoc />
    public string XLabel => "Частота огибающей, Гц";

    /// <inheritdoc />
    public string YLabel => "Уровень огибающей, дБ";

    /// <inheritdoc />
    public double[] X => _frequencies;

    /// <inheritdoc />
    public double[] Y => _levelsDb;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(16384, sampleRate);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        var now = Environment.TickCount64;
        if (now - _lastProcessTicks < 250)
        {
            return;
        }

        _lastProcessTicks = now;

        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var low = Math.Min(Filters.HighPassHz, Filters.LowPassHz);
        var high = Math.Max(Filters.HighPassHz, Filters.LowPassHz);
        high = Math.Min(high, sampleRate * 0.49);
        low = Math.Max(1.0, Math.Min(low, high - 1.0));

        if (high <= low)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        Filters.HighPassHz = low;
        Filters.LowPassHz = high;
        var filtered = Filters.Apply(samples, sampleRate);
        var envelope = HilbertEnvelope(filtered);
        if (envelope.Length == 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        // Края окна и переходный процесс полосового фильтра дают выбросы
        // огибающей. Для глубины модуляции берём устойчивые процентили.
        var sortedEnvelope = envelope.OrderBy(value => value).ToArray();
        var envelopeLow = Percentile(sortedEnvelope, 5.0);
        var envelopeHigh = Percentile(sortedEnvelope, 95.0);
        var modulationDepth = envelopeHigh + envelopeLow > MinValue
            ? (envelopeHigh - envelopeLow) / (envelopeHigh + envelopeLow)
            : 0.0;

        var envelopeSpectrum = FftProcessor.Compute(
            envelope,
            sampleRate,
            WindowKind.Hann,
            removeDc: true);
        var carrierSpectrum = FftProcessor.Compute(
            filtered,
            sampleRate,
            WindowKind.Hann,
            removeDc: true);

        Publish(
            envelopeSpectrum.Frequencies,
            envelopeSpectrum.MagnitudesDb,
            envelopeSpectrum.PeakFrequencyHz,
            modulationDepth,
            carrierSpectrum.PeakFrequencyHz);
        MarkUpdated();
    }

    private static double[] HilbertEnvelope(double[] signal)
    {
        if (signal.Length == 0)
        {
            return Array.Empty<double>();
        }

        var nfft = NextPowerOfTwo(signal.Length);
        var buffer = new Complex[nfft];
        for (var index = 0; index < signal.Length; index++)
        {
            buffer[index] = new Complex(signal[index], 0.0);
        }

        Fourier.Forward(buffer, FourierOptions.NoScaling);

        for (var bin = 1; bin < nfft / 2; bin++)
        {
            buffer[bin] *= 2.0;
        }

        for (var bin = nfft / 2 + 1; bin < nfft; bin++)
        {
            buffer[bin] = Complex.Zero;
        }

        Fourier.Inverse(buffer, FourierOptions.NoScaling);

        var envelope = new double[signal.Length];
        for (var index = 0; index < signal.Length; index++)
        {
            envelope[index] = buffer[index].Magnitude / nfft;
        }

        return envelope;
    }

    private void Publish(
        double[] frequencies,
        double[] levelsDb,
        double modulationFrequency,
        double modulationDepth,
        double carrierFrequency)
    {
        _frequencies = frequencies;
        _levelsDb = levelsDb;
        _readouts = new[]
        {
            new AnalyzerReadout("Частота модуляции", modulationFrequency, "Гц"),
            new AnalyzerReadout("Глубина модуляции", modulationDepth, "отн."),
            new AnalyzerReadout("Несущая", carrierFrequency, "Гц"),
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0.0;
        }

        var position = Math.Clamp(percentile, 0.0, 100.0) / 100.0 * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] * (1.0 - fraction) + sorted[upper] * fraction;
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
