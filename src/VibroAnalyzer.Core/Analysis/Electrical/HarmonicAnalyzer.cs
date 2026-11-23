using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Electrical;

/// <summary>
/// Гармонический анализ: уровни первых десяти гармоник, THD и THD+N.
/// </summary>
public sealed class HarmonicAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 16384;
    private const int HarmonicCount = 10;
    private const double MinLevelDb = -240.0;

    // Эквивалентная шумовая полоса окна Ханна.
    private const double HannEnbw = 1.5;

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _fundamentalFrequencyHz;
    private double _thdPercent;
    private double _thdPlusNoisePercent;
    private double _significantHarmonics;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Гармоники и THD";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Электрика";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Находит основной тон сигнала и измеряет уровни первых десяти гармоник. По ним "
        + "рассчитываются коэффициенты THD и THD+N, что позволяет оценить нелинейные искажения "
        + "генераторов, усилителей и электрической сети.";

    /// <summary>Частота основного тона, Гц.</summary>
    public double FundamentalFrequencyHz => Volatile.Read(ref _fundamentalFrequencyHz);

    /// <summary>Коэффициент гармонических искажений, %.</summary>
    public double ThdPercent => Volatile.Read(ref _thdPercent);

    /// <summary>Коэффициент искажений с шумом, %.</summary>
    public double ThdPlusNoisePercent => Volatile.Read(ref _thdPlusNoisePercent);

    /// <summary>Число значимых гармоник (выше −40 дБ относительно основной).</summary>
    public double SignificantHarmonics => Volatile.Read(ref _significantHarmonics);

    /// <inheritdoc />
    public string XLabel => "Номер гармоники";

    /// <inheritdoc />
    public string YLabel => "Уровень, дБFS (ампл.)";

    /// <inheritdoc />
    public double[] X => _x;

    /// <inheritdoc />
    public double[] Y => _y;

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

        var harmonicNumbers = CreateHarmonicAxis();
        var levels = CreateEmptyLevels();

        if (samples.Length == 0)
        {
            _x = harmonicNumbers;
            _y = levels;
            Publish(0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var result = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
        var magnitudes = result.Magnitudes;
        var magnitudesDb = result.MagnitudesDb;

        if (magnitudes.Length < 3)
        {
            _x = harmonicNumbers;
            _y = levels;
            Publish(0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var (peakFrequency, peakLevel) =
            FftProcessor.FindPeak(magnitudesDb, sampleRate, result.FftSize);

        if (!double.IsFinite(peakFrequency)
            || peakFrequency <= 0.0
            || peakLevel <= MinLevelDb + 1.0)
        {
            _x = harmonicNumbers;
            _y = levels;
            Publish(0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var binWidth = (double)sampleRate / result.FftSize;
        var peakBin = (int)Math.Round(peakFrequency / binWidth);
        var (_, peakAmplitude) = FindLocalPeak(magnitudes, peakBin, 3);
        var fundamentalFrequency = peakFrequency;
        var fundamentalBin = peakBin;
        var fundamentalAmplitude = peakAmplitude;

        // Если самый сильный пик — 2x/3x/4x/5x, ищем значимую субгармонику.
        // Это защищает ТХД от сигналов с сильной второй/третьей гармоникой.
        if (peakAmplitude > 1e-12)
        {
            for (var divisor = 2; divisor <= 5; divisor++)
            {
                var candidate = peakFrequency / divisor;
                if (candidate < 20.0)
                {
                    continue;
                }

                var candidateBin = (int)Math.Round(candidate / binWidth);
                var (_, candidateAmplitude) = FindLocalPeak(magnitudes, candidateBin, 3);

                if (candidateAmplitude > peakAmplitude * 0.05)
                {
                    fundamentalFrequency = candidate;
                    fundamentalBin = candidateBin;
                    fundamentalAmplitude = candidateAmplitude;
                    break;
                }
            }
        }

        if (fundamentalAmplitude <= 1e-12)
        {
            _x = harmonicNumbers;
            _y = levels;
            Publish(0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var harmonics = new double[HarmonicCount];

        for (var h = 1; h <= HarmonicCount; h++)
        {
            var frequency = fundamentalFrequency * h;

            if (frequency >= sampleRate / 2.0)
            {
                harmonics[h - 1] = 0.0;
                levels[h - 1] = MinLevelDb;
                continue;
            }

            var centerBin = (int)Math.Round(frequency / binWidth);
            var (_, amplitude) = FindLocalPeak(magnitudes, centerBin, 3);
            harmonics[h - 1] = amplitude;
            levels[h - 1] = SignalMath.DbFs(amplitude);
        }

        // Основную гармонику берём из найденного пика, чтобы не зависеть от округления бина.
        harmonics[0] = fundamentalAmplitude;
        levels[0] = SignalMath.DbFs(fundamentalAmplitude);

        var harmonicPower = 0.0;
        for (var h = 1; h < HarmonicCount; h++)
        {
            harmonicPower += harmonics[h] * harmonics[h];
        }

        var thd = fundamentalAmplitude > 1e-12
            ? Math.Sqrt(harmonicPower) / fundamentalAmplitude * 100.0
            : 0.0;

        var totalPower = SumPower(magnitudes, 1, magnitudes.Length - 1);
        var fundamentalPower = SumPower(
            magnitudes,
            Math.Max(1, fundamentalBin - 4),
            Math.Min(magnitudes.Length - 1, fundamentalBin + 4));

        // THD+N: вычитаем не один пиковый бин, а весь главный лепесток
        // основной частоты. Иначе остаток окна засчитывается как шум.
        var noisePower = Math.Max(0.0, totalPower - fundamentalPower);
        var thdPlusNoise = fundamentalPower > 1e-24
            ? Math.Sqrt(noisePower / fundamentalPower) * 100.0
            : 0.0;

        var significant = 0;
        for (var h = 1; h < HarmonicCount; h++)
        {
            if (harmonics[h] > fundamentalAmplitude * 0.01)
            {
                significant++;
            }
        }

        _x = harmonicNumbers;
        _y = levels;

        Publish(fundamentalFrequency, thd, thdPlusNoise, significant);
        MarkUpdated();
    }

    private static double SumPower(double[] magnitudes, int start, int end)
    {
        var sum = 0.0;

        for (var i = Math.Max(1, start); i <= end && i < magnitudes.Length; i++)
        {
            sum += magnitudes[i] * magnitudes[i];
        }

        return sum;
    }

    private static double[] CreateHarmonicAxis()
    {
        var axis = new double[HarmonicCount];
        for (var i = 0; i < axis.Length; i++)
        {
            axis[i] = i + 1;
        }

        return axis;
    }

    private static double[] CreateEmptyLevels()
    {
        var levels = new double[HarmonicCount];
        Array.Fill(levels, MinLevelDb);
        return levels;
    }

    private static (int Bin, double Amplitude) FindLocalPeak(double[] magnitudes, int centerBin, int radius)
    {
        var start = Math.Clamp(centerBin - radius, 1, magnitudes.Length - 1);
        var end = Math.Clamp(centerBin + radius, start, magnitudes.Length - 1);

        var peakBin = start;
        var peak = magnitudes[start];

        for (var i = start + 1; i <= end; i++)
        {
            if (magnitudes[i] > peak)
            {
                peak = magnitudes[i];
                peakBin = i;
            }
        }

        return (peakBin, peak);
    }

    private void Publish(
        double fundamentalFrequencyHz,
        double thdPercent,
        double thdPlusNoisePercent,
        double significantHarmonics)
    {
        Volatile.Write(ref _fundamentalFrequencyHz, fundamentalFrequencyHz);
        Volatile.Write(ref _thdPercent, thdPercent);
        Volatile.Write(ref _thdPlusNoisePercent, thdPlusNoisePercent);
        Volatile.Write(ref _significantHarmonics, significantHarmonics);

        _readouts = new[]
        {
            new AnalyzerReadout("Основная частота", fundamentalFrequencyHz, "Гц"),
            new AnalyzerReadout("THD", thdPercent, "%"),
            new AnalyzerReadout("THD+N", thdPlusNoisePercent, "%"),
            new AnalyzerReadout("Значимых гармоник", significantHarmonics, string.Empty)
        };
    }
}
