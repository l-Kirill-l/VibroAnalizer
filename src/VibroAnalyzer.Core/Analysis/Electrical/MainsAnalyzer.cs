using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Electrical;

/// <summary>
/// Анализ сети 50/60 Гц: уровень основной частоты, её гармоники и THD.
/// </summary>
public sealed class MainsAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized
{
    private const int WindowSize = 16384;
    private const int HarmonicCount = 40;
    private const double MinLevelDb = -240.0;

    private readonly AnalyzerParameter[] _parameters;

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _frequencyHz = 50.0;
    private double _fundamentalLevelDb;
    private double _mainsFrequencyHz;
    private double _thdPercent;
    private double _thirdHarmonicDb;
    private double _fifthHarmonicDb;

    public MainsAnalyzer()
    {
        _parameters = new[]
        {
            new AnalyzerParameter(
                "Частота сети",
                50.0,
                60.0,
                () => FrequencyHz,
                value => FrequencyHz = value,
                "Гц")
        };
    }

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Сеть 50/60 Гц";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Электрика";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Измеряет фактическую частоту сети и уровни её первых гармоник, включая третью и пятую. "
        + "По соотношению гармоник рассчитывается THD, что помогает обнаружить нелинейные нагрузки "
        + "и проблемы с питанием.";

    /// <summary>Номинальная частота сети, Гц.</summary>
    public double FrequencyHz
    {
        get => Volatile.Read(ref _frequencyHz);
        set => Volatile.Write(ref _frequencyHz, Math.Clamp(value, 50.0, 60.0));
    }

    /// <summary>Уровень основной гармоники, дБFS.</summary>
    public double FundamentalLevelDb => Volatile.Read(ref _fundamentalLevelDb);

    /// <summary>Измеренная частота сети, Гц.</summary>
    public double MainsFrequencyHz => Volatile.Read(ref _mainsFrequencyHz);

    /// <summary>Коэффициент гармонических искажений, %.</summary>
    public double ThdPercent => Volatile.Read(ref _thdPercent);

    /// <summary>Уровень третьей гармоники, дБFS.</summary>
    public double ThirdHarmonicDb => Volatile.Read(ref _thirdHarmonicDb);

    /// <summary>Уровень пятой гармоники, дБFS.</summary>
    public double FifthHarmonicDb => Volatile.Read(ref _fifthHarmonicDb);

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => _parameters;

    /// <inheritdoc />
    public string XLabel => "Номер гармоники";

    /// <inheritdoc />
    public string YLabel => "Уровень, дБFS";

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
        var nominal = FrequencyHz;

        if (samples.Length == 0)
        {
            _x = harmonicNumbers;
            _y = levels;
            Publish(MinLevelDb, nominal, 0.0, MinLevelDb, MinLevelDb);
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
            Publish(MinLevelDb, nominal, 0.0, MinLevelDb, MinLevelDb);
            MarkUpdated();
            return;
        }

        var binWidth = (double)sampleRate / result.FftSize;
        var nyquist = sampleRate / 2.0;

        // Ищем сетевую частоту в окне ±2 Гц вокруг номинала.
        var searchRadius = Math.Max(1, (int)Math.Round(2.0 / binWidth));
        var centerBin = (int)Math.Round(nominal / binWidth);
        var start = Math.Clamp(centerBin - searchRadius, 1, magnitudes.Length - 1);
        var end = Math.Clamp(centerBin + searchRadius, start, magnitudes.Length - 1);

        var peakBin = start;
        var peakAmplitude = magnitudes[start];

        for (var i = start + 1; i <= end; i++)
        {
            if (magnitudes[i] > peakAmplitude)
            {
                peakAmplitude = magnitudes[i];
                peakBin = i;
            }
        }

        var mainsFrequency = nominal;

        if (peakAmplitude <= 1e-9)
        {
            _x = harmonicNumbers;
            _y = levels;
            Publish(MinLevelDb, 0.0, 0.0, MinLevelDb, MinLevelDb);
            MarkUpdated();
            return;
        }

        {
            var y0 = magnitudesDb[Math.Max(1, peakBin - 1)];
            var y1 = magnitudesDb[peakBin];
            var y2 = magnitudesDb[Math.Min(magnitudesDb.Length - 1, peakBin + 1)];
            var denominator = y0 - 2.0 * y1 + y2;
            var delta = 0.0;

            if (Math.Abs(denominator) > 1e-12 && double.IsFinite(denominator))
            {
                delta = Math.Clamp(0.5 * (y0 - y2) / denominator, -0.5, 0.5);
            }

            mainsFrequency = Math.Clamp((peakBin + delta) * binWidth, 1.0, nyquist);
        }

        var harmonicAmplitudes = new double[HarmonicCount];

        for (var h = 1; h <= HarmonicCount; h++)
        {
            var frequency = mainsFrequency * h;

            if (frequency >= nyquist)
            {
                levels[h - 1] = MinLevelDb;
                continue;
            }

            var harmonicBin = (int)Math.Round(frequency / binWidth);
            var (_, amplitude) = FindLocalPeak(magnitudes, harmonicBin, 3);
            harmonicAmplitudes[h - 1] = amplitude;
            levels[h - 1] = SignalMath.DbFs(amplitude);
        }

        harmonicAmplitudes[0] = peakAmplitude;
        levels[0] = SignalMath.DbFs(peakAmplitude);

        var harmonicPower = 0.0;
        for (var h = 1; h < HarmonicCount; h++)
        {
            harmonicPower += harmonicAmplitudes[h] * harmonicAmplitudes[h];
        }

        var thd = peakAmplitude > 1e-12
            ? Math.Sqrt(harmonicPower) / peakAmplitude * 100.0
            : 0.0;

        var third = HarmonicCount >= 3 ? levels[2] : MinLevelDb;
        var fifth = HarmonicCount >= 5 ? levels[4] : MinLevelDb;

        _x = harmonicNumbers;
        _y = levels;

        Publish(levels[0], mainsFrequency, thd, third, fifth);
        MarkUpdated();
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
        double fundamentalLevelDb,
        double mainsFrequencyHz,
        double thdPercent,
        double thirdHarmonicDb,
        double fifthHarmonicDb)
    {
        Volatile.Write(ref _fundamentalLevelDb, fundamentalLevelDb);
        Volatile.Write(ref _mainsFrequencyHz, mainsFrequencyHz);
        Volatile.Write(ref _thdPercent, thdPercent);
        Volatile.Write(ref _thirdHarmonicDb, thirdHarmonicDb);
        Volatile.Write(ref _fifthHarmonicDb, fifthHarmonicDb);

        _readouts = new[]
        {
            new AnalyzerReadout("Уровень основной", fundamentalLevelDb, "дБFS (ампл.)"),
            new AnalyzerReadout("Частота сети", mainsFrequencyHz, "Гц"),
            new AnalyzerReadout("THD", thdPercent, "%"),
            new AnalyzerReadout("3-я гармоника", thirdHarmonicDb, "дБFS (ампл.)"),
            new AnalyzerReadout("5-я гармоника", fifthHarmonicDb, "дБFS (ампл.)")
        };
    }
}
