using VibroAnalyzer.Core.Modulation;

namespace VibroAnalyzer.Core.Audio;

/// <summary>
/// Один тон демонстрационного конструктора сигнала.
/// </summary>
public sealed class DemoTone
{
    public double FrequencyHz { get; set; } = 1000.0;

    public double Amplitude { get; set; } = 0.2;

    public CarrierWaveform Waveform { get; set; } = CarrierWaveform.Sine;

    public double Phase { get; set; }

    /// <summary>Канал устройства; −1 — все каналы.</summary>
    public int Channel { get; set; } = -1;

    public DemoTone Clone() => new()
    {
        FrequencyHz = FrequencyHz,
        Amplitude = Amplitude,
        Waveform = Waveform,
        Phase = Phase,
        Channel = Channel,
    };
}

/// <summary>
/// Настройки демонстрационного генератора: набор тонов, шум и петля выхода.
/// </summary>
public sealed class DemoSignalConfig
{
    public List<DemoTone> Tones { get; set; } = new();

    public double NoiseLevel { get; set; } = 0.004;

    /// <summary>Доля последнего выходного блока, подмешиваемая во вход.</summary>
    public double LoopbackLevel { get; set; }

    public DemoSignalConfig Clone() => new()
    {
        NoiseLevel = NoiseLevel,
        LoopbackLevel = LoopbackLevel,
        Tones = Tones.Select(tone => tone.Clone()).ToList(),
    };

    public static DemoSignalConfig CreateDefault() => new()
    {
        NoiseLevel = 0.004,
        LoopbackLevel = 0.0,
        Tones = new List<DemoTone>
        {
            new() { FrequencyHz = 1000, Amplitude = 0.30, Channel = 0 },
            new() { FrequencyHz = 3000, Amplitude = 0.06, Channel = 0 },
            new() { FrequencyHz = 50, Amplitude = 0.02, Channel = 0 },
            new() { FrequencyHz = 100, Amplitude = 0.25, Channel = 1 },
            new() { FrequencyHz = 500, Amplitude = 0.10, Channel = 1 },
        },
    };
}
