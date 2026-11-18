using VibroAnalyzer.Audio;
using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Dsp;
using VibroAnalyzer.Core.Engine;
using VibroAnalyzer.Core.Session;

namespace VibroAnalyzer.Tests;

public sealed class EngineTests
{
    [Fact]
    public void DemoBackendRoutesTwoInputChannelsIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibroanalyzer-tests", Guid.NewGuid().ToString("N"));

        try
        {
            Assert.Contains("Демо", AudioBackendFactory.AvailableBackendNames);

            var context = SessionContext.FromArgs(new[] { "--session-dir", root });
            using var engine = new AudioEngine(AudioBackendFactory.Create("Демо"), context);

            var device = engine.Devices.First(item => item.Id == "sim-input");
            var low = engine.AddInput(device, 0, "Датчик 1 кГц");
            var high = engine.AddInput(device, 1, "Датчик 100 Гц");

            var lowScope = new OscilloscopeAnalyzer { TimeWindowSeconds = 0.2 };
            var highScope = new OscilloscopeAnalyzer { TimeWindowSeconds = 0.2 };
            low.AddAnalyzer(lowScope);
            high.AddAnalyzer(highScope);
            low.AddAnalyzer(new SpectrumAnalyzer());
            high.AddAnalyzer(new SpectrumAnalyzer());

            engine.Start();

            Assert.True(engine.IsRunning);
            Assert.True(
                SpinWait.SpinUntil(
                    () => low.RawBuffer.Count > 24000 && high.RawBuffer.Count > 24000,
                    TimeSpan.FromSeconds(3)),
                "Демо-бэкенд не заполнил оба входа.");
            Assert.True(
                SpinWait.SpinUntil(
                    () => lowScope.LastUpdated != default && highScope.LastUpdated != default,
                    TimeSpan.FromSeconds(3)),
                "Анализаторы не получили данные.");

            var lowPeak = FftProcessor.Compute(low.RawBuffer.Snapshot(8192, 0), 48000);
            var highPeak = FftProcessor.Compute(high.RawBuffer.Snapshot(8192, 0), 48000);

            Assert.InRange(lowPeak.PeakFrequencyHz, 990, 1010);
            Assert.InRange(highPeak.PeakFrequencyHz, 90, 110);

            var reportPath = engine.SaveMeasurement();
            Assert.True(File.Exists(reportPath));

            engine.Stop();
            Assert.False(engine.IsRunning);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void OutputsUseIndependentChannelsAndModulations()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibroanalyzer-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var context = SessionContext.FromArgs(new[] { "--session-dir", root });
            using var engine = new AudioEngine(AudioBackendFactory.Create("Демо"), context);

            var device = engine.Devices.First(item => item.Id == "sim-output");
            var left = engine.AddOutput(device, 0, "Левый");
            var right = engine.AddOutput(device, 1, "Правый");

            left.Modulation.Carrier.FrequencyHz = 1000;
            left.Modulation.Carrier.Amplitude = 0.2;
            right.Modulation.Carrier.FrequencyHz = 3000;
            right.Modulation.Carrier.Amplitude = 0.3;

            engine.Start();
            Assert.True(engine.IsRunning);

            Thread.Sleep(200);
            engine.Stop();

            Assert.False(engine.IsRunning);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
