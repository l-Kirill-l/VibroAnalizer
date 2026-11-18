using VibroAnalyzer.Core.Dsp;
using VibroAnalyzer.Core.Routing;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Tests;

public sealed class RoutingTests
{
    [Fact]
    public void VirtualInputAveragesTwoSensors()
    {
        const int sampleRate = 48000;
        var first = new InputBus("Датчик A", "test", 0, sampleRate);
        var second = new InputBus("Датчик B", "test", 1, sampleRate);

        var mixed = new VirtualInputBus(
            "Среднее",
            new[]
            {
                new FusionSource(first),
                new FusionSource(second),
            },
            FusionMode.Average,
            sampleRate);

        first.RawBuffer.Write(MakeBlock(100, 0.4, sampleRate, 4800));
        second.RawBuffer.Write(MakeBlock(1000, 0.2, sampleRate, 4800));
        mixed.Update();

        var snapshot = mixed.RawBuffer.Snapshot(960, 0);
        var rms = SignalMath.Rms(snapshot);

        Assert.InRange(rms, 0.13, 0.19);
    }

    [Fact]
    public void BandMergeCombinesLowAndHighSensors()
    {
        const int sampleRate = 48000;
        var low = new InputBus("НЧ", "test", 0, sampleRate);
        var high = new InputBus("ВЧ", "test", 1, sampleRate);

        var merged = new VirtualInputBus(
            "НЧ+ВЧ",
            new[]
            {
                new FusionSource(low, Band: FusionBand.LowPass, CutoffHz: 500),
                new FusionSource(high, Band: FusionBand.HighPass, CutoffHz: 2000),
            },
            FusionMode.BandMerge,
            sampleRate);

        low.RawBuffer.Write(MakeBlock(100, 0.4, sampleRate, 4800));
        high.RawBuffer.Write(MakeBlock(5000, 0.4, sampleRate, 4800));

        merged.Update();

        var result = FftProcessor.Compute(merged.RawBuffer.Snapshot(8192, 0), sampleRate);

        Assert.True(result.PeakFrequencyHz is > 80 and < 120 or > 4900 and < 5100);
        Assert.Contains(result.Frequencies, frequency => Math.Abs(frequency - 100) < 10);
        Assert.Contains(result.Frequencies, frequency => Math.Abs(frequency - 5000) < 10);
    }

    [Fact]
    public void OutputMixerKeepsChannelsSeparate()
    {
        const int sampleRate = 48000;
        var left = new OutputBus("Левый", "test", 0);
        var right = new OutputBus("Правый", "test", 1);

        left.Modulation.Carrier.FrequencyHz = 1000;
        left.Modulation.Carrier.Amplitude = 0.5;
        right.Modulation.Carrier.FrequencyHz = 3000;
        right.Modulation.Carrier.Amplitude = 0.5;

        var block = OutputMixer.Mix(new[] { left, right }, 4800, sampleRate, 2);

        Assert.Equal(4800 * 2, block.Length);

        var leftChannel = new double[4800];
        var rightChannel = new double[4800];

        for (var frame = 0; frame < 4800; frame++)
        {
            leftChannel[frame] = block[frame * 2];
            rightChannel[frame] = block[frame * 2 + 1];
        }

        var leftPeak = FftProcessor.Compute(leftChannel, sampleRate);
        var rightPeak = FftProcessor.Compute(rightChannel, sampleRate);

        Assert.InRange(leftPeak.PeakFrequencyHz, 990, 1010);
        Assert.InRange(rightPeak.PeakFrequencyHz, 2990, 3010);
    }

    [Fact]
    public void OutputMixerSumsBusesOnSameChannel()
    {
        const int sampleRate = 48000;
        var first = new OutputBus("A", "test", 0);
        var second = new OutputBus("B", "test", 0);

        first.Modulation.Carrier.FrequencyHz = 1000;
        first.Modulation.Carrier.Amplitude = 0.2;
        second.Modulation.Carrier.FrequencyHz = 1000;
        second.Modulation.Carrier.Amplitude = 0.2;

        var block = OutputMixer.Mix(new[] { first, second }, 4800, sampleRate, 1);
        var values = new double[block.Length];

        for (var i = 0; i < block.Length; i++)
        {
            values[i] = block[i];
        }

        var rms = SignalMath.Rms(values);
        Assert.InRange(rms, 0.25, 0.32);
    }

    private static SignalBlock MakeBlock(double frequency, double amplitude, int sampleRate, int frames)
    {
        var samples = new float[frames];

        for (var i = 0; i < frames; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        }

        return new SignalBlock(samples, 1, sampleRate);
    }
}
