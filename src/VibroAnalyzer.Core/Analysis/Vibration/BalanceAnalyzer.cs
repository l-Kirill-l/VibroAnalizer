using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Vibration;

/// <summary>
/// Балансировка 1×: синхронное детектирование амплитуды и фазы на частоте вращения.
/// </summary>
public sealed class BalanceAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized
{
    private const int WindowFrames = 8192;
    private const double MinRpm = 1.0;
    private const double MaxRpm = 100000.0;

    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    /// <summary>Частота вращения вала, об/мин.</summary>
    public double Rpm { get; set; } = 1500.0;

    /// <inheritdoc />
    public override string Title => "Балансировка 1x";

    /// <inheritdoc />
    public override string Category => "Вибрация и механика";

    /// <inheritdoc />
    public override string Description =>
        "Синхронно детектирует составляющую на частоте 1× и измеряет её амплитуду и фазу "
        + "относительно синусоидальной опоры. По амплитуде и фазе подбирают корректирующую массу при балансировке ротора.";

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
    public IReadOnlyList<AnalyzerParameter> Parameters => new AnalyzerParameter[]
    {
        new("Обороты", MinRpm, MaxRpm, () => Rpm,
            value => Rpm = double.IsFinite(value) ? Math.Clamp(value, MinRpm, MaxRpm) : 1500.0, "об/мин"),
    };

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowFrames, sampleRate * 4);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || sampleRate <= 0)
        {
            Publish(0.0, 0.0, SignalMath.DbFs(0.0));
            MarkUpdated();
            return;
        }

        var frequency = Rpm / 60.0;
        if (frequency <= 0.0 || frequency >= sampleRate * 0.5)
        {
            Publish(0.0, 0.0, SignalMath.DbFs(0.0));
            MarkUpdated();
            return;
        }

        var count = samples.Length;
        var window = WindowFunctions.Create(WindowKind.Hann, count);
        var windowSum = WindowFunctions.Sum(window);
        if (Math.Abs(windowSum) < 1e-12)
        {
            windowSum = count;
        }

        var sineSum = 0.0;
        var cosineSum = 0.0;
        var step = 2.0 * Math.PI * frequency / sampleRate;

        for (var i = 0; i < count; i++)
        {
            var angle = step * i;
            var value = samples[i] * window[i];
            sineSum += value * Math.Sin(angle);
            cosineSum += value * Math.Cos(angle);
        }

        // Опорой считается синус: фаза получается в тех же координатах, что и сигнал.
        var inPhase = 2.0 * sineSum / windowSum;
        var quadrature = 2.0 * cosineSum / windowSum;

        var amplitude = Math.Sqrt(inPhase * inPhase + quadrature * quadrature);
        var phase = Math.Atan2(quadrature, inPhase) * 180.0 / Math.PI;
        if (phase < 0.0)
        {
            phase += 360.0;
        }

        var imbalanceDb = SignalMath.DbFs(amplitude);

        Publish(amplitude, phase, imbalanceDb);
        MarkUpdated();
    }

    private void Publish(double amplitude, double phaseDegrees, double imbalanceDb)
    {
        _readouts = new[]
        {
            new AnalyzerReadout("Амплитуда 1x", amplitude, "ед."),
            new AnalyzerReadout("Фаза 1x", phaseDegrees, "°"),
            new AnalyzerReadout("Уровень дисбаланса", imbalanceDb, "дБFS (ампл.)"),
        };
    }
}
