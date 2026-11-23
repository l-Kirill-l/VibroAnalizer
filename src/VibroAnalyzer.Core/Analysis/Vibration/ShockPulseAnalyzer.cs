using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Vibration;

/// <summary>
/// Анализатор ударного импульса: пик, время нарастания, эксцесс, крест-фактор и энергия.
/// </summary>
public sealed class ShockPulseAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowFrames = 8192;
    private const double MinLevel = 1e-12;

    private volatile double[] _times = Array.Empty<double>();
    private volatile double[] _envelope = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <inheritdoc />
    public override string Title => "Ударный импульс";

    /// <inheritdoc />
    public override string Category => "Вибрация и механика";

    /// <inheritdoc />
    public override string Description =>
        "Описывает ударный импульс во временной области: амплитуду, время нарастания, "
        + "эксцесс, крест-фактор и энергию. Помогает отличать одиночные удары от вибрации и оценивать жёсткость соударений.";

    /// <summary>Частота среза фильтра огибающей, Гц.</summary>
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
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var count = samples.Length;

        var peak = SignalMath.Peak(samples);
        var rms = SignalMath.Rms(samples);
        var crestFactor = rms > MinLevel ? peak / rms : 0.0;

        var mean = SignalMath.Mean(samples);
        var sum2 = 0.0;
        var sum4 = 0.0;
        for (var i = 0; i < count; i++)
        {
            var delta = samples[i] - mean;
            var square = delta * delta;
            sum2 += square;
            sum4 += square * square;
        }

        var variance = sum2 / count;
        var kurtosis = variance > 1e-18 ? sum4 / count / (variance * variance) - 3.0 : 0.0;

        var energy = 0.0;
        for (var i = 0; i < count; i++)
        {
            energy += samples[i] * samples[i];
        }

        energy /= sampleRate;

        var peakIndex = 0;
        var peakValue = 0.0;
        for (var i = 0; i < count; i++)
        {
            var magnitude = Math.Abs(samples[i]);
            if (magnitude > peakValue)
            {
                peakValue = magnitude;
                peakIndex = i;
            }
        }

        var threshold = peakValue * 0.1;
        var riseStart = peakIndex;
        while (riseStart > 0 && Math.Abs(samples[riseStart - 1]) >= threshold)
        {
            riseStart--;
        }

        var riseTimeMs = (peakIndex - riseStart) * 1000.0 / sampleRate;

        var filter = BiquadFilter.CreateLowPass(sampleRate, CutoffHz);
        var envelope = new double[count];
        var times = new double[count];
        for (var i = 0; i < count; i++)
        {
            envelope[i] = filter.Process(Math.Abs(samples[i]));
            times[i] = (double)i / sampleRate;
        }

        Publish(times, envelope, peak, riseTimeMs, kurtosis, crestFactor, energy);
        MarkUpdated();
    }

    private void Publish(
        double[] times,
        double[] envelope,
        double peak,
        double riseTimeMs,
        double kurtosis,
        double crestFactor,
        double energy)
    {
        _times = times;
        _envelope = envelope;
        _readouts = new[]
        {
            new AnalyzerReadout("Пик", peak, "ед."),
            new AnalyzerReadout("Время нарастания", riseTimeMs, "мс"),
            new AnalyzerReadout("Эксцесс", kurtosis, ""),
            new AnalyzerReadout("Крест-фактор", crestFactor, ""),
            new AnalyzerReadout("Энергия импульса", energy, "ед.²·с"),
        };
    }
}
