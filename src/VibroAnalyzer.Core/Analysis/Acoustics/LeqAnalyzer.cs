using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Acoustics;

/// <summary>
/// Эквивалентный непрерывный уровень Leq за текущее окно анализа
/// с частотной коррекцией A и C по IEC 61672.
/// </summary>
public sealed class LeqAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 48000;
    private const double HannEnbw = 1.5;
    private const double MinLevelDb = -240.0;

    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private long _lastProcessTicks;
    private double _leqDbFs;
    private double _laEqDb;
    private double _lcEqDb;
    private double _peakDbFs;
    private double _durationSeconds;

    /// <inheritdoc />
    public override string Title => "Эквивалентный уровень (Leq)";

    /// <inheritdoc />
    public override string Category => "Акустика";

    /// <inheritdoc />
    public override string Description =>
        "Рассчитывает эквивалентный непрерывный уровень Leq за текущее окно: "
        + "энергетическое среднее, а не среднее децибел. Дополнительно выдаёт LAeq и LCeq "
        + "с частотной коррекцией A и C по IEC 61672.";

    /// <summary>Leq без коррекции, дБFS.</summary>
    public double LeqDbFs => Volatile.Read(ref _leqDbFs);

    /// <summary>Leq с коррекцией A, дБ(A, отн.).</summary>
    public double LaEqDb => Volatile.Read(ref _laEqDb);

    /// <summary>Leq с коррекцией C, дБ(C, отн.).</summary>
    public double LcEqDb => Volatile.Read(ref _lcEqDb);

    /// <summary>Пиковый уровень окна, дБFS.</summary>
    public double PeakDbFs => Volatile.Read(ref _peakDbFs);

    /// <summary>Длительность окна, с.</summary>
    public double DurationSeconds => Volatile.Read(ref _durationSeconds);

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
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowSize, sampleRate);

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
            Publish(MinLevelDb, MinLevelDb, MinLevelDb, MinLevelDb, 0.0);
            MarkUpdated();
            return;
        }

        var spectrum = FftProcessor.Compute(
            samples,
            sampleRate,
            WindowKind.Hann,
            removeDc: true);
        var frequencies = spectrum.Frequencies;
        var magnitudes = spectrum.Magnitudes;

        var totalPower = 0.0;
        var powerA = 0.0;
        var powerC = 0.0;

        for (var bin = 1; bin < magnitudes.Length; bin++)
        {
            var magnitude = magnitudes[bin];
            var power = magnitude * magnitude;
            totalPower += power;

            var frequency = frequencies[bin];
            var gainA = WeightingGainA(frequency);
            var gainC = WeightingGainC(frequency);
            powerA += power * gainA * gainA;
            powerC += power * gainC * gainC;
        }

        // БПФ дополняется нулями до степени двойки. Сумма квадратов
        // амплитуд по более частой сетке пропорциональна FftSize/N,
        // поэтому нормируем обратно к длине исходного окна.
        var paddingScale = spectrum.FftSize > 0
            ? (double)samples.Length / spectrum.FftSize
            : 1.0;

        var rms = Math.Sqrt(totalPower * paddingScale / (2.0 * HannEnbw));
        var rmsA = Math.Sqrt(powerA * paddingScale / (2.0 * HannEnbw));
        var rmsC = Math.Sqrt(powerC * paddingScale / (2.0 * HannEnbw));
        var peak = SignalMath.Peak(samples);
        var duration = (double)samples.Length / sampleRate;

        Publish(
            SignalMath.DbFs(rms),
            SignalMath.DbFs(rmsA),
            SignalMath.DbFs(rmsC),
            SignalMath.DbFs(peak),
            duration);
        MarkUpdated();
    }

    private void Publish(
        double leqDbFs,
        double laEqDb,
        double lcEqDb,
        double peakDbFs,
        double durationSeconds)
    {
        Volatile.Write(ref _leqDbFs, leqDbFs);
        Volatile.Write(ref _laEqDb, laEqDb);
        Volatile.Write(ref _lcEqDb, lcEqDb);
        Volatile.Write(ref _peakDbFs, peakDbFs);
        Volatile.Write(ref _durationSeconds, durationSeconds);

        _readouts = new[]
        {
            new AnalyzerReadout("Leq окна", leqDbFs, "дБFS"),
            new AnalyzerReadout("LAeq окна", laEqDb, "дБ(A, отн.)"),
            new AnalyzerReadout("LCeq окна", lcEqDb, "дБ(C, отн.)"),
            new AnalyzerReadout("Пик", peakDbFs, "дБFS"),
            new AnalyzerReadout("Длительность", durationSeconds, "с"),
        };
    }

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
}
