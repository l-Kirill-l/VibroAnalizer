using VibroAnalyzer.Core.Devices;

namespace VibroAnalyzer.Tests;

public sealed class AudioInputSpecsTests
{
    [Fact]
    public void MicrophoneAndLineInputsHaveDifferentTypicalSpecs()
    {
        var microphone = AudioInputSpecsCatalog.For(
            AudioJackType.Microphone,
            isInput: true,
            isOutput: false,
            bitDepth: 24);
        var line = AudioInputSpecsCatalog.For(
            AudioJackType.LineIn,
            isInput: true,
            isOutput: false,
            bitDepth: 24);

        Assert.NotNull(line.MaxVoltageRms);
        Assert.NotNull(microphone.MaxVoltageRms);
        Assert.True(line.MaxVoltageRms!.Value > microphone.MaxVoltageRms!.Value);
        Assert.NotNull(line.ImpedanceOhms);
        Assert.NotNull(microphone.ImpedanceOhms);
        Assert.True(line.ImpedanceOhms!.Value > microphone.ImpedanceOhms!.Value);
        Assert.Contains("микрофонный", AudioInputSpecsCatalog.Describe(microphone));
        Assert.Contains("линейный", AudioInputSpecsCatalog.Describe(line));
        Assert.Contains("24 бит", AudioInputSpecsCatalog.Describe(line));
    }

    [Fact]
    public void ComboPortMentionsBothDirections()
    {
        var combo = AudioInputSpecsCatalog.For(
            AudioJackType.Headset,
            isInput: true,
            isOutput: true,
            bitDepth: 16);

        var description = AudioInputSpecsCatalog.Describe(combo);

        Assert.Contains("комбо-разъём", description);
        Assert.Contains("микрофонный", description);
        Assert.Contains("+ выход", description);
    }

    [Fact]
    public void OutputDoesNotPretendToHaveInputParameters()
    {
        var output = AudioInputSpecsCatalog.For(
            AudioJackType.Speaker,
            isInput: false,
            isOutput: true,
            bitDepth: 16);

        var description = AudioInputSpecsCatalog.Describe(output);

        Assert.False(output.IsInput);
        Assert.Null(output.MaxVoltageRms);
        Assert.Contains("параметры входа неприменимы", description);
    }

    [Fact]
    public void DigitalPortDoesNotPretendToHaveAnalogParameters()
    {
        var digital = AudioInputSpecsCatalog.For(
            AudioJackType.Digital,
            isInput: true,
            isOutput: false,
            bitDepth: 16);

        var description = AudioInputSpecsCatalog.Describe(digital);

        Assert.Null(digital.MaxVoltageRms);
        Assert.Contains("аналоговые параметры неприменимы", description);
    }
}
