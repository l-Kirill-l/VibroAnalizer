using VibroAnalyzer.Core.Analysis.General;
using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Vibration;

/// <summary>
/// Тахометр: оценивает частоту вращения по автокорреляции сигнала.
/// </summary>
public sealed class TachometerAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowFrames = 8192;
    private const double MinFrequencyHz = 0.5;
    private const double MaxFrequencyHz = 1000.0;

    private readonly AutocorrelationAnalyzer _autocorrelation = new();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <inheritdoc />
    public override string Title => "Тахометр";

    /// <inheritdoc />
    public override string Category => "Вибрация и механика";

    /// <inheritdoc />
    public override string Description =>
        "Оценивает частоту вращения вала по периодичности сигнала: находит основной период "
        + "автокорреляцией и переводит его в обороты в минуту. Используется для тахометрических датчиков и контроля скорости машин.";

    /// <inheritdoc />
    public string XLabel => "—";

    /// <inheritdoc />
    public string YLabel => "—";

    /// <inheritdoc />
    public double[] X => Array.Empty<double>();

    /// <inheritdoc />
    public double[] Y => Array.Empty<double>();

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowFrames, sampleRate * 2);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length < 16 || sampleRate <= 0)
        {
            Publish(0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        // Автокорреляция через БПФ: O(N log N) вместо O(N·lag),
        // что важно для длинных окон тахометра.
        _autocorrelation.Process(samples, sampleRate);

        var frequencyHz = _autocorrelation.FrequencyHz;

        if (!double.IsFinite(frequencyHz)
            || frequencyHz < MinFrequencyHz
            || frequencyHz > MaxFrequencyHz)
        {
            var fallback = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
            frequencyHz = fallback.PeakFrequencyHz;
        }

        if (double.IsFinite(frequencyHz)
            && frequencyHz >= MinFrequencyHz
            && frequencyHz <= MaxFrequencyHz)
        {
            // Если найденная частота — 2x/3x/4x/5x, а субгармоника значима,
            // выбираем её: это защита от захвата гармоники.
            var spectrum = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
            var binWidth = (double)sampleRate / spectrum.FftSize;
            var peakBin = (int)Math.Round(frequencyHz / binWidth);
            var peakAmplitude = LocalPeak(spectrum.Magnitudes, peakBin, 3);

            for (var divisor = 2; divisor <= 5 && peakAmplitude > 1e-12; divisor++)
            {
                var candidate = frequencyHz / divisor;
                if (candidate < MinFrequencyHz)
                {
                    continue;
                }

                var candidateBin = (int)Math.Round(candidate / binWidth);
                var candidateAmplitude = LocalPeak(spectrum.Magnitudes, candidateBin, 3);

                if (candidateAmplitude > peakAmplitude * 0.10)
                {
                    frequencyHz = candidate;
                    break;
                }
            }
        }

        if (!double.IsFinite(frequencyHz)
            || frequencyHz < MinFrequencyHz
            || frequencyHz > MaxFrequencyHz)
        {
            frequencyHz = 0.0;
        }

        var rpm = frequencyHz * 60.0;
        var periodMs = frequencyHz > 0.0 ? 1000.0 / frequencyHz : 0.0;

        Publish(rpm, frequencyHz, periodMs);
        MarkUpdated();
    }

    private static double LocalPeak(double[] magnitudes, int centerBin, int radius)
    {
        var start = Math.Clamp(centerBin - radius, 1, magnitudes.Length - 1);
        var end = Math.Clamp(centerBin + radius, start, magnitudes.Length - 1);
        var peak = magnitudes[start];

        for (var i = start + 1; i <= end; i++)
        {
            peak = Math.Max(peak, magnitudes[i]);
        }

        return peak;
    }

    private void Publish(double rpm, double frequencyHz, double periodMs)
    {
        _readouts = new[]
        {
            new AnalyzerReadout("RPM", rpm, "об/мин"),
            new AnalyzerReadout("Частота", frequencyHz, "Гц"),
            new AnalyzerReadout("Период", periodMs, "мс"),
        };
    }
}
