using VibroAnalyzer.Core.Analysis.Acoustics;
using VibroAnalyzer.Core.Analysis.Electrical;
using VibroAnalyzer.Core.Analysis.General;
using VibroAnalyzer.Core.Analysis.Music;

namespace VibroAnalyzer.Tests;

public sealed class GeneralAcousticsAnalyzerTests
{
    private const int SampleRate = 48000;

    private static double[] GenerateSine(double frequency, double amplitude, int count)
    {
        var samples = new double[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = amplitude * Math.Sin(2 * Math.PI * frequency * i / SampleRate);
        }

        return samples;
    }

    [Fact]
    public void LevelStatisticsMeasuresSine()
    {
        var analyzer = new LevelStatisticsAnalyzer();
        analyzer.Process(GenerateSine(1000, 1.0, 4096), SampleRate);

        Assert.InRange(analyzer.Rms, 0.70, 0.712);
        Assert.InRange(analyzer.Peak, 0.99, 1.01);
        Assert.InRange(analyzer.PeakToPeak, 1.98, 2.01);
        Assert.InRange(analyzer.CrestFactor, 1.40, 1.43);
        Assert.InRange(analyzer.Kurtosis, 1.40, 1.60);
        Assert.InRange(analyzer.ExcessKurtosis, -1.60, -1.40);
        Assert.InRange(analyzer.Skewness, -0.02, 0.02);
        Assert.Equal(8, analyzer.Readouts.Count);
    }

    [Fact]
    public void HistogramOfRampIsNormalized()
    {
        var samples = new double[8192];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i / (samples.Length - 1.0);
        }

        var analyzer = new HistogramAnalyzer();
        analyzer.Process(samples, SampleRate);

        Assert.Equal(128, analyzer.X.Length);
        Assert.Equal(128, analyzer.Y.Length);
        Assert.InRange(analyzer.Y.Sum(), 0.999, 1.001);
        Assert.InRange(analyzer.Mean, 0.49, 0.51);
        Assert.InRange(analyzer.Median, 0.49, 0.51);
        Assert.Equal(0.0, analyzer.Minimum, 6);
        Assert.Equal(1.0, analyzer.Maximum, 6);
    }

    [Fact]
    public void AutocorrelationFindsPeriodAndFrequency()
    {
        var analyzer = new AutocorrelationAnalyzer();
        analyzer.Process(GenerateSine(100, 0.5, 8192), SampleRate);

        Assert.InRange(analyzer.PeriodSeconds, 0.0098, 0.0102);
        Assert.InRange(analyzer.FrequencyHz, 98.0, 102.0);
        Assert.InRange(analyzer.FirstMaximum, 0.9, 1.0);
        Assert.Equal(8192, analyzer.X.Length);
        Assert.Equal(8192, analyzer.Y.Length);
    }

    [Fact]
    public void BandLevelWorksForVeryNarrowBand()
    {
        var analyzer = new BandLevelAnalyzer { LowHz = 999, HighHz = 1001 };
        analyzer.Process(GenerateSine(1000, 0.5, 8192), SampleRate);

        Assert.InRange(analyzer.BandRms, 0.34, 0.37);
        Assert.InRange(analyzer.BandLevelDb, -9.6, -8.5);
    }

    [Fact]
    public void TrendSlopeOnRampIsCorrect()
    {
        const int rate = 1000;
        const int count = 4000;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = (double)i / rate;
        }

        var analyzer = new TrendAnalyzer { MovingAverageSeconds = 0.1 };
        analyzer.Process(samples, rate);

        Assert.InRange(analyzer.SlopePerSecond, 0.98, 1.02);
        Assert.True(analyzer.Drift > 3.5);
    }

    [Fact]
    public void SpectralSlopeOfPureToneIsNotDrivenByNoiseFloor()
    {
        var analyzer = new SpectralFeaturesAnalyzer();
        analyzer.Process(GenerateSine(1000, 0.5, 8192), SampleRate);

        Assert.InRange(analyzer.SlopeDbPerKhz, -1.0, 1.0);
    }

    [Fact]
    public void MainsCountsEleventhHarmonic()
    {
        const int count = 16384;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / SampleRate;
            samples[i] = 0.5 * Math.Sin(2.0 * Math.PI * 50.0 * time)
                + 0.10 * Math.Sin(2.0 * Math.PI * 550.0 * time);
        }

        var analyzer = new MainsAnalyzer { FrequencyHz = 50.0 };
        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.ThdPercent, 16.0, 24.0);
    }

    [Fact]
    public void HarmonicAnalyzerMeasuresThd()
    {
        var samples = new double[16384];
        for (var i = 0; i < samples.Length; i++)
        {
            var time = (double)i / SampleRate;
            samples[i] = 0.5 * Math.Sin(2 * Math.PI * 1000 * time)
                + 0.05 * Math.Sin(2 * Math.PI * 2000 * time);
        }

        var analyzer = new HarmonicAnalyzer();
        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.FundamentalFrequencyHz, 995.0, 1005.0);
        Assert.InRange(analyzer.ThdPercent, 8.5, 11.5);
        Assert.InRange(analyzer.SignificantHarmonics, 0.9, 1.1);
        Assert.Equal(10, analyzer.X.Length);
        Assert.InRange(analyzer.Y[1], -27.5, -24.5);
    }

    [Fact]
    public void MainsAnalyzerMeasuresFundamentalAndThirdHarmonic()
    {
        const int sampleRate = 48000;
        const int count = 16384;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / sampleRate;
            samples[i] = 0.5 * Math.Sin(2.0 * Math.PI * 50.0 * time)
                + 0.05 * Math.Sin(2.0 * Math.PI * 150.0 * time);
        }

        var analyzer = new MainsAnalyzer { FrequencyHz = 50.0 };
        analyzer.Process(samples, sampleRate);

        Assert.InRange(analyzer.MainsFrequencyHz, 49.8, 50.2);
        Assert.InRange(analyzer.ThdPercent, 8.0, 12.0);
        Assert.InRange(analyzer.ThirdHarmonicDb, -28.0, -24.0);
    }

    [Fact]
    public void HarmonicThdPlusNoiseIsNearZeroForPureSine()
    {
        var analyzer = new HarmonicAnalyzer();
        analyzer.Process(GenerateSine(1000, 0.5, 16384), SampleRate);

        Assert.InRange(analyzer.ThdPercent, 0.0, 1.0);
        Assert.InRange(analyzer.ThdPlusNoisePercent, 0.0, 2.0);
    }

    [Fact]
    public void HarmonicChoosesFundamentalWhenSecondHarmonicIsStronger()
    {
        const int count = 16384;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / SampleRate;
            samples[i] = 0.3 * Math.Sin(2.0 * Math.PI * 1000.0 * time)
                + 0.5 * Math.Sin(2.0 * Math.PI * 2000.0 * time);
        }

        var analyzer = new HarmonicAnalyzer();
        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.FundamentalFrequencyHz, 990.0, 1010.0);
        Assert.True(analyzer.ThdPercent > 100.0);
    }

    [Fact]
    public void TunerRejectsStrongHarmonic()
    {
        const int count = 8192;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / SampleRate;
            samples[i] = 0.3 * Math.Sin(2.0 * Math.PI * 440.0 * time)
                + 0.3 * Math.Sin(2.0 * Math.PI * 880.0 * time);
        }

        var analyzer = new TunerAnalyzer();
        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.FrequencyHz, 435.0, 445.0);
    }

    [Fact]
    public void TunerIdentifiesA4()
    {
        var analyzer = new TunerAnalyzer();
        analyzer.Process(GenerateSine(440, 0.5, 8192), SampleRate);

        Assert.InRange(analyzer.FrequencyHz, 438.0, 442.0);
        Assert.Equal(69, analyzer.NoteNumber);
        Assert.Equal("A", analyzer.NoteName);
        Assert.Equal(4, analyzer.Octave);
        Assert.InRange(analyzer.Cents, -5.0, 5.0);
    }

    [Fact]
    public void BandLevelRestrictsToSelectedBand()
    {
        var samples = GenerateSine(1000, 0.5, 8192);
        var analyzer = new BandLevelAnalyzer { LowHz = 500, HighHz = 1500 };

        analyzer.Process(samples, SampleRate);

        Assert.InRange(analyzer.BandRms, 0.34, 0.37);
        Assert.InRange(analyzer.BandLevelDb, -9.6, -8.5);

        analyzer.LowHz = 3000;
        analyzer.HighHz = 4000;
        analyzer.Process(samples, SampleRate);

        Assert.True(analyzer.BandRms < 0.01);
    }
}
