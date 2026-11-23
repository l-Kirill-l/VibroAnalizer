using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Medicine;

/// <summary>
/// Ритмы ЭЭГ: мощность и доля стандартных частотных полос.
/// </summary>
public sealed class EegBandAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowFrames = 16384;
    private const double MinPower = 1e-24;
    private const double MinPowerDb = -240.0;

    private static readonly (string Name, double Low, double High)[] Bands =
    {
        ("Дельта", 0.5, 4.0),
        ("Тета", 4.0, 8.0),
        ("Альфа", 8.0, 13.0),
        ("Бета", 13.0, 30.0),
        ("Гамма", 30.0, 45.0),
    };

    private volatile double[] _centers = Array.Empty<double>();
    private volatile double[] _powerDb = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <inheritdoc />
    public override string Title => "Ритмы ЭЭГ";

    /// <inheritdoc />
    public override string Category => "Медицина";

    /// <inheritdoc />
    public override string Description =>
        "Раскладывает сигнал в спектр и считает мощность в полосах дельта, тета, альфа, бета и гамма. "
        + "Соотношение полос помогает оценить преобладающий ритм ЭЭГ и его изменение во времени.";

    /// <inheritdoc />
    public string XLabel => "Частота, Гц";

    /// <inheritdoc />
    public string YLabel => "Мощность, дБ";

    /// <inheritdoc />
    public double[] X => _centers;

    /// <inheritdoc />
    public double[] Y => _powerDb;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowFrames, sampleRate * 4);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            var emptyPowerDb = new double[Bands.Length];
            Array.Fill(emptyPowerDb, MinPowerDb);
            Publish(Array.Empty<double>(), Array.Empty<double>(), emptyPowerDb, new double[Bands.Length]);
            MarkUpdated();
            return;
        }

        var filtered = new double[samples.Length];
        var highPass = BiquadFilter.CreateHighPass(sampleRate, 0.5);
        var lowPass = BiquadFilter.CreateLowPass(sampleRate, 45.0);
        BiquadFilter? notch50 = sampleRate > 110 ? BiquadFilter.CreateNotch(sampleRate, 50.0, 30.0) : null;
        BiquadFilter? notch60 = sampleRate > 130 ? BiquadFilter.CreateNotch(sampleRate, 60.0, 30.0) : null;

        for (var i = 0; i < samples.Length; i++)
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

        var spectrum = FftProcessor.Compute(filtered, sampleRate, WindowKind.Hann, removeDc: true);
        var frequencies = spectrum.Frequencies;
        var magnitudes = spectrum.Magnitudes;
        var powers = new double[Bands.Length];
        var totalPower = 0.0;

        for (var band = 0; band < Bands.Length; band++)
        {
            var power = 0.0;
            var low = Bands[band].Low;
            var high = Bands[band].High;

            for (var k = 1; k < frequencies.Length; k++)
            {
                var frequency = frequencies[k];
                if (frequency >= low && frequency < high)
                {
                    power += magnitudes[k] * magnitudes[k];
                }
            }

            powers[band] = power;
            totalPower += power;
        }

        var centers = new double[Bands.Length];
        var powerDb = new double[Bands.Length];
        var percentages = new double[Bands.Length];

        for (var band = 0; band < Bands.Length; band++)
        {
            centers[band] = (Bands[band].Low + Bands[band].High) * 0.5;
            powerDb[band] = 10.0 * Math.Log10(Math.Max(powers[band], MinPower));
            percentages[band] = totalPower > 0.0 ? powers[band] / totalPower * 100.0 : 0.0;
        }

        Publish(centers, powerDb, powerDb, percentages);
        MarkUpdated();
    }

    private void Publish(double[] centers, double[] powerDb, double[] powerDbForReadouts, double[] percentages)
    {
        _centers = centers;
        _powerDb = powerDb;

        var readouts = new AnalyzerReadout[Bands.Length * 2];
        for (var band = 0; band < Bands.Length; band++)
        {
            var name = Bands[band].Name;
            var power = powerDbForReadouts.Length > band ? powerDbForReadouts[band] : MinPowerDb;
            var percent = percentages.Length > band ? percentages[band] : 0.0;

            readouts[band * 2] = new AnalyzerReadout($"Мощность {name}", power, "дБ");
            readouts[band * 2 + 1] = new AnalyzerReadout($"Доля {name}", percent, "%");
        }

        _readouts = readouts;
    }
}
