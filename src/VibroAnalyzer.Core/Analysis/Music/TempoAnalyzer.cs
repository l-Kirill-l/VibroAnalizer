using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Music;

/// <summary>
/// Оценка темпа по огибающей и найденным атакам (onsets).
/// </summary>
public sealed class TempoAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 16384;
    private const int FrameSize = 512;
    private const int HopSize = 256;
    private const double MinBeatIntervalSeconds = 0.15;
    private const double MinimumDurationSeconds = 0.15;

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _bpm;
    private double _beatCount;
    private double _averageIntervalSeconds;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Темп и биты";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Музыка";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Строит огибающую по коротким кадрам, выделяет фронты (атаки) и по интервалам между ними "
        + "оценивает темп в BPM. Подходит для ритмичной музыки, ударных и механических импульсов; "
        + "на слишком коротком фрагменте темп не определяется.";

    /// <summary>Оценка темпа, ударов в минуту.</summary>
    public double Bpm => Volatile.Read(ref _bpm);

    /// <summary>Число найденных битов.</summary>
    public double BeatCount => Volatile.Read(ref _beatCount);

    /// <summary>Средний интервал между битами, с.</summary>
    public double AverageIntervalSeconds => Volatile.Read(ref _averageIntervalSeconds);

    /// <inheritdoc />
    public string XLabel => "Время, с";

    /// <inheritdoc />
    public string YLabel => "Огибающая";

    /// <inheritdoc />
    public double[] X => _x;

    /// <inheritdoc />
    public double[] Y => _y;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(WindowSize, sampleRate * 4);

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var count = samples.Length;
        if (count < FrameSize * 2 || count < sampleRate * MinimumDurationSeconds)
        {
            _x = Array.Empty<double>();
            _y = Array.Empty<double>();
            Publish(0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var frameCount = (count - FrameSize) / HopSize + 1;
        var times = new double[frameCount];
        var envelope = new double[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var start = frame * HopSize;
            var sum = 0.0;

            for (var i = 0; i < FrameSize; i++)
            {
                var value = samples[start + i];
                sum += value * value;
            }

            envelope[frame] = Math.Sqrt(sum / FrameSize);
            times[frame] = (start + FrameSize / 2.0) / sampleRate;
        }

        // Новизна — положительный прирост энергии между кадрами.
        var novelty = new double[frameCount];
        var noveltySum = 0.0;

        for (var frame = 1; frame < frameCount; frame++)
        {
            var difference = envelope[frame] - envelope[frame - 1];
            novelty[frame] = difference > 0.0 ? difference : 0.0;
            noveltySum += novelty[frame];
        }

        var noveltyMean = frameCount > 1 ? noveltySum / (frameCount - 1) : 0.0;

        var noveltyVariance = 0.0;
        for (var frame = 1; frame < frameCount; frame++)
        {
            var deviation = novelty[frame] - noveltyMean;
            noveltyVariance += deviation * deviation;
        }

        noveltyVariance /= Math.Max(1, frameCount - 1);
        var threshold = noveltyMean + 0.8 * Math.Sqrt(noveltyVariance);

        var beats = new List<double>();
        var minimumDistanceFrames = Math.Max(1, (int)Math.Round(MinBeatIntervalSeconds * sampleRate / HopSize));
        var lastBeatFrame = -minimumDistanceFrames;

        if (threshold > 1e-12)
        {
            for (var frame = 1; frame < frameCount - 1; frame++)
            {
                var value = novelty[frame];

                if (value < threshold)
                {
                    continue;
                }

                if (value < novelty[frame - 1] || value < novelty[frame + 1])
                {
                    continue;
                }

                if (frame - lastBeatFrame < minimumDistanceFrames)
                {
                    continue;
                }

                beats.Add(times[frame]);
                lastBeatFrame = frame;
            }
        }

        var bpm = 0.0;
        var averageInterval = 0.0;

        if (beats.Count >= 2)
        {
            var intervals = new double[beats.Count - 1];
            var intervalSum = 0.0;

            for (var i = 1; i < beats.Count; i++)
            {
                var interval = beats[i] - beats[i - 1];
                intervals[i - 1] = interval;
                intervalSum += interval;
            }

            averageInterval = intervalSum / intervals.Length;

            var sorted = new double[intervals.Length];
            Array.Copy(intervals, sorted, intervals.Length);
            Array.Sort(sorted);

            var median = sorted.Length % 2 == 0
                ? 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2])
                : sorted[sorted.Length / 2];

            if (median > 1e-9)
            {
                bpm = 60.0 / median;
            }
        }

        _x = times;
        _y = envelope;

        Publish(bpm, beats.Count, averageInterval);
        MarkUpdated();
    }

    private void Publish(double bpm, double beatCount, double averageIntervalSeconds)
    {
        Volatile.Write(ref _bpm, bpm);
        Volatile.Write(ref _beatCount, beatCount);
        Volatile.Write(ref _averageIntervalSeconds, averageIntervalSeconds);

        _readouts = new[]
        {
            new AnalyzerReadout("BPM", bpm, string.Empty),
            new AnalyzerReadout("Число битов", beatCount, string.Empty),
            new AnalyzerReadout("Средний интервал", averageIntervalSeconds, "с")
        };
    }
}
