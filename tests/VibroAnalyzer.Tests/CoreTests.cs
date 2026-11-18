using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Calibration;
using VibroAnalyzer.Core.Dsp;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Session;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Tests;

public sealed class SignalTests
{
    [Fact]
    public void RingBufferPadsSnapshotOnTheLeft()
    {
        var buffer = new SignalRingBuffer(1, 10);
        buffer.Write(new SignalBlock(new float[] { 1, 2, 3 }, 1, 48000));

        var snapshot = buffer.Snapshot(5, 0);

        Assert.Equal(new double[] { 0, 0, 1, 2, 3 }, snapshot);
    }

    [Fact]
    public void OscilloscopeMeasuresSine()
    {
        const int sampleRate = 48000;
        var samples = GenerateSine(1000, 0.5, sampleRate, 4800);

        var analyzer = new OscilloscopeAnalyzer { TimeWindowSeconds = 0.1 };
        analyzer.Process(samples, sampleRate);

        Assert.InRange(analyzer.FrequencyHz, 995, 1005);
        Assert.InRange(analyzer.Rms, 0.34, 0.37);
        Assert.InRange(analyzer.Vpp, 0.98, 1.01);
        Assert.InRange(analyzer.DcOffset, -0.01, 0.01);
        Assert.NotEqual(default, analyzer.LastUpdated);
    }

    [Fact]
    public void CalibrationConvertsUnits()
    {
        var profile = new CalibrationProfile
        {
            FullScaleVolts = 2.0,
            InputGain = 2.0,
            Mode = CalibrationMode.Voltage,
        };

        Assert.Equal(0.5, profile.ToDisplay(0.5), 6);
        Assert.Equal("В", profile.Unit);

        profile.Mode = CalibrationMode.Current;
        profile.ShuntOhms = 10.0;

        Assert.Equal(0.05, profile.ToDisplay(0.5), 6);
        Assert.Equal("А", profile.Unit);
    }

    internal static double[] GenerateSine(double frequency, double amplitude, int sampleRate, int count)
    {
        var samples = new double[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        }

        return samples;
    }
}

public sealed class SpectrumTests
{
    [Fact]
    public void FftFindsToneAmplitudeAndFrequency()
    {
        const int sampleRate = 48000;
        const int fftSize = 8192;
        var samples = SignalTests.GenerateSine(1000, 0.5, sampleRate, fftSize);

        var result = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann);

        Assert.Equal(fftSize, result.FftSize);
        Assert.InRange(result.PeakFrequencyHz, 998, 1002);
        Assert.InRange(result.PeakLevelDb, -6.7, -5.4);
        Assert.True(result.Frequencies.Length > 1000);
    }

    [Fact]
    public void SpectrumAnalyzerAveragesAndPublishesArrays()
    {
        const int sampleRate = 48000;
        var analyzer = new SpectrumAnalyzer { FftSize = 4096, AveragingFactor = 0.5 };
        var samples = SignalTests.GenerateSine(1000, 0.5, sampleRate, 4096);

        analyzer.Process(samples, sampleRate);
        var first = analyzer.MagnitudesDb;
        analyzer.Process(samples, sampleRate);

        Assert.NotSame(first, analyzer.MagnitudesDb);
        Assert.InRange(analyzer.PeakFrequencyHz, 995, 1005);
        Assert.True(analyzer.NoiseFloorDb < analyzer.PeakLevelDb);
    }
}

public sealed class ModulationTests
{
    [Fact]
    public void ModulationGeneratesAmOnAllChannels()
    {
        var chain = new ModulationChain
        {
            OutputEnabled = true,
            Modulate = true,
            ModulationIndex = 0.5,
            BasebandFrequencyHz = 10,
            BasebandAmplitude = 0.5,
        };
        chain.Carrier.FrequencyHz = 1000;
        chain.Carrier.Amplitude = 0.5;

        var block = chain.GenerateBlock(48000, 24000, 2);

        Assert.Equal(2, block.Channels);
        Assert.Equal(24000, block.FrameCount);
        Assert.Equal(block.GetSample(1000, 0), block.GetSample(1000, 1), 6);

        var rms = SignalMath.Rms(block.ToChannelDouble(0));
        Assert.InRange(rms, 0.30, 0.42);
    }

    [Fact]
    public void DisabledOutputProducesSilence()
    {
        var chain = new ModulationChain { OutputEnabled = false, Modulate = true };
        var block = chain.GenerateBlock(48000, 1000, 1);

        Assert.All(block.Samples, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void EnvelopeDemodulationRecoversCarrierLevel()
    {
        var chain = new ModulationChain
        {
            OutputEnabled = true,
            Modulate = true,
            ModulationIndex = 0.5,
            BasebandFrequencyHz = 20,
            BasebandAmplitude = 0.5,
        };
        chain.Carrier.FrequencyHz = 5000;
        chain.Carrier.Amplitude = 0.5;

        var block = chain.GenerateBlock(48000, 48000, 1);

        var demod = new DemodulationChain
        {
            Enabled = true,
            Kind = DemodulationKind.Envelope,
            LowPassCutoffHz = 100,
            SourceChannel = 0,
        };
        demod.Configure(48000);

        var envelope = demod.Process(block);

        Assert.Equal(48000, envelope.Length);
        Assert.InRange(demod.LastAmplitude, 0.3, 0.7);
        Assert.All(envelope.Skip(4800), value => Assert.InRange(value, 0.0, 0.8));
    }

    [Fact]
    public void SynchronousDemodulationReportsPhase()
    {
        var carrier = new CarrierGenerator { FrequencyHz = 1000, Amplitude = 0.5 };
        carrier.Reset();
        var samples = new double[48000];
        carrier.Fill(samples, 48000);
        var block = new SignalBlock(Array.ConvertAll(samples, value => (float)value), 1, 48000);

        var demod = new DemodulationChain
        {
            Enabled = true,
            Kind = DemodulationKind.Synchronous,
            CarrierFrequencyHz = 1000,
            LowPassCutoffHz = 200,
        };
        demod.Configure(48000);
        demod.Process(block);

        Assert.True(demod.LastAmplitude > 0.05);
        Assert.False(double.IsNaN(demod.LastPhaseDegrees));
    }
}

public sealed class SessionTests
{
    [Fact]
    public void SessionWritesReportAndMeasurements()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibroanalyzer-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var context = SessionContext.FromArgs(
                new[] { "--session-dir=" + root, "--module-id", "test_module" });
            context.EnsureDirectories();

            Assert.Equal(root, context.SessionDirectory);
            Assert.Equal("test_module", context.ModuleId);
            Assert.False(context.IsStandalone);
            Assert.Equal(Path.Combine(root, "test_module"), context.ModuleDirectory);

            var writer = new SessionWriter(context);
            var report = new ReportData
            {
                ModuleId = context.ModuleId,
                CreatedAt = "2026-01-01 00:00:00",
                Backend = "Тест",
                SampleRate = 48000,
            };
            report.Measurements.Add(new Measurement("Частота", 1000, "Гц"));

            var reportPath = writer.WriteReport(report);
            Assert.True(File.Exists(reportPath));

            writer.AppendMeasurements(report.Measurements);
            var statePath = Path.Combine(root, "session_state.json");
            Assert.True(File.Exists(statePath));

            var json = File.ReadAllText(statePath);
            Assert.Contains("test_module", json);
            Assert.Contains("Частота", json);
            Assert.Contains("1000", json);
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
    public void StandaloneSessionUsesSharedDirectory()
    {
        var context = SessionContext.FromArgs(Array.Empty<string>());

        Assert.True(context.IsStandalone);
        Assert.Equal("hw_vibro_analyzer", context.ModuleId);
        Assert.EndsWith("SharedSessionData", context.SessionDirectory);
    }
}
