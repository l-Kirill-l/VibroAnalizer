using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Devices;
using VibroAnalyzer.Core.Engine;
using VibroAnalyzer.Core.Session;

namespace VibroAnalyzer.Tests;

public sealed class DevicePortRoutingTests
{
    [Fact]
    public void EffectiveStreamIdFallsBackToDeviceId()
    {
        var device = CreateInput("legacy", streamId: "", portId: "");

        Assert.Equal("legacy", device.EffectiveStreamId);
    }

    [Fact]
    public void EffectiveStreamIdUsesExplicitStreamId()
    {
        var device = CreateInput("logical-a", streamId: "source", portId: "internal");

        Assert.Equal("source", device.EffectiveStreamId);
    }

    [Fact]
    public void DifferentPortsOfSameStreamCanBeAddedSeparately()
    {
        var internalMic = CreateInput("logical-internal", "source", "internal");
        var headsetMic = CreateInput("logical-headset", "source", "headset");

        using var engine = CreateEngine(new[] { internalMic, headsetMic });
        engine.AddInput(internalMic, 0);
        engine.AddInput(headsetMic, 0);

        Assert.Equal(2, engine.Inputs.Count);
    }

    [Fact]
    public void UnconnectedInputCanBeCreatedAndConnectedLater()
    {
        var internalMic = CreateInput("logical-internal", "source", "internal");

        using var engine = CreateEngine(new[] { internalMic });
        var bus = engine.AddInput("Датчик 1");

        Assert.False(bus.IsConnected);
        Assert.Equal("Датчик 1", bus.Name);

        engine.ReconnectInput(bus, internalMic, 0);

        Assert.True(bus.IsConnected);
        Assert.Equal(internalMic.Id, bus.DeviceId);
        Assert.Equal(0, bus.PhysicalChannel);
        Assert.Equal("Датчик 1", bus.Name);
    }

    [Fact]
    public void SamePortCanBeUsedByMultipleLogicalInputs()
    {
        var first = CreateInput("logical-a", "source", "internal");
        var second = CreateInput("logical-b", "source", "internal");

        using var engine = CreateEngine(new[] { first, second });

        engine.AddInput(first, 0);
        engine.AddInput(second, 1);
    }

    [Fact]
    public void DifferentOutputPortsCanBeAddedSeparately()
    {
        var speakers = CreateOutput("logical-speakers", "sink", "speakers");
        var headphones = CreateOutput("logical-headphones", "sink", "headphones");

        using var engine = CreateEngine(new[] { speakers, headphones });
        engine.AddOutput(speakers, 0);
        engine.AddOutput(headphones, 0);

        Assert.Equal(2, engine.Outputs.Count);
    }

    [Fact]
    public void UnconnectedOutputCanBeCreatedAndConnectedLater()
    {
        var speakers = CreateOutput("logical-speakers", "sink", "speakers");

        using var engine = CreateEngine(new[] { speakers });
        var bus = engine.AddOutput("Выход 1");

        Assert.False(bus.IsConnected);
        engine.ReconnectOutput(bus, speakers, 0);
        Assert.True(bus.IsConnected);
        Assert.Equal(speakers.Id, bus.DeviceId);
    }

    private static AudioEngine CreateEngine(IReadOnlyList<AudioDeviceInfo> devices)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vibroanalyzer-tests",
            Guid.NewGuid().ToString("N"));
        var context = SessionContext.FromArgs(new[] { "--session-dir", root });
        return new AudioEngine(new FixedDeviceBackend(devices), context);
    }

    private static AudioDeviceInfo CreateInput(string id, string streamId, string portId)
    {
        return CreateDevice(id, streamId, portId, isInput: true);
    }

    private static AudioDeviceInfo CreateOutput(string id, string streamId, string portId)
    {
        return CreateDevice(id, streamId, portId, isInput: false);
    }

    private static AudioDeviceInfo CreateDevice(
        string id,
        string streamId,
        string portId,
        bool isInput)
    {
        var channels = new[]
        {
            new AudioChannelInfo(
                0,
                "Левый",
                isInput,
                isInput ? AudioJackType.Microphone : AudioJackType.Speaker),
            new AudioChannelInfo(
                1,
                "Правый",
                isInput,
                isInput ? AudioJackType.Microphone : AudioJackType.Speaker),
        };

        return new AudioDeviceInfo(
            id,
            id,
            "Тестовый",
            IsInput: isInput,
            IsOutput: !isInput,
            InputChannels: isInput ? 2 : 0,
            OutputChannels: isInput ? 0 : 2,
            DefaultSampleRate: 48000,
            SupportedSampleRates: new[] { 48000 },
            Channels: channels,
            Capabilities: new DeviceCapabilities(
                SupportsDuplex: true,
                IsAcCoupled: true,
                MaxInputVoltageRms: 1.0,
                SupportsExclusiveMode: false,
                SupportsLoopback: true,
                SupportsChannelMapping: true,
                BitDepth: 24))
        {
            CardId = "card",
            StreamId = streamId,
            PortId = portId,
        };
    }

    private sealed class FixedDeviceBackend : IAudioBackend
    {
        private readonly IReadOnlyList<AudioDeviceInfo> _devices;

        public FixedDeviceBackend(IReadOnlyList<AudioDeviceInfo> devices)
        {
            _devices = devices;
        }

        public string Name => "Тестовый";

        public bool IsAvailable => true;

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => _devices;

        public IAudioStream OpenStream(AudioStreamOptions options)
            => throw new NotSupportedException();
    }
}
