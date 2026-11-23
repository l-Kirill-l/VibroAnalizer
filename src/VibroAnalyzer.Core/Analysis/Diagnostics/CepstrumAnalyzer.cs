using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Diagnostics;

/// <summary>
/// Кепстральный анализ: обратное БПФ от логарифма амплитудного спектра.
/// </summary>
public sealed class CepstrumAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 8192;
    private const double MagnitudeFloor = 1e-12;

    private volatile double[] _x = Array.Empty<double>();
    private volatile double[] _y = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private double _dominantQuefrencyMs;
    private double _dominantFrequencyHz;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Кепстр";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Диагностика";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Вычисляет действительный кепстр как обратное БПФ от log|БПФ|. Пики кепстра указывают "
        + "на периодичность спектра: доминирующая кефренция соответствует периоду основного тона "
        + "и помогает диагностировать гармонические и модуляционные процессы.";

    /// <summary>Доминирующая кефренция, мс.</summary>
    public double DominantQuefrencyMs => Volatile.Read(ref _dominantQuefrencyMs);

    /// <summary>Частота, соответствующая доминирующей кефренции, Гц.</summary>
    public double DominantFrequencyHz => Volatile.Read(ref _dominantFrequencyHz);

    /// <inheritdoc />
    public string XLabel => "Кефренция, мс";

    /// <inheritdoc />
    public string YLabel => "Амплитуда";

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

        var count = samples.Length;
        if (count < 4)
        {
            _x = Array.Empty<double>();
            _y = Array.Empty<double>();
            Publish(0.0, 0.0);
            MarkUpdated();
            return;
        }

        var mean = SignalMath.Mean(samples);
        var window = WindowFunctions.Create(WindowKind.Hann, count);
        var fftSize = NextPowerOfTwo(count);
        var buffer = new Complex[fftSize];

        for (var i = 0; i < count; i++)
        {
            buffer[i] = new Complex((samples[i] - mean) * window[i], 0.0);
        }

        Fourier.Forward(buffer, FourierOptions.NoScaling);

        var magnitudes = new double[buffer.Length];
        for (var i = 0; i < buffer.Length; i++)
        {
            magnitudes[i] = buffer[i].Magnitude;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = new Complex(Math.Log(Math.Max(magnitudes[i], MagnitudeFloor)), 0.0);
        }

        Fourier.Inverse(buffer, FourierOptions.NoScaling);

        var half = fftSize / 2;
        var x = new double[half + 1];
        var y = new double[half + 1];

        for (var i = 0; i <= half; i++)
        {
            x[i] = i * 1000.0 / sampleRate;
            y[i] = buffer[i].Real / fftSize;
        }

        // Кепстр имеет смысл для гармонических комплексов. Для чистого тона
        // log|FFT| определяется главным лепестком окна, и пик кепстра — артефакт.
        if (!HasHarmonicStructure(magnitudes, magnitudes.Length / 2))
        {
            _x = x;
            _y = y;
            Publish(0.0, 0.0);
            MarkUpdated();
            return;
        }

        // Используем модуль кепстра: реальные пики могут быть отрицательными.
        // Старт 2 мс отсекает медленный артефакт главного лепестка окна;
        // для основных частот выше ~500 Гц кефренция меньше и не ищется.
        var start = Math.Clamp((int)Math.Round(sampleRate * 0.002), 2, half);
        var peakIndex = 0;
        var peakValue = 0.0;

        for (var i = start; i <= half; i++)
        {
            var magnitude = Math.Abs(y[i]);
            if (magnitude > peakValue)
            {
                peakValue = magnitude;
                peakIndex = i;
            }
        }

        var quefrencyMs = peakIndex > 0 ? peakIndex * 1000.0 / sampleRate : 0.0;
        var frequencyHz = peakIndex > 0 && peakValue > 1e-9 ? (double)sampleRate / peakIndex : 0.0;

        _x = x;
        _y = y;

        Publish(quefrencyMs, frequencyHz);
        MarkUpdated();
    }

    private static bool HasHarmonicStructure(double[] magnitudes, int length)
    {
        if (length < 8)
        {
            return false;
        }

        var maximum = 0.0;
        for (var i = 1; i < length; i++)
        {
            if (magnitudes[i] > maximum)
            {
                maximum = magnitudes[i];
            }
        }

        if (maximum <= 1e-12)
        {
            return false;
        }

        var threshold = maximum * 0.15;
        var peaks = 0;
        var lastPeak = -10;

        for (var i = 2; i < length - 2; i++)
        {
            var value = magnitudes[i];
            if (value < threshold
                || value <= magnitudes[i - 1]
                || value <= magnitudes[i + 1])
            {
                continue;
            }

            if (i - lastPeak < 8)
            {
                if (value > magnitudes[lastPeak])
                {
                    lastPeak = i;
                }

                continue;
            }

            peaks++;
            lastPeak = i;
        }

        return peaks >= 2;
    }

    private static int NextPowerOfTwo(int value)    {
        if (value <= 1)
        {
            return 1;
        }

        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private void Publish(double quefrencyMs, double frequencyHz)
    {
        Volatile.Write(ref _dominantQuefrencyMs, quefrencyMs);
        Volatile.Write(ref _dominantFrequencyHz, frequencyHz);

        _readouts = new[]
        {
            new AnalyzerReadout("Доминирующая кефренция", quefrencyMs, "мс"),
            new AnalyzerReadout("Частота", frequencyHz, "Гц")
        };
    }
}
