using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Analysis.Acoustics;
using VibroAnalyzer.Core.Analysis.General;
using VibroAnalyzer.Core.Analysis.Medicine;
using VibroAnalyzer.Core.Analysis.Vibration;
using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Tests;

public sealed class NewAnalyzerTests
{
    private const int SampleRate = 48000;

    private static double[] Sine(double frequency, double amplitude, int count)
    {
        var samples = new double[count];
        for (var index = 0; index < count; index++)
        {
            samples[index] = amplitude * Math.Sin(2.0 * Math.PI * frequency * index / SampleRate);
        }

        return samples;
    }

    private static double Readout(IAnalyzerView view, string name)
        => view.Readouts.First(readout =>
            readout.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static double MagnitudeAt(double[] samples, double frequency)
    {
        var spectrum = FftProcessor.Compute(samples, SampleRate, WindowKind.Hann, removeDc: true);
        var bin = Array.IndexOf(
            spectrum.Frequencies,
            spectrum.Frequencies.MinBy(value => Math.Abs(value - frequency)));
        return spectrum.Magnitudes[bin];
    }

    [Fact]
    public void OptionalFiltersReduceMainsWithoutDestroyingSignal()
    {
        var count = SampleRate * 2;
        var samples = new double[count];
        for (var index = 0; index < count; index++)
        {
            var time = (double)index / SampleRate;
            samples[index] = 0.5 * Math.Sin(2.0 * Math.PI * 50.0 * time)
                             + 0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * time);
        }

        var filters = new AnalysisFilterSettings
        {
            HighPassHz = 100.0,
            LowPassHz = 5000.0,
            MainsNotchEnabled = true,
            Notch50Enabled = true,
            Notch60Enabled = true,
        };

        var filtered = filters.Apply(samples, SampleRate);
        var tail = filtered.Skip(SampleRate / 5).ToArray();
        var mains = MagnitudeAt(tail, 50.0);
        var tone = MagnitudeAt(tail, 1000.0);

        Assert.True(tone > mains * 20.0, $"50 Гц не подавлена: {mains:E2}, 1 кГц: {tone:E2}");

        filters.BandPassEnabled = false;
        filters.Notch50Enabled = false;
        filters.Notch60Enabled = false;
        var unfiltered = filters.Apply(samples, SampleRate);

        // При отключённых фильтрах массив не копируется и не меняется.
        Assert.Same(samples, unfiltered);
    }

    [Fact]
    public void WelchPsdFindsToneAndIntegratedRms()
    {
        var analyzer = new WelchPsdAnalyzer
        {
            SegmentFrames = 4096,
            OverlapPercent = 50,
            RemoveDc = true,
        };

        analyzer.Process(Sine(1000, 0.5, SampleRate * 2), SampleRate);

        Assert.InRange(Readout(analyzer, "Пик PSD"), 995, 1005);
        Assert.InRange(Readout(analyzer, "СКЗ"), 0.30, 0.40);
        Assert.True(Readout(analyzer, "Сегментов") > 10);
    }

    [Fact]
    public void EnvelopeSpectrumFindsModulation()
    {
        var count = SampleRate * 2;
        var samples = new double[count];
        for (var index = 0; index < count; index++)
        {
            var time = (double)index / SampleRate;
            var modulation = 1.0 + 0.5 * Math.Sin(2.0 * Math.PI * 100.0 * time);
            samples[index] = modulation * Math.Sin(2.0 * Math.PI * 3000.0 * time);
        }

        var analyzer = new EnvelopeSpectrumAnalyzer();
        analyzer.Filters.HighPassHz = 1000;
        analyzer.Filters.LowPassHz = 6000;
        analyzer.Process(samples, SampleRate);

        Assert.InRange(Readout(analyzer, "Частота модуляции"), 95, 105);
        Assert.InRange(Readout(analyzer, "Глубина модуляции"), 0.35, 0.65);
        Assert.InRange(Readout(analyzer, "Несущая"), 2900, 3100);
    }

    [Fact]
    public void LeqUsesEnergyAverageAndAWeighting()
    {
        var analyzer = new LeqAnalyzer();
        analyzer.Process(Sine(1000, 0.5, 48000), SampleRate);

        Assert.InRange(analyzer.LeqDbFs, -9.5, -8.5);
        Assert.InRange(analyzer.LaEqDb, -10.0, -8.0);
        Assert.InRange(analyzer.LcEqDb, -10.0, -8.0);

        var low = new LeqAnalyzer();
        low.Process(Sine(100, 0.5, 48000), SampleRate);
        Assert.True(low.LaEqDb < low.LcEqDb - 10.0);
    }

    [Fact]
    public void ZeroCrossingEstimatesSineFrequencyAndIgnoresDc()
    {
        var samples = Sine(1000, 0.5, 48000);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] += 1.0;
        }

        var analyzer = new ZeroCrossingAnalyzer { RemoveDc = true, ThresholdRatio = 0.05 };
        analyzer.Process(samples, SampleRate);

        Assert.InRange(Readout(analyzer, "Оценка частоты"), 975, 1025);
        Assert.InRange(Readout(analyzer, "Переходы"), 1950, 2050);
    }

    [Fact]
    public void NoiseFloorSeparatesNoiseAndSignal()
    {
        var count = SampleRate;
        var random = new Random(1234);
        var samples = new double[count];
        for (var index = 0; index < count; index++)
        {
            var noise = (random.NextDouble() - 0.5) * 0.002;
            samples[index] = index < count / 2
                ? noise
                : noise + 0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * index / SampleRate);
        }

        var analyzer = new NoiseFloorAnalyzer
        {
            FrameMilliseconds = 20,
            NoisePercentile = 10,
            SignalPercentile = 95,
        };
        analyzer.Process(samples, SampleRate);

        Assert.True(Readout(analyzer, "Шумовой пол") < -50.0);
        Assert.True(Readout(analyzer, "Уровень сигнала") > -15.0);
        Assert.True(Readout(analyzer, "SNR") > 30.0);
    }

    [Fact]
    public void MedicalAnalyzersExposeOptionalFilters()
    {
        Assert.Contains(new PulseAnalyzer().Toggles, toggle => toggle.Name.Contains("Полосовой"));
        Assert.Contains(new RespirationAnalyzer().Toggles, toggle => toggle.Name.Contains("Полосовой"));
        Assert.Contains(new EegBandAnalyzer().Toggles, toggle => toggle.Name.Contains("Полосовой"));
        Assert.Contains(new EmgAnalyzer().Toggles, toggle => toggle.Name.Contains("Полосовой"));
    }
}
