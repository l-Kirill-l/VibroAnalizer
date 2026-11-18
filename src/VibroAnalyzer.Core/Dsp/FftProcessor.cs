using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace VibroAnalyzer.Core.Dsp;

/// <summary>
/// Результат одностороннего спектра действительного сигнала.
/// </summary>
public sealed class FftResult
{
    /// <summary>Частоты бинов, Гц.</summary>
    public double[] Frequencies { get; }

    /// <summary>Амплитуды бинов (не дБ).</summary>
    public double[] Magnitudes { get; }

    /// <summary>Амплитуды бинов в дБ: 20*log10(max(mag, 1e-12)).</summary>
    public double[] MagnitudesDb { get; }

    /// <summary>Размер БПФ после дополнения нулями.</summary>
    public int FftSize { get; }

    /// <summary>Частота дискретизации, Гц.</summary>
    public int SampleRate { get; }

    /// <summary>Постоянная составляющая исходного сигнала.</summary>
    public double DcOffset { get; }

    /// <summary>Частота пика, Гц.</summary>
    public double PeakFrequencyHz { get; }

    /// <summary>Уровень пика, дБ.</summary>
    public double PeakLevelDb { get; }

    public FftResult(
        double[] frequencies,
        double[] magnitudes,
        double[] magnitudesDb,
        int fftSize,
        int sampleRate,
        double dcOffset,
        double peakFrequencyHz,
        double peakLevelDb)
    {
        ArgumentNullException.ThrowIfNull(frequencies);
        ArgumentNullException.ThrowIfNull(magnitudes);
        ArgumentNullException.ThrowIfNull(magnitudesDb);

        Frequencies = frequencies;
        Magnitudes = magnitudes;
        MagnitudesDb = magnitudesDb;
        FftSize = fftSize;
        SampleRate = sampleRate;
        DcOffset = dcOffset;
        PeakFrequencyHz = peakFrequencyHz;
        PeakLevelDb = peakLevelDb;
    }
}

/// <summary>
/// Односторонний спектр действительного сигнала и поиск пика.
/// </summary>
public static class FftProcessor
{
    private const double MinMagnitude = 1e-12;
    private const double MinLevelDb = -240.0;

    /// <summary>
    /// Вычисляет односторонний амплитудный спектр действительного сигнала.
    /// </summary>
    public static FftResult Compute(
        double[] samples,
        int sampleRate,
        WindowKind windowKind = WindowKind.Hann,
        bool removeDc = true)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        if (samples.Length == 0)
        {
            return new FftResult(
                Array.Empty<double>(),
                Array.Empty<double>(),
                Array.Empty<double>(),
                0,
                sampleRate,
                0.0,
                0.0,
                MinLevelDb);
        }

        var count = samples.Length;

        var sum = 0.0;
        for (var i = 0; i < count; i++)
        {
            sum += samples[i];
        }

        var dcOffset = sum / count;

        var window = WindowFunctions.Create(windowKind, count);
        var windowSum = WindowFunctions.Sum(window);
        if (Math.Abs(windowSum) < 1e-12)
        {
            windowSum = count;
        }

        var fftSize = NextPowerOfTwo(count);
        var buffer = new Complex[fftSize];

        for (var i = 0; i < count; i++)
        {
            var value = removeDc ? samples[i] - dcOffset : samples[i];
            buffer[i] = new Complex(value * window[i], 0.0);
        }

        Fourier.Forward(buffer, FourierOptions.NoScaling);

        var binCount = fftSize / 2 + 1;
        var frequencies = new double[binCount];
        var magnitudes = new double[binCount];
        var magnitudesDb = new double[binCount];

        for (var k = 0; k < binCount; k++)
        {
            var magnitude = buffer[k].Magnitude;

            // Постоянная составляющая и Найквист не удваиваются.
            var scale = k == 0 || k == fftSize / 2 ? 1.0 : 2.0;
            magnitude = magnitude * scale / windowSum;

            frequencies[k] = (double)k * sampleRate / fftSize;
            magnitudes[k] = magnitude;
            magnitudesDb[k] = 20.0 * Math.Log10(Math.Max(magnitude, MinMagnitude));
        }

        var (peakFrequencyHz, peakLevelDb) = FindPeak(magnitudesDb, sampleRate, fftSize);

        return new FftResult(
            frequencies,
            magnitudes,
            magnitudesDb,
            fftSize,
            sampleRate,
            dcOffset,
            peakFrequencyHz,
            peakLevelDb);
    }

    /// <summary>
    /// Ищет максимум спектра в дБ, исключая DC и, по возможности, бин Найквиста.
    /// Частота и уровень уточняются параболической интерполяцией по трём точкам.
    /// </summary>
    public static (double FrequencyHz, double LevelDb) FindPeak(
        double[] magnitudesDb,
        int sampleRate,
        int fftSize,
        int minBin = 1)
    {
        ArgumentNullException.ThrowIfNull(magnitudesDb);

        if (magnitudesDb.Length == 0 || sampleRate <= 0 || fftSize <= 0)
        {
            return (0.0, MinLevelDb);
        }

        var start = Math.Clamp(minBin, 0, magnitudesDb.Length - 1);

        // DC по умолчанию не рассматривается.
        if (start < 1 && magnitudesDb.Length > 1)
        {
            start = 1;
        }

        var end = magnitudesDb.Length - 1;

        // Найквист исключаем, если после этого остаётся хотя бы один бин.
        var nyquistBin = fftSize / 2;
        if (nyquistBin > start && nyquistBin <= end)
        {
            end = nyquistBin - 1;
        }

        if (end < start)
        {
            end = magnitudesDb.Length - 1;
        }

        var peakIndex = start;
        var peakDb = magnitudesDb[start];

        for (var i = start + 1; i <= end; i++)
        {
            if (magnitudesDb[i] > peakDb)
            {
                peakDb = magnitudesDb[i];
                peakIndex = i;
            }
        }

        var frequency = (double)peakIndex * sampleRate / fftSize;
        var level = peakDb;

        if (peakIndex > start && peakIndex < end)
        {
            var y0 = magnitudesDb[peakIndex - 1];
            var y1 = magnitudesDb[peakIndex];
            var y2 = magnitudesDb[peakIndex + 1];
            var denominator = y0 - 2.0 * y1 + y2;

            if (Math.Abs(denominator) > 1e-12 && double.IsFinite(denominator))
            {
                var delta = 0.5 * (y0 - y2) / denominator;
                delta = Math.Clamp(delta, -0.5, 0.5);

                frequency = (peakIndex + delta) * sampleRate / fftSize;
                level = y1 - 0.25 * (y0 - y2) * delta;
            }
        }

        frequency = Math.Clamp(frequency, 0.0, sampleRate / 2.0);

        return (frequency, level);
    }

    private static int NextPowerOfTwo(int value)
    {
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
}
