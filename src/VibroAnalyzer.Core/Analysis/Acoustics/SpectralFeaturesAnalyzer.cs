using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Acoustics;

/// <summary>
/// Спектральные признаки: центроид, rolloff, плоскостность, ширина полосы и наклон.
/// </summary>
public sealed class SpectralFeaturesAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 8192;
    private const double PowerFloor = 1e-24;

    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _centroidHz;
    private double _rolloffHz;
    private double _flatness;
    private double _bandwidthHz;
    private double _slopeDbPerKhz;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Спектральные признаки";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Акустика";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Вычисляет компактные признаки спектра: центроид, частоту спада 85 % мощности, "
        + "спектральную плоскостность, ширину полосы и наклон в дБ/кГц. Эти признаки удобны "
        + "для классификации звуков и автоматического различения тонов, шума и импульсов.";

    /// <summary>Спектральный центроид, Гц.</summary>
    public double CentroidHz => Volatile.Read(ref _centroidHz);

    /// <summary>Частота, ниже которой сосредоточено 85 % мощности, Гц.</summary>
    public double RolloffHz => Volatile.Read(ref _rolloffHz);

    /// <summary>Спектральная плоскостность 0…1.</summary>
    public double Flatness => Volatile.Read(ref _flatness);

    /// <summary>Спектральная ширина полосы, Гц.</summary>
    public double BandwidthHz => Volatile.Read(ref _bandwidthHz);

    /// <summary>Наклон спектра, дБ/кГц.</summary>
    public double SlopeDbPerKhz => Volatile.Read(ref _slopeDbPerKhz);

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
            Publish(0.0, 0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var result = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
        var frequencies = result.Frequencies;
        var magnitudes = result.Magnitudes;
        var magnitudesDb = result.MagnitudesDb;

        var totalPower = 0.0;
        var weightedFrequency = 0.0;
        var logPowerSum = 0.0;
        var usedBins = 0;

        for (var k = 1; k < magnitudes.Length; k++)
        {
            var power = magnitudes[k] * magnitudes[k];
            totalPower += power;
            weightedFrequency += frequencies[k] * power;
            logPowerSum += Math.Log(Math.Max(power, PowerFloor));
            usedBins++;
        }

        if (totalPower <= PowerFloor || usedBins == 0)
        {
            Publish(0.0, 0.0, 0.0, 0.0, 0.0);
            MarkUpdated();
            return;
        }

        var centroid = weightedFrequency / totalPower;

        var rolloffThreshold = 0.85 * totalPower;
        var cumulative = 0.0;
        var rolloff = 0.0;

        for (var k = 1; k < magnitudes.Length; k++)
        {
            cumulative += magnitudes[k] * magnitudes[k];
            if (cumulative >= rolloffThreshold)
            {
                rolloff = frequencies[k];
                break;
            }
        }

        var variance = 0.0;
        for (var k = 1; k < magnitudes.Length; k++)
        {
            var difference = frequencies[k] - centroid;
            variance += magnitudes[k] * magnitudes[k] * difference * difference;
        }

        var bandwidth = Math.Sqrt(variance / totalPower);

        var geometricMean = Math.Exp(logPowerSum / usedBins);
        var arithmeticMean = totalPower / usedBins;
        var flatness = Math.Clamp(geometricMean / arithmeticMean, 0.0, 1.0);

        var slope = flatness < 0.05
            ? 0.0
            : CalculateSlope(frequencies, magnitudesDb, magnitudes, totalPower);

        Publish(centroid, rolloff, flatness, bandwidth, slope);
        MarkUpdated();
    }

    private static double CalculateSlope(
        double[] frequencies,
        double[] magnitudesDb,
        double[] magnitudes,
        double totalPower)
    {
        // Регрессия только по значимым бинам: шумовой пол −240 дБ
        // иначе полностью определяет наклон для тональных сигналов.
        var powerThreshold = totalPower * 1e-6;
        var count = 0;
        var meanX = 0.0;
        var meanY = 0.0;

        for (var k = 1; k < magnitudes.Length; k++)
        {
            if (magnitudes[k] * magnitudes[k] < powerThreshold)
            {
                continue;
            }

            meanX += frequencies[k] / 1000.0;
            meanY += magnitudesDb[k];
            count++;
        }

        if (count < 5)
        {
            return 0.0;
        }

        meanX /= count;
        meanY /= count;

        var covariance = 0.0;
        var varianceX = 0.0;

        for (var k = 1; k < magnitudes.Length; k++)
        {
            if (magnitudes[k] * magnitudes[k] < powerThreshold)
            {
                continue;
            }

            var dx = frequencies[k] / 1000.0 - meanX;
            covariance += dx * (magnitudesDb[k] - meanY);
            varianceX += dx * dx;
        }

        return varianceX > 1e-24 ? covariance / varianceX : 0.0;
    }

    private void Publish(
        double centroidHz,
        double rolloffHz,
        double flatness,
        double bandwidthHz,
        double slopeDbPerKhz)
    {
        Volatile.Write(ref _centroidHz, centroidHz);
        Volatile.Write(ref _rolloffHz, rolloffHz);
        Volatile.Write(ref _flatness, flatness);
        Volatile.Write(ref _bandwidthHz, bandwidthHz);
        Volatile.Write(ref _slopeDbPerKhz, slopeDbPerKhz);

        _readouts = new[]
        {
            new AnalyzerReadout("Центроид", centroidHz, "Гц"),
            new AnalyzerReadout("Rolloff 85 %", rolloffHz, "Гц"),
            new AnalyzerReadout("Плоскостность", flatness, string.Empty),
            new AnalyzerReadout("Спектральное СКО", bandwidthHz, "Гц"),
            new AnalyzerReadout("Наклон", slopeDbPerKhz, "дБ/кГц")
        };
    }
}
