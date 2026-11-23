using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Acoustics;

/// <summary>
/// Уровень звука с частотной коррекцией A и C, рассчитанной по спектру БПФ.
/// </summary>
public sealed class SoundLevelAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 8192;
    private const double MinLevelDb = -240.0;

    // Эквивалентная шумовая полоса окна Ханна.
    private const double HannEnbw = 1.5;

    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _levelDbFs;
    private double _levelDbA;
    private double _levelDbC;
    private double _peakDbFs;
    private double _crestFactor;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Уровень звука";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Акустика";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Измеряет общий уровень звука в дБFS и корректированные уровни dB(A) и dB(C) по "
        + "стандартным весовым кривым, применяемым к bins спектра. Используется для оценки "
        + "громкости, шума оборудования и санитарного контроля.";

    /// <summary>Некорректированный уровень, дБFS.</summary>
    public double LevelDbFs => Volatile.Read(ref _levelDbFs);

    /// <summary>Уровень с коррекцией A, дБА.</summary>
    public double LevelDbA => Volatile.Read(ref _levelDbA);

    /// <summary>Уровень с коррекцией C, дБC.</summary>
    public double LevelDbC => Volatile.Read(ref _levelDbC);

    /// <summary>Пиковый уровень, дБFS.</summary>
    public double PeakDbFs => Volatile.Read(ref _peakDbFs);

    /// <summary>Отношение пика к СКЗ.</summary>
    public double CrestFactor => Volatile.Read(ref _crestFactor);

    /// <inheritdoc />
    public string XLabel => "Частота, Гц";

    /// <inheritdoc />
    public string YLabel => "Уровень, дБ";

    /// <inheritdoc />
    public double[] X => Array.Empty<double>();

    /// <inheritdoc />
    public double[] Y => Array.Empty<double>();

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate) => WindowSize;

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        if (samples.Length == 0)
        {
            Publish(MinLevelDb, MinLevelDb, MinLevelDb, MinLevelDb, 0.0);
            MarkUpdated();
            return;
        }

        var result = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
        var frequencies = result.Frequencies;
        var magnitudes = result.Magnitudes;

        var totalPower = 0.0;
        var powerA = 0.0;
        var powerC = 0.0;

        for (var k = 1; k < magnitudes.Length; k++)
        {
            var magnitude = magnitudes[k];
            var power = magnitude * magnitude;
            totalPower += power;

            var frequency = frequencies[k];
            var gainA = WeightingGainA(frequency);
            var gainC = WeightingGainC(frequency);

            powerA += power * gainA * gainA;
            powerC += power * gainC * gainC;
        }

        var rms = Math.Sqrt(totalPower / (2.0 * HannEnbw));
        var peak = SignalMath.Peak(samples);

        var levelDbFs = SignalMath.DbFs(rms);
        var levelDbA = PowerToDb(powerA);
        var levelDbC = PowerToDb(powerC);
        var peakDbFs = SignalMath.DbFs(peak);
        var crestFactor = rms > 1e-12 ? peak / rms : 0.0;

        Publish(levelDbFs, levelDbA, levelDbC, peakDbFs, crestFactor);
        MarkUpdated();
    }

    private static double PowerToDb(double power)
    {
        var rms = Math.Sqrt(Math.Max(power, 0.0) / (2.0 * HannEnbw));
        return SignalMath.DbFs(rms);
    }

    /// <summary>Весовой коэффициент A по стандартной формуле IEC 61672.</summary>
    private static double WeightingGainA(double frequency)
    {
        if (frequency <= 0.0 || double.IsNaN(frequency))
        {
            return 0.0;
        }

        var f2 = frequency * frequency;
        var numerator = 12194.0 * 12194.0 * f2 * f2;
        var denominator =
            (f2 + 20.6 * 20.6)
            * Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9))
            * (f2 + 12194.0 * 12194.0);

        if (denominator <= 0.0)
        {
            return 0.0;
        }

        var gain = numerator / denominator;
        return Math.Pow(10.0, (20.0 * Math.Log10(Math.Max(gain, 1e-12)) + 2.0) / 20.0);
    }

    /// <summary>Весовой коэффициент C по стандартной формуле IEC 61672.</summary>
    private static double WeightingGainC(double frequency)
    {
        if (frequency <= 0.0 || double.IsNaN(frequency))
        {
            return 0.0;
        }

        var f2 = frequency * frequency;
        var numerator = 12194.0 * 12194.0 * f2;
        var denominator = (f2 + 20.6 * 20.6) * (f2 + 12194.0 * 12194.0);

        if (denominator <= 0.0)
        {
            return 0.0;
        }

        var gain = numerator / denominator;
        return Math.Pow(10.0, (20.0 * Math.Log10(Math.Max(gain, 1e-12)) + 0.06) / 20.0);
    }

    private void Publish(
        double levelDbFs,
        double levelDbA,
        double levelDbC,
        double peakDbFs,
        double crestFactor)
    {
        Volatile.Write(ref _levelDbFs, levelDbFs);
        Volatile.Write(ref _levelDbA, levelDbA);
        Volatile.Write(ref _levelDbC, levelDbC);
        Volatile.Write(ref _peakDbFs, peakDbFs);
        Volatile.Write(ref _crestFactor, crestFactor);

        _readouts = new[]
        {
            new AnalyzerReadout("dBFS", levelDbFs, "дБFS (СКЗ)"),
            new AnalyzerReadout("dB(A)", levelDbA, "дБ (A, отн.)"),
            new AnalyzerReadout("dB(C)", levelDbC, "дБ (C, отн.)"),
            new AnalyzerReadout("Пик", peakDbFs, "дБFS (ампл.)"),
            new AnalyzerReadout("Крест-фактор", crestFactor, string.Empty)
        };
    }
}
