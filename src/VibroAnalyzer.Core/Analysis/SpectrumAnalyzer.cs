using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis;

/// <summary>
/// Анализатор спектра. Спектр вычисляется через <see cref="FftProcessor"/>,
/// при необходимости сглаживается экспоненциальным усреднением.
/// </summary>
public sealed class SpectrumAnalyzer : AnalyzerBase
{
    private const int MeanNoiseFloorThreshold = 16;

    private volatile double[] _frequencies = Array.Empty<double>();
    private volatile double[] _magnitudesDb = Array.Empty<double>();
    private double _peakFrequencyHz;
    private double _peakLevelDb;
    private double _noiseFloorDb;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Спектр";

    /// <summary>Размер быстрого преобразования Фурье, отсчётов.</summary>
    public int FftSize { get; set; } = 8192;

    /// <summary>Весовая функция окна.</summary>
    public WindowKind Window { get; set; } = WindowKind.Hann;

    /// <summary>
    /// Коэффициент экспоненциального усреднения (0…1). Значения вне диапазона
    /// отключают усреднение.
    /// </summary>
    public double AveragingFactor { get; set; } = 0.2;

    /// <summary>Частоты бинов спектра, Гц.</summary>
    public double[] Frequencies => _frequencies;

    /// <summary>Уровни бинов спектра, дБ.</summary>
    public double[] MagnitudesDb => _magnitudesDb;

    /// <summary>Частота максимума спектра, Гц.</summary>
    public double PeakFrequencyHz => Volatile.Read(ref _peakFrequencyHz);

    /// <summary>Уровень максимума спектра, дБ.</summary>
    public double PeakLevelDb => Volatile.Read(ref _peakLevelDb);

    /// <summary>Оценка уровня шумовой дорожки, дБ.</summary>
    public double NoiseFloorDb => Volatile.Read(ref _noiseFloorDb);

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate) => FftSize;

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var result = FftProcessor.Compute(samples, sampleRate, Window);
        var current = result.MagnitudesDb;
        var alpha = AveragingFactor;
        var previous = _magnitudesDb;

        double[] magnitudesDb;
        if (alpha <= 0 || alpha >= 1 || previous.Length != current.Length)
        {
            // Усреднение отключено или изменился размер спектра — начинаем заново.
            magnitudesDb = current;
        }
        else
        {
            magnitudesDb = new double[current.Length];
            for (var i = 0; i < magnitudesDb.Length; i++)
            {
                magnitudesDb[i] = alpha * current[i] + (1 - alpha) * previous[i];
            }
        }

        var (peakFrequencyHz, peakLevelDb) =
            FftProcessor.FindPeak(magnitudesDb, result.SampleRate, result.FftSize);

        var noiseFloorDb = CalculateNoiseFloor(magnitudesDb);

        // Новые массивы публикуются целиком: UI читает их из другого потока.
        _frequencies = result.Frequencies;
        _magnitudesDb = magnitudesDb;
        Volatile.Write(ref _peakFrequencyHz, peakFrequencyHz);
        Volatile.Write(ref _peakLevelDb, peakLevelDb);
        Volatile.Write(ref _noiseFloorDb, noiseFloorDb);

        MarkUpdated();
    }

    /// <summary>
    /// Оценивает шумовую дорожку как медиану нижних 50% бинов. Перед этим
    /// отбрасываются крайние 5% самых громких бинов. Для малого числа бинов
    /// используется среднее.
    /// </summary>
    private static double CalculateNoiseFloor(double[] magnitudesDb)
    {
        if (magnitudesDb.Length == 0)
        {
            return 0.0;
        }

        var sorted = new double[magnitudesDb.Length];
        Array.Copy(magnitudesDb, sorted, magnitudesDb.Length);
        Array.Sort(sorted);

        // Отбрасываем крайние 5% сверху, затем берём нижнюю половину оставшихся.
        var usable = Math.Max(1, (int)Math.Floor(sorted.Length * 0.95));
        var count = Math.Max(1, usable / 2);

        if (count < MeanNoiseFloorThreshold)
        {
            var sum = 0.0;
            for (var i = 0; i < count; i++)
            {
                sum += sorted[i];
            }

            return sum / count;
        }

        var middle = count / 2;
        return count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }
}
