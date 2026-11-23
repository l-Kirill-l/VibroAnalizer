namespace VibroAnalyzer.Core.Analysis.Seismology;

/// <summary>
/// Спектр реакции: максимальный отклик линейного осциллятора с заданным затуханием.
/// </summary>
public sealed class ResponseSpectrumAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized
{
    private const int WindowFrames = 16384;
    private const int PointCount = 100;
    private const int ComputeEveryCalls = 10;
    private const double MinPeriod = 0.02;
    private const double MaxPeriod = 5.0;
    private int _callCounter;

    private volatile double[] _periods = Array.Empty<double>();
    private volatile double[] _accelerations = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <summary>Коэффициент затухания осциллятора (доля от критического).</summary>
    public double Damping { get; set; } = 0.05;

    /// <inheritdoc />
    public override string Title => "Спектр реакции";

    /// <inheritdoc />
    public override string Category => "Сейсмология";

    /// <inheritdoc />
    public override string Description =>
        "Пропускает запись ускорения через набор линейных осцилляторов с разными периодами "
        + "и заданным затуханием, а затем строит псевдоускорение реакции. "
        + "Пик спектра показывает период, наиболее опасный для сооружений и оборудования.";

    /// <inheritdoc />
    public string XLabel => "Период, с";

    /// <inheritdoc />
    public string YLabel => "Псевдоускорение";

    /// <inheritdoc />
    public double[] X => _periods;

    /// <inheritdoc />
    public double[] Y => _accelerations;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => new AnalyzerParameter[]
    {
        new("Затухание", 0.01, 0.3, () => Damping,
            value => Damping = double.IsFinite(value) ? Math.Clamp(value, 0.01, 0.3) : 0.05, ""),
    };

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowFrames, sampleRate * 10);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (++_callCounter % ComputeEveryCalls != 1)
        {
            // Спектр реакции тяжёлый: пересчитываем ~2 раза в секунду,
            // а не на каждом 50-мс тике анализаторов.
            return;
        }

        if (samples is null || samples.Length < 2 || sampleRate <= 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var damping = double.IsFinite(Damping) ? Math.Clamp(Damping, 0.01, 0.3) : 0.05;
        var ratio = MaxPeriod / MinPeriod;

        var periods = new double[PointCount];
        var accelerations = new double[PointCount];

        for (var i = 0; i < PointCount; i++)
        {
            var period = MinPeriod * Math.Pow(ratio, i / (PointCount - 1.0));
            periods[i] = period;
            accelerations[i] = ComputePseudoAcceleration(samples, sampleRate, period, damping);
        }

        var peakIndex = 0;
        for (var i = 1; i < accelerations.Length; i++)
        {
            if (accelerations[i] > accelerations[peakIndex])
            {
                peakIndex = i;
            }
        }

        var peakAcceleration = accelerations[peakIndex];
        var peakPeriod = peakAcceleration > 0.0 ? periods[peakIndex] : 0.0;

        Publish(periods, accelerations, peakPeriod, peakAcceleration);
        MarkUpdated();
    }

    /// <summary>
    /// Интегрирует уравнение относительного движения осциллятора методом Ньюмарка
    /// (среднее ускорение) и возвращает пиковое псевдоускорение ω²·|u|.
    /// </summary>
    private static double ComputePseudoAcceleration(double[] acceleration, int sampleRate, double period, double damping)
    {
        if (period <= 0.0 || sampleRate <= 0)
        {
            return 0.0;
        }

        var frequency = 1.0 / period;
        if (frequency >= sampleRate * 0.5)
        {
            return 0.0;
        }

        var dt = 1.0 / sampleRate;
        var omega = 2.0 * Math.PI / period;
        var stiffness = omega * omega;
        var dampingCoefficient = 2.0 * damping * omega;

        const double beta = 0.25;
        const double gamma = 0.5;

        var a0 = 1.0 / (beta * dt * dt);
        var a1 = gamma / (beta * dt);
        var a2 = 1.0 / (beta * dt);
        var a3 = 1.0 / (2.0 * beta);
        var a4 = gamma / beta;
        var a5 = dt * (gamma / (2.0 * beta) - 1.0);

        var effectiveStiffness = stiffness + a0 + a1 * dampingCoefficient;

        var displacement = 0.0;
        var velocity = 0.0;
        var relativeAcceleration = -acceleration[0];
        var previousForce = -acceleration[0];
        var peakDisplacement = 0.0;

        for (var i = 1; i < acceleration.Length; i++)
        {
            var force = -acceleration[i];
            var deltaForce = force - previousForce
                + a2 * velocity
                + a3 * relativeAcceleration
                + dampingCoefficient * (a4 * velocity + a5 * relativeAcceleration);

            var deltaDisplacement = deltaForce / effectiveStiffness;
            var deltaVelocity = a1 * deltaDisplacement
                - a4 * velocity
                - a5 * relativeAcceleration;
            var deltaAcceleration = a0 * deltaDisplacement
                - a2 * velocity
                - a3 * relativeAcceleration;

            displacement += deltaDisplacement;
            velocity += deltaVelocity;
            relativeAcceleration += deltaAcceleration;

            var magnitude = Math.Abs(displacement);
            if (magnitude > peakDisplacement)
            {
                peakDisplacement = magnitude;
            }

            previousForce = force;
        }

        return stiffness * peakDisplacement;
    }

    private void Publish(double[] periods, double[] accelerations, double peakPeriod, double peakAcceleration)
    {
        _periods = periods;
        _accelerations = accelerations;
        _readouts = new[]
        {
            new AnalyzerReadout("Пиковый период", peakPeriod, "с"),
            new AnalyzerReadout("Пиковое ускорение", peakAcceleration, "ед."),
        };
    }
}
