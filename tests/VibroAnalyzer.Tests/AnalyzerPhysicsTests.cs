using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Analysis.Acoustics;
using VibroAnalyzer.Core.Analysis.Diagnostics;
using VibroAnalyzer.Core.Analysis.Medicine;
using VibroAnalyzer.Core.Analysis.Seismology;
using VibroAnalyzer.Core.Analysis.Vibration;

namespace VibroAnalyzer.Tests;

public sealed class AnalyzerPhysicsTests
{
    private const int SampleRate = 48000;

    private static double[] GenerateSine(double frequency, double amplitude, int count)
    {
        var samples = new double[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = amplitude * Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
        }

        return samples;
    }

    private static double Readout(IAnalyzerView view, string name)
        => view.Readouts.First(readout => readout.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void OctaveBandLevelMatchesSineRms()
    {
        var analyzer = new OctaveBandAnalyzer();
        analyzer.Process(GenerateSine(1000, 0.5, 16384), SampleRate);

        Assert.InRange(analyzer.OverallLevelDb, -9.5, -8.5);
        Assert.InRange(analyzer.DominantCenterHz, 950, 1050);
        Assert.InRange(analyzer.DominantLevelDb, -9.7, -8.3);
    }

    [Fact]
    public void SoundLevelUsesStandardACWeighting()
    {
        var analyzer = new SoundLevelAnalyzer();
        analyzer.Process(GenerateSine(1000, 0.5, 8192), SampleRate);

        Assert.InRange(analyzer.LevelDbFs, -9.5, -8.5);
        Assert.InRange(analyzer.LevelDbA, -9.5, -8.5);
        Assert.InRange(analyzer.LevelDbC, -9.5, -8.5);

        analyzer.Process(GenerateSine(100, 0.5, 8192), SampleRate);

        // IEC 61672: A(100 Гц) ≈ −19.1 дБ, C(100 Гц) ≈ −0.3 дБ.
        Assert.InRange(analyzer.LevelDbA, -30.0, -26.0);
        Assert.InRange(analyzer.LevelDbC, -10.0, -8.0);
    }

    [Fact]
    public void Rt60EstimatesExponentialDecay()
    {
        const double rt60 = 0.5;
        const int count = 96000;
        var samples = new double[count];
        var tau = rt60 / Math.Log(1000.0);

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / SampleRate;
            samples[i] = Math.Exp(-time / tau) * Math.Sin(2.0 * Math.PI * 1000.0 * time);
        }

        var analyzer = new Rt60Analyzer();
        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.Rt60Seconds, 0.40, 0.60);
        Assert.InRange(analyzer.Reliability, 0.5, 1.0);
        Assert.True(analyzer.SlopeDbPerSecond < 0);
    }

    [Fact]
    public void Rt60IgnoresDcAndNoiseFloor()
    {
        const double rt60 = 0.5;
        const int count = 96000;
        var samples = new double[count];
        var tau = rt60 / Math.Log(1000.0);
        var random = new Random(12345);

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / SampleRate;
            samples[i] = 0.05
                + Math.Exp(-time / tau) * Math.Sin(2.0 * Math.PI * 1000.0 * time)
                + (random.NextDouble() - 0.5) * 0.01;
        }

        var analyzer = new Rt60Analyzer();
        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.Rt60Seconds, 0.30, 0.70);
    }

    [Fact]
    public void CepstrumFindsPeriodOfHarmonicComplex()
    {
        const int count = 8192;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / SampleRate;
            for (var harmonic = 1; harmonic <= 7; harmonic++)
            {
                samples[i] += 0.3 / harmonic * Math.Sin(2.0 * Math.PI * 100.0 * harmonic * time);
            }
        }

        var analyzer = new CepstrumAnalyzer();
        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.DominantQuefrencyMs, 9.0, 11.0);
        Assert.InRange(analyzer.DominantFrequencyHz, 90.0, 110.0);

        analyzer.Process(GenerateSine(100, 0.5, count), SampleRate);
        Assert.Equal(0.0, analyzer.DominantQuefrencyMs, 6);
    }

    [Fact]
    public void AriasIntensityMatchesConstantAcceleration()
    {
        const int count = 48000;
        var samples = new double[count];
        Array.Fill(samples, 1.0);

        var analyzer = new SeismicEnergyAnalyzer();
        analyzer.Process(samples, SampleRate);

        var expected = Math.PI / (2.0 * 9.80665);
        Assert.InRange(Readout(analyzer, "Ариаса"), expected * 0.95, expected * 1.05);
        Assert.InRange(Readout(analyzer, "PGA"), 0.99, 1.01);
        Assert.InRange(Readout(analyzer, "Длительность"), 0.85, 0.95);
    }

    [Fact]
    public void ResponseSpectrumPeaksAtResonancePeriod()
    {
        const int count = 240000; // 5 секунд
        const double inputPeriod = 0.5;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = Math.Sin(2.0 * Math.PI * i / (inputPeriod * SampleRate));
        }

        var analyzer = new ResponseSpectrumAnalyzer { Damping = 0.05 };
        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.X.Length, 90, 110);
        Assert.InRange(analyzer.Y.Length, 90, 110);
        Assert.InRange(Readout(analyzer, "Пиковый период"), 0.40, 0.60);
        Assert.True(Readout(analyzer, "Пиковое ускорение") > 1.0);
    }

    [Fact]
    public void EveryCatalogAnalyzerHasDetailedHelp()
    {
        Assert.NotEmpty(AnalyzerCatalog.All);

        foreach (var descriptor in AnalyzerCatalog.All)
        {
            var analyzer = descriptor.Factory();
            var help = analyzer.Help;

            Assert.False(string.IsNullOrWhiteSpace(help.Purpose), descriptor.Title);
            Assert.False(string.IsNullOrWhiteSpace(help.Formulas), descriptor.Title);
            Assert.False(string.IsNullOrWhiteSpace(help.Usage), descriptor.Title);
            Assert.False(string.IsNullOrWhiteSpace(help.Limitations), descriptor.Title);
            Assert.True(help.ToDisplayText().Length > 200, descriptor.Title);
            Assert.Contains(descriptor.Category, AnalyzerCatalog.CategoryDescriptions.Keys);
        }
    }

    [Fact]
    public void LongAnalyzersRequestPhysicallySufficientWindows()
    {
        Assert.True(new PulseAnalyzer().GetWindowFrames(SampleRate) >= SampleRate * 8);
        Assert.True(new RespirationAnalyzer().GetWindowFrames(SampleRate) >= SampleRate * 20);
        Assert.True(new StaLtaAnalyzer().GetWindowFrames(SampleRate) >= SampleRate * 10);
        Assert.True(new SeismicEnergyAnalyzer().GetWindowFrames(SampleRate) >= SampleRate * 10);
        Assert.True(new ResponseSpectrumAnalyzer().GetWindowFrames(SampleRate) >= SampleRate * 10);
        Assert.True(new EegBandAnalyzer().GetWindowFrames(SampleRate) >= SampleRate * 4);
        Assert.True(new TachometerAnalyzer().GetWindowFrames(SampleRate) >= SampleRate * 2);
        Assert.True(new Rt60Analyzer().GetWindowFrames(SampleRate) >= SampleRate * 10);
    }
}
