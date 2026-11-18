using VibroAnalyzer.Core.Modulation;

namespace VibroAnalyzer.Core.Routing;

/// <summary>
/// Логический выход: одна модуляционная цепь, привязанная к физическому
/// каналу аудиоустройства. Несколько выходов могут делить один канал —
/// тогда их сигналы складываются.
/// </summary>
public sealed class OutputBus
{
    public OutputBus(string name, string deviceId, int physicalChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (physicalChannel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalChannel));
        }

        Name = name;
        DeviceId = deviceId;
        PhysicalChannel = physicalChannel;
        Modulation = new ModulationChain();
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; }

    public string DeviceId { get; }

    public int PhysicalChannel { get; }

    /// <summary>Признак участия выхода в миксе устройства.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Усиление выхода перед микшированием.</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>Цепь несущей и модуляции этого выхода.</summary>
    public ModulationChain Modulation { get; }

    /// <summary>Краткое описание для интерфейса.</summary>
    public string Description => $"канал {PhysicalChannel + 1}";

    /// <summary>Формирует моно-блок выхода.</summary>
    public float[] GenerateMonoBlock(int sampleRate, int frames)
    {
        if (frames <= 0)
        {
            return Array.Empty<float>();
        }

        var block = Modulation.GenerateBlock(sampleRate, frames, 1);
        var result = new float[frames];
        var gain = !Enabled || !double.IsFinite(Gain) ? 0.0 : Gain;

        for (var i = 0; i < frames; i++)
        {
            result[i] = (float)Math.Clamp(block.Samples[i] * gain, -1.0, 1.0);
        }

        return result;
    }
}

/// <summary>
/// Собирает моно-сигналы логических выходов в интерливленный блок устройства.
/// </summary>
public static class OutputMixer
{
    public static float[] Mix(
        IReadOnlyList<OutputBus> buses,
        int frames,
        int sampleRate,
        int deviceChannels)
    {
        ArgumentNullException.ThrowIfNull(buses);

        if (frames <= 0 || deviceChannels <= 0)
        {
            return Array.Empty<float>();
        }

        var result = new float[frames * deviceChannels];

        foreach (var bus in buses)
        {
            if (!bus.Enabled || bus.PhysicalChannel >= deviceChannels)
            {
                continue;
            }

            var mono = bus.GenerateMonoBlock(sampleRate, frames);
            var offset = bus.PhysicalChannel;

            for (var frame = 0; frame < frames; frame++)
            {
                result[frame * deviceChannels + offset] += mono[frame];
            }
        }

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Math.Clamp(result[i], -1.0f, 1.0f);
        }

        return result;
    }
}
