using VibroAnalyzer.Audio.Simulated;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Dsp;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Tests;

public sealed class DemoSignalTests
{
    [Fact]
    public void DemoBackendUsesConfiguredTones()
    {
        var config = new DemoSignalConfig
        {
            NoiseLevel = 0.0,
            Tones = new List<DemoTone>
            {
                new()
                {
                    FrequencyHz = 2000,
                    Amplitude = 0.25,
                    Waveform = CarrierWaveform.Sine,
                    Channel = -1,
                },
            },
        };

        var backend = new SimulatedAudioBackend(config);
        using var stream = backend.OpenStream(new AudioStreamOptions(
            "sim-input",
            null,
            48000,
            1,
            0,
            4096));

        SignalBlock? block = null;
        stream.InputAvailable += (_, value) => block = value;
        stream.Start();

        Assert.True(
            SpinWait.SpinUntil(() => block is not null, TimeSpan.FromSeconds(2)),
            "Демо-генератор не выдал блок.");

        stream.Stop();

        var samples = block!.ToChannelDouble(0);
        var spectrum = FftProcessor.Compute(samples, 48000);
        Assert.InRange(spectrum.PeakFrequencyHz, 1990, 2010);
    }

    [Fact]
    public void DemoConfigCloneIsIndependent()
    {
        var config = DemoSignalConfig.CreateDefault();
        var clone = config.Clone();
        clone.Tones[0].FrequencyHz = 1234;
        clone.NoiseLevel = 0.2;

        Assert.NotEqual(1234, config.Tones[0].FrequencyHz);
        Assert.NotEqual(0.2, config.NoiseLevel);
    }
}
