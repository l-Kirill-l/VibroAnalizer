namespace VibroAnalyzer.Core.Analysis.Seismology;

/// <summary>
/// Энергия и интенсивность Ариаса: накопленная энергия и длительность значимой части записи.
/// </summary>
public sealed class SeismicEnergyAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized
{
    private const int WindowFrames = 8192;
    private const double Gravity = 9.80665;
    private const double MinScale = 0.001;
    private const double MaxScale = 1000.0;

    private volatile double[] _times = Array.Empty<double>();
    private volatile double[] _normalizedEnergy = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <summary>Масштаб входного сигнала в м/с² на единицу отсчёта.</summary>
    public double AccelerationScale { get; set; } = 1.0;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => new AnalyzerParameter[]
    {
        new("Масштаб в м/с²", MinScale, MaxScale, () => AccelerationScale,
            value => AccelerationScale = double.IsFinite(value)
                ? Math.Clamp(value, MinScale, MaxScale)
                : 1.0,
            "м/с²/ед."),
    };

    /// <inheritdoc />
    public override string Title => "Энергия и интенсивность Ариаса";

    /// <inheritdoc />
    public override string Category => "Сейсмология";

    /// <inheritdoc />
    public override string Description =>
        "Накапливает квадрат ускорения во времени и вычисляет интенсивность Ариаса, PGA и СКЗ. "
        + "По кривой накопленной энергии определяется длительность значимой части записи (5–95 %).";

    /// <inheritdoc />
    public string XLabel => "Время, с";

    /// <inheritdoc />
    public string YLabel => "Накопленная энергия";

    /// <inheritdoc />
    public double[] X => _times;

    /// <inheritdoc />
    public double[] Y => _normalizedEnergy;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowFrames, sampleRate * 10);

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
        var dt = 1.0 / sampleRate;
        var scale = double.IsFinite(AccelerationScale)
            ? Math.Clamp(AccelerationScale, MinScale, MaxScale)
            : 1.0;

        var times = new double[count];
        var cumulative = new double[count];
        var energy = 0.0;

        for (var i = 0; i < count; i++)
        {
            var acceleration = samples[i] * scale;
            energy += acceleration * acceleration * dt;
            cumulative[i] = energy;
            times[i] = (double)i / sampleRate;
        }

        var total = energy;
        var normalized = new double[count];

        if (total > 1e-30)
        {
            for (var i = 0; i < count; i++)
            {
                normalized[i] = cumulative[i] / total;
            }
        }

        var pga = 0.0;
        for (var i = 0; i < count; i++)
        {
            var magnitude = Math.Abs(samples[i] * scale);
            if (magnitude > pga)
            {
                pga = magnitude;
            }
        }

        var rms = Math.Sqrt(total / (count * dt));
        var ariasIntensity = Math.PI / (2.0 * Gravity) * total;

        var duration = 0.0;
        if (total > 1e-30)
        {
            var lowIndex = -1;
            var highIndex = -1;

            for (var i = 0; i < count; i++)
            {
                if (lowIndex < 0 && normalized[i] >= 0.05)
                {
                    lowIndex = i;
                }

                if (normalized[i] >= 0.95)
                {
                    highIndex = i;
                    break;
                }
            }

            if (lowIndex >= 0 && highIndex >= lowIndex)
            {
                duration = (highIndex - lowIndex) * dt;
            }
        }

        Publish(times, normalized, pga, rms, ariasIntensity, total, duration);
        MarkUpdated();
    }

    private void Publish(
        double[] times,
        double[] normalized,
        double pga,
        double rms,
        double ariasIntensity,
        double energy,
        double duration)
    {
        _times = times;
        _normalizedEnergy = normalized;
        _readouts = new[]
        {
            new AnalyzerReadout("PGA", pga, "ед."),
            new AnalyzerReadout("СКЗ", rms, "ед."),
            new AnalyzerReadout("Интенсивность Ариаса", ariasIntensity, "м/с"),
            new AnalyzerReadout("Энергия в окне", energy, "ед.²·с"),
            new AnalyzerReadout("Длительность 5–95 %", duration, "с"),
        };
    }
}
