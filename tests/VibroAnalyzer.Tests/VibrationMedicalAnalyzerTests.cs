using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Analysis.Medicine;
using VibroAnalyzer.Core.Analysis.Seismology;
using VibroAnalyzer.Core.Analysis.Vibration;

namespace VibroAnalyzer.Tests;

public sealed class VibrationMedicalAnalyzerTests
{
    [Fact]
    public void EnvelopeFollowsAmplitudeModulatedSignal()
    {
        const int sampleRate = 48000;
        const int count = 8192;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / sampleRate;
            var modulation = 0.5 + 0.4 * Math.Sin(2.0 * Math.PI * 20.0 * time);
            samples[i] = modulation * Math.Sin(2.0 * Math.PI * 3000.0 * time);
        }

        var analyzer = new EnvelopeAnalyzer();
        analyzer.Process(samples, sampleRate);

        Assert.Equal(count, analyzer.X.Length);
        Assert.Equal(count, analyzer.Y.Length);
        Assert.InRange(analyzer.Y.Max(), 0.40, 0.75);
        Assert.InRange(analyzer.Y.Average(), 0.27, 0.40);
        Assert.InRange(Readout(analyzer, "СКЗ"), 0.32, 0.42);
        Assert.InRange(Readout(analyzer, "Частота"), 13.0, 27.0);
        Assert.NotEqual(default, analyzer.LastUpdated);
    }

    [Fact]
    public void TachometerFindsKnownRotationFrequency()
    {
        const int sampleRate = 1000;
        const int count = 8192;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = 0.5 * Math.Sin(2.0 * Math.PI * 25.0 * i / sampleRate);
        }

        var analyzer = new TachometerAnalyzer();
        analyzer.Process(samples, sampleRate);

        Assert.InRange(Readout(analyzer, "RPM"), 1480.0, 1520.0);
        Assert.InRange(Readout(analyzer, "Частота"), 24.6, 25.4);
        Assert.InRange(Readout(analyzer, "Период"), 39.0, 41.0);
        Assert.Empty(analyzer.X);
        Assert.Empty(analyzer.Y);
    }

    [Fact]
    public void TachometerRejectsStrongHarmonic()
    {
        const int sampleRate = 1000;
        const int count = 8192;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = 0.5 * Math.Sin(2.0 * Math.PI * 25.0 * i / sampleRate)
                + 0.5 * Math.Sin(2.0 * Math.PI * 50.0 * i / sampleRate);
        }

        var analyzer = new TachometerAnalyzer();
        analyzer.Process(samples, sampleRate);

        Assert.InRange(Readout(analyzer, "RPM"), 1480.0, 1520.0);
    }

    [Fact]
    public void EnvelopeOfConstantAmplitudeHasNoFalseModulation()
    {
        const int sampleRate = 48000;
        const int count = 8192;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = 0.5 * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate);
        }

        var analyzer = new EnvelopeAnalyzer { CutoffHz = 100 };
        analyzer.Process(samples, sampleRate);

        Assert.InRange(Readout(analyzer, "Частота"), 0.0, 1.0);
    }

    [Fact]
    public void StaLtaTriggersOnImpulse()
    {
        const int sampleRate = 100;
        const int count = 2000;
        var samples = new double[count];
        samples[^1] = 1.0;

        var analyzer = new StaLtaAnalyzer();
        analyzer.Process(samples, sampleRate);

        Assert.Equal(count, analyzer.Y.Length);
        Assert.True(analyzer.Y.Max() > 3.0);
        Assert.Equal(1.0, Readout(analyzer, "Триггер"), 6);
        Assert.True(Readout(analyzer, "Событий") >= 1.0);
    }

    [Fact]
    public void PulseDetectsPulseTrain()
    {
        const int sampleRate = 200;
        const int count = 2000;
        var samples = new double[count];

        for (var beat = 0; beat < 10; beat++)
        {
            var center = 0.5 + beat;
            for (var i = 0; i < count; i++)
            {
                var time = (double)i / sampleRate;
                var offset = (time - center) / 0.04;
                samples[i] += 0.8 * Math.Exp(-0.5 * offset * offset);
            }
        }

        var analyzer = new PulseAnalyzer();
        analyzer.Process(samples, sampleRate);

        Assert.InRange(Readout(analyzer, "Пульс"), 55.0, 65.0);
        Assert.InRange(Readout(analyzer, "Средний RR"), 950.0, 1050.0);
        Assert.True(Readout(analyzer, "СКО RR") < 50.0);
        Assert.True(Readout(analyzer, "Число ударов") >= 9.0);
    }

    [Fact]
    public void EegAlphaBandDominatesTenHertzTone()
    {
        const int sampleRate = 48000;
        const int count = 16384;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = 0.5 * Math.Sin(2.0 * Math.PI * 10.0 * i / sampleRate);
        }

        var analyzer = new EegBandAnalyzer();
        analyzer.Process(samples, sampleRate);

        Assert.Equal(5, analyzer.X.Length);
        Assert.Equal(5, analyzer.Y.Length);

        var alpha = Readout(analyzer, "Доля Альфа");
        Assert.InRange(alpha, 50.0, 100.0);

        var maximumIndex = 0;
        for (var i = 1; i < analyzer.Y.Length; i++)
        {
            if (analyzer.Y[i] > analyzer.Y[maximumIndex])
            {
                maximumIndex = i;
            }
        }

        Assert.Equal(2, maximumIndex);
    }

    [Fact]
    public void EmgRejectsMainsHumAndFindsMedianFrequency()
    {
        const int sampleRate = 2000;
        const int count = 8192;
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            var time = (double)i / sampleRate;
            samples[i] = 1.0 * Math.Sin(2.0 * Math.PI * 50.0 * time)
                + 0.5 * Math.Sin(2.0 * Math.PI * 100.0 * time);
        }

        var analyzer = new EmgAnalyzer();
        analyzer.Process(samples, sampleRate);

        Assert.InRange(Readout(analyzer, "Медианная частота"), 95.0, 105.0);
        Assert.InRange(Readout(analyzer, "СКЗ"), 0.30, 0.40);
    }

    [Fact]
    public void BalanceDetectsOneXAmplitudeAndPhase()
    {
        const int sampleRate = 48000;
        const int count = 8192;
        var samples = new double[count];
        var phase = 30.0 * Math.PI / 180.0;

        for (var i = 0; i < count; i++)
        {
            samples[i] = 0.5 * Math.Sin(2.0 * Math.PI * 25.0 * i / sampleRate + phase);
        }

        var analyzer = new BalanceAnalyzer { Rpm = 1500.0 };
        analyzer.Process(samples, sampleRate);

        Assert.InRange(Readout(analyzer, "Амплитуда"), 0.48, 0.52);
        Assert.InRange(Readout(analyzer, "Фаза"), 28.0, 32.0);
        Assert.InRange(Readout(analyzer, "Уровень дисбаланса"), -6.5, -5.5);
    }

    private static double Readout(IAnalyzerView view, string name)
    {
        return view.Readouts.First(readout => readout.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
