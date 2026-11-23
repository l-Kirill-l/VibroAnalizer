using VibroAnalyzer.Core.Devices;

namespace VibroAnalyzer.Tests;

public sealed class AudioPortTopologyTests
{
    [Fact]
    public void CombinesHeadphonesAndHeadsetMicIntoFourPinCombo()
    {
        var ports = AudioPortTopology.Build(new[]
        {
            new AudioPortRoute(
                "analog-output-headphones",
                "Наушники",
                IsInput: false,
                AudioJackType.Headphone,
                PortAvailability.Unavailable,
                "Legacy 2",
                Channels: 2),
            new AudioPortRoute(
                "analog-input-headset-mic",
                "Микрофон гарнитуры",
                IsInput: true,
                AudioJackType.Headset,
                PortAvailability.Unavailable,
                "Legacy 2",
                Channels: 2),
        });

        var port = Assert.Single(ports);
        Assert.True(port.IsInput);
        Assert.True(port.IsOutput);
        Assert.Equal("Комбо-разъём гарнитуры", port.Name);
        Assert.Equal(PortAvailability.Unavailable, port.Availability);
        Assert.Equal(
            new[] { PinMode.Write, PinMode.Write, PinMode.Absent, PinMode.Read },
            port.Pins.Select(pin => pin.Mode));
    }

    [Fact]
    public void OutputOnlyJackHasWritePinsAndAbsentFourthPin()
    {
        var port = Assert.Single(AudioPortTopology.Build(new[]
        {
            new AudioPortRoute(
                "analog-output-speaker",
                "Динамики",
                IsInput: false,
                AudioJackType.Speaker,
                PortAvailability.Unknown,
                "Legacy 3",
                Channels: 2),
        }));

        Assert.False(port.IsInput);
        Assert.True(port.IsOutput);
        Assert.Equal(
            new[] { PinMode.Write, PinMode.Write, PinMode.Absent, PinMode.Absent },
            port.Pins.Select(pin => pin.Mode));
    }

    [Fact]
    public void InputOnlyJackHasReadPinsAndAbsentFourthPin()
    {
        var port = Assert.Single(AudioPortTopology.Build(new[]
        {
            new AudioPortRoute(
                "analog-input-internal-mic",
                "Встроенный микрофон",
                IsInput: true,
                AudioJackType.Microphone,
                PortAvailability.Available,
                "Legacy 1",
                Channels: 2),
        }));

        Assert.True(port.IsInput);
        Assert.False(port.IsOutput);
        Assert.Equal(
            new[] { PinMode.Read, PinMode.Read, PinMode.Absent, PinMode.Absent },
            port.Pins.Select(pin => pin.Mode));
    }

    [Fact]
    public void MonoInputUsesOnlyFirstSignalPin()
    {
        var port = Assert.Single(AudioPortTopology.Build(new[]
        {
            new AudioPortRoute(
                "analog-input-mic",
                "Микрофон",
                IsInput: true,
                AudioJackType.Microphone,
                PortAvailability.Unknown,
                "Legacy 1",
                Channels: 1),
        }));

        Assert.Equal(
            new[] { PinMode.Read, PinMode.Absent, PinMode.Absent, PinMode.Absent },
            port.Pins.Select(pin => pin.Mode));
    }

    [Fact]
    public void DigitalPortDoesNotPretendToHaveAnalogPins()
    {
        var port = Assert.Single(AudioPortTopology.Build(new[]
        {
            new AudioPortRoute(
                "hdmi-output-0",
                "HDMI / DisplayPort",
                IsInput: false,
                AudioJackType.Digital,
                PortAvailability.Unavailable,
                "Legacy 1",
                Channels: 2),
        }));

        Assert.True(port.IsDigital);
        Assert.Empty(port.Pins);
    }

    [Fact]
    public void SameDirectionRoutesAreNotMergedIntoCombo()
    {
        var ports = AudioPortTopology.Build(new[]
        {
            new AudioPortRoute(
                "analog-output-speaker",
                "Динамики",
                IsInput: false,
                AudioJackType.Speaker,
                PortAvailability.Unknown,
                "Legacy 3",
                Channels: 2),
            new AudioPortRoute(
                "analog-output-headphones",
                "Наушники",
                IsInput: false,
                AudioJackType.Headphone,
                PortAvailability.Unavailable,
                "Legacy 2",
                Channels: 2),
        });

        Assert.Equal(2, ports.Count);
        Assert.All(ports, port =>
        {
            Assert.False(port.IsInput);
            Assert.True(port.IsOutput);
        });
    }
}
