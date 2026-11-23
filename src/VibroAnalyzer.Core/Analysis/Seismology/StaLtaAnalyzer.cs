namespace VibroAnalyzer.Core.Analysis.Seismology;

/// <summary>
/// STA/LTA триггер: отношение короткого и длинного средних энергии сигнала.
/// </summary>
public sealed class StaLtaAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized
{
    private const int WindowFrames = 8192;
    private const double MaxRatio = 1000.0;

    private volatile double[] _times = Array.Empty<double>();
    private volatile double[] _ratios = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();

    private bool _wasTriggered;
    private int _eventCount;

    /// <summary>Длительность короткого окна, с.</summary>
    public double ShortSeconds { get; set; } = 0.5;

    /// <summary>Длительность длинного окна, с.</summary>
    public double LongSeconds { get; set; } = 5.0;

    /// <summary>Порог отношения STA/LTA для срабатывания.</summary>
    public double TriggerRatio { get; set; } = 3.0;

    /// <inheritdoc />
    public override string Title => "STA/LTA триггер";

    /// <inheritdoc />
    public override string Category => "Сейсмология";

    /// <inheritdoc />
    public override string Description =>
        "Считает отношение средних энергий в коротком и длинном окнах (STA/LTA). "
        + "Резкий рост отношения отмечает приход сейсмического события, а гистерезис "
        + "не даёт многократно считать один и тот же импульс.";

    /// <inheritdoc />
    public string XLabel => "Время, с";

    /// <inheritdoc />
    public string YLabel => "STA/LTA";

    /// <inheritdoc />
    public double[] X => _times;

    /// <inheritdoc />
    public double[] Y => _ratios;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => new AnalyzerParameter[]
    {
        new("Короткое окно", 0.1, 10.0, () => ShortSeconds,
            value => ShortSeconds = Sanitize(value, 0.1, 10.0, 0.5), "с"),
        new("Длинное окно", 0.5, 60.0, () => LongSeconds,
            value => LongSeconds = Sanitize(value, 0.5, 60.0, 5.0), "с"),
        new("Порог", 1.5, 10.0, () => TriggerRatio,
            value => TriggerRatio = Sanitize(value, 1.5, 10.0, 3.0), ""),
    };

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
    {
        // Длинное окно LTA должно физически помещаться в массив.
        var seconds = Math.Clamp(Math.Max(10.0, LongSeconds * 2.0), 10.0, 20.0);
        return Math.Max(WindowFrames, (int)Math.Round(seconds * sampleRate));
    }

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        if (samples is null || samples.Length < 2 || sampleRate <= 0)
        {
            _times = Array.Empty<double>();
            _ratios = Array.Empty<double>();
            Publish(0.0, false);
            MarkUpdated();
            return;
        }

        var count = samples.Length;
        _wasTriggered = false;
        _eventCount = 0;

        var prefix = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            prefix[i + 1] = prefix[i] + samples[i] * samples[i];
        }

        var longCount = Math.Clamp((int)Math.Round(LongSeconds * sampleRate), 2, count);
        var shortCount = Math.Clamp((int)Math.Round(ShortSeconds * sampleRate), 1, longCount);
        if (shortCount >= longCount)
        {
            shortCount = Math.Max(1, longCount - 1);
        }

        var ratios = new double[count];
        var times = new double[count];
        var threshold = TriggerRatio;
        var release = threshold * 0.5;

        for (var i = 0; i < count; i++)
        {
            var end = i + 1;
            var shortStart = Math.Max(0, end - shortCount);
            var longStart = Math.Max(0, end - longCount);

            var sta = (prefix[end] - prefix[shortStart]) / (end - shortStart);
            var lta = (prefix[end] - prefix[longStart]) / (end - longStart);

            var ratio = lta > 1e-15 ? sta / lta : 0.0;
            if (!double.IsFinite(ratio) || ratio < 0.0)
            {
                ratio = 0.0;
            }

            ratios[i] = Math.Min(ratio, MaxRatio);
            times[i] = (double)i / sampleRate;

            if (!_wasTriggered && ratios[i] >= threshold)
            {
                _wasTriggered = true;
                _eventCount++;
            }
            else if (_wasTriggered && ratios[i] < release)
            {
                _wasTriggered = false;
            }
        }

        _times = times;
        _ratios = ratios;
        Publish(ratios[^1], _wasTriggered);
        MarkUpdated();
    }

    private void Publish(double currentRatio, bool triggered)
    {
        _readouts = new[]
        {
            new AnalyzerReadout("Текущее отношение", currentRatio, ""),
            new AnalyzerReadout("Триггер", triggered ? 1.0 : 0.0, ""),
            new AnalyzerReadout("Событий в окне", _eventCount, "шт."),
        };
    }

    private static double Sanitize(double value, double minimum, double maximum, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}
