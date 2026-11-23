using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.General;

/// <summary>
/// Оценка спектральной плотности мощности методом Уэлча: перекрывающиеся
/// сегменты, окно Ханна и усреднение периодограмм.
/// </summary>
public sealed class WelchPsdAnalyzer : AnalyzerBase, IAnalyzerView, IAnalyzerParameterized, IAnalyzerToggleable
{
    private const int MinLevelDb = -240;
    private const double MinPower = 1e-24;

    private volatile double[] _frequencies = Array.Empty<double>();
    private volatile double[] _psdDb = Array.Empty<double>();
    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private readonly AnalyzerParameter[] _parameters;
    private readonly AnalyzerToggle[] _toggles;
    private long _lastProcessTicks;

    public WelchPsdAnalyzer()
    {
        _parameters = new[]
        {
            new AnalyzerParameter(
                "Длина сегмента",
                512,
                16384,
                () => SegmentFrames,
                value => SegmentFrames = (int)Math.Clamp(value, 512, 16384),
                "отсч."),
            new AnalyzerParameter(
                "Перекрытие",
                0,
                90,
                () => OverlapPercent,
                value => OverlapPercent = Math.Clamp(value, 0, 90),
                "%"),
        };

        _toggles = new[]
        {
            new AnalyzerToggle(
                "Вычитать DC",
                () => RemoveDc,
                value => RemoveDc = value),
        };
    }

    /// <summary>Длина сегмента, отсчётов.</summary>
    public int SegmentFrames { get; set; } = 4096;

    /// <summary>Перекрытие соседних сегментов, %.</summary>
    public double OverlapPercent { get; set; } = 50.0;

    /// <summary>Вычитать среднее из каждого сегмента.</summary>
    public bool RemoveDc { get; set; } = true;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerParameter> Parameters => _parameters;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerToggle> Toggles => _toggles;

    /// <inheritdoc />
    public override string Title => "Спектральная плотность (Welch)";

    /// <inheritdoc />
    public override string Category => "Общие измерения";

    /// <inheritdoc />
    public override string Description =>
        "Делит запись на перекрывающиеся сегменты, применяет окно Ханна и усредняет "
        + "периодограммы. Даёт устойчивую оценку спектральной плотности мощности в дБFS/Гц.";

    /// <inheritdoc />
    public string XLabel => "Частота, Гц";

    /// <inheritdoc />
    public string YLabel => "PSD, дБFS/Гц";

    /// <inheritdoc />
    public double[] X => _frequencies;

    /// <inheritdoc />
    public double[] Y => _psdDb;

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate)
        => Math.Max(32768, sampleRate * 2);

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
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0, 0);
            MarkUpdated();
            return;
        }

        var segment = Math.Clamp(SegmentFrames, 256, Math.Min(samples.Length, 16384));
        if (samples.Length < segment)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0, 0);
            MarkUpdated();
            return;
        }

        var overlap = (int)Math.Round(segment * Math.Clamp(OverlapPercent, 0, 90) / 100.0);
        var step = Math.Max(1, segment - overlap);
        var nfft = NextPowerOfTwo(segment);
        var window = WindowFunctions.Create(WindowKind.Hann, segment);
        var windowPower = 0.0;
        foreach (var value in window)
        {
            windowPower += value * value;
        }

        if (windowPower < 1e-20)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0, 0);
            MarkUpdated();
            return;
        }

        var psd = new double[nfft / 2 + 1];
        var segments = 0;

        for (var start = 0; start + segment <= samples.Length; start += step)
        {
            var mean = 0.0;
            if (RemoveDc)
            {
                for (var index = 0; index < segment; index++)
                {
                    mean += samples[start + index];
                }

                mean /= segment;
            }

            var buffer = new Complex[nfft];
            for (var index = 0; index < segment; index++)
            {
                buffer[index] = new Complex((samples[start + index] - mean) * window[index], 0.0);
            }

            Fourier.Forward(buffer, FourierOptions.NoScaling);

            for (var bin = 1; bin < nfft / 2; bin++)
            {
                var magnitude = buffer[bin].Magnitude;
                // Односторонняя PSD: удвоение мощности положительных частот.
                psd[bin] += 2.0 * magnitude * magnitude / (sampleRate * windowPower);
            }

            if (nfft > 1)
            {
                var dc = buffer[0].Magnitude;
                var nyquist = buffer[nfft / 2].Magnitude;
                psd[0] += dc * dc / (sampleRate * windowPower);
                psd[nfft / 2] += nyquist * nyquist / (sampleRate * windowPower);
            }

            segments++;
        }

        if (segments == 0)
        {
            Publish(Array.Empty<double>(), Array.Empty<double>(), 0.0, 0.0, 0.0, 0);
            MarkUpdated();
            return;
        }

        var frequencies = new double[psd.Length];
        var psdDb = new double[psd.Length];
        var totalPower = 0.0;
        var binWidth = (double)sampleRate / nfft;

        for (var bin = 0; bin < psd.Length; bin++)
        {
            psd[bin] /= segments;
            frequencies[bin] = bin * binWidth;
            psdDb[bin] = 10.0 * Math.Log10(Math.Max(psd[bin], MinPower));
            totalPower += psd[bin] * binWidth;
        }

        var (peakFrequency, _) = FftProcessor.FindPeak(psdDb, sampleRate, nfft);
        var peakBin = (int)Math.Round(peakFrequency / binWidth);
        var peakDensityDb = peakBin >= 0 && peakBin < psdDb.Length
            ? psdDb[peakBin]
            : MinLevelDb;
        var rms = Math.Sqrt(Math.Max(totalPower, 0.0));

        Publish(
            frequencies,
            psdDb,
            peakFrequency,
            peakDensityDb,
            rms,
            segments);

        MarkUpdated();
    }

    private void Publish(
        double[] frequencies,
        double[] psdDb,
        double peakFrequency,
        double peakDensityDb,
        double rms,
        int segments)
    {
        _frequencies = frequencies;
        _psdDb = psdDb;
        _readouts = new[]
        {
            new AnalyzerReadout("Пик PSD", peakFrequency, "Гц"),
            new AnalyzerReadout("Плотность пика", peakDensityDb, "дБFS/Гц"),
            new AnalyzerReadout("СКЗ (интеграл)", rms, "ед."),
            new AnalyzerReadout("Сегментов", segments, "шт."),
        };
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
