using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Medicine;

/// <summary>
/// ЭМГ: СКЗ, медианная частота, пик и оценка активности мышцы.
/// </summary>
public sealed class EmgAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowFrames = 8192;

    private volatile double[] _frequencies = Array.Empty<double>();
    private volatile double[] _levelsDb = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <inheritdoc />
    public override string Title => "ЭМГ";

    /// <inheritdoc />
    public override string Category => "Медицина";

    /// <inheritdoc />
    public override string Description =>
        "Измеряет энергетические характеристики электромиограммы: СКЗ, пик и медианную частоту спектра. "
        + "Медианная частота и активность помогают следить за утомлением и уровнем напряжения мышцы.";

    /// <inheritdoc />
    public string XLabel => "Частота, Гц";

    /// <inheritdoc />
    public string YLabel => "дБFS (ампл.)";

    /// <inheritdoc />
    public double[] X => _frequencies;

    /// <inheritdoc />
    public double[] Y => _levelsDb;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Clamp(sampleRate / 4, 1024, 32768);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var count = samples.Length;
        var filtered = new double[count];

        // Поверхностная ЭМГ: 20–450 Гц + режекция сетевых 50/60 Гц.
        // Это убирает DC, дрейф и сетевую наводку до расчёта RMS/спектра.
        var nyquist = sampleRate * 0.5;
        var highPass = BiquadFilter.CreateHighPass(sampleRate, Math.Min(20.0, nyquist * 0.5));
        var lowPass = BiquadFilter.CreateLowPass(sampleRate, Math.Min(450.0, nyquist * 0.9));
        BiquadFilter? notch50 = nyquist > 55.0 ? BiquadFilter.CreateNotch(sampleRate, 50.0, 30.0) : null;
        BiquadFilter? notch60 = nyquist > 65.0 ? BiquadFilter.CreateNotch(sampleRate, 60.0, 30.0) : null;

        for (var i = 0; i < count; i++)
        {
            var value = highPass.Process(samples[i]);
            value = lowPass.Process(value);

            if (notch50 is not null)
            {
                value = notch50.Process(value);
            }

            if (notch60 is not null)
            {
                value = notch60.Process(value);
            }

            filtered[i] = value;
        }

        var rms = SignalMath.Rms(filtered);
        var peak = SignalMath.Peak(filtered);

        var spectrum = FftProcessor.Compute(filtered, sampleRate, WindowKind.Hann, removeDc: true);
        var frequencies = spectrum.Frequencies;
        var magnitudes = spectrum.Magnitudes;

        var lowFrequency = 20.0;
        var highFrequency = Math.Min(450.0, sampleRate * 0.45);
        var totalPower = 0.0;

        for (var k = 1; k < magnitudes.Length; k++)
        {
            if (frequencies[k] < lowFrequency || frequencies[k] > highFrequency)
            {
                continue;
            }

            totalPower += magnitudes[k] * magnitudes[k];
        }

        var medianFrequency = 0.0;
        if (totalPower > 0.0)
        {
            var half = totalPower * 0.5;
            var cumulative = 0.0;
            for (var k = 1; k < magnitudes.Length; k++)
            {
                if (frequencies[k] < lowFrequency || frequencies[k] > highFrequency)
                {
                    continue;
                }

                cumulative += magnitudes[k] * magnitudes[k];
                if (cumulative >= half)
                {
                    medianFrequency = frequencies[k];
                    break;
                }
            }
        }

        var rmsDb = SignalMath.DbFs(rms);
        var activity = Math.Clamp((rmsDb + 60.0) / 60.0, 0.0, 1.0);

        Publish(spectrum.Frequencies, spectrum.MagnitudesDb, rms, medianFrequency, peak, activity);
        MarkUpdated();
    }

    private void Publish(
        double[] frequencies,
        double[] levelsDb,
        double rms,
        double medianFrequency,
        double peak,
        double activity)
    {
        _frequencies = frequencies;
        _levelsDb = levelsDb;
        _readouts = new[]
        {
            new AnalyzerReadout("СКЗ", rms, "ед."),
            new AnalyzerReadout("Медианная частота", medianFrequency, "Гц"),
            new AnalyzerReadout("Пик", peak, "ед."),
            new AnalyzerReadout("Активность", activity, ""),
        };
    }
}
