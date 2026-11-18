using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Modulation;

/// <summary>
/// Цепь амплитудной модуляции: несущая и внутренняя синусоидальная база.
/// </summary>
public sealed class ModulationChain
{
    private const double TwoPi = Math.PI * 2.0;

    private long _sequence;
    private double _basebandPhase;

    public ModulationChain()
    {
        Carrier = new CarrierGenerator();
    }

    /// <summary>
    /// Включение выхода. При выключении генерируются нулевые блоки.
    /// </summary>
    public bool OutputEnabled { get; set; } = true;

    /// <summary>
    /// Включение амплитудной модуляции.
    /// </summary>
    public bool Modulate { get; set; }

    /// <summary>
    /// Глубина модуляции m в формуле carrier * (1 + m * baseband).
    /// </summary>
    public double ModulationIndex { get; set; } = 0.5;

    /// <summary>
    /// Частота внутренней синусоидальной базы в герцах.
    /// </summary>
    public double BasebandFrequencyHz { get; set; } = 100.0;

    /// <summary>
    /// Амплитуда внутренней синусоидальной базы.
    /// </summary>
    public double BasebandAmplitude { get; set; } = 0.5;

    /// <summary>
    /// Генератор несущей.
    /// </summary>
    public CarrierGenerator Carrier { get; }

    /// <summary>
    /// Сбрасывает генератор несущей, фазу базы и счётчик последовательности.
    /// </summary>
    public void Reset()
    {
        Carrier.Reset();
        _basebandPhase = 0.0;
        _sequence = 0;
    }

    /// <summary>
    /// Формирует блок сигнала: моно-несущая дублируется во все каналы.
    /// </summary>
    public SignalBlock GenerateBlock(int sampleRate, int frames, int channels, DateTimeOffset? timestamp = null)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        if (frames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), "Число кадров не может быть отрицательным.");
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Число каналов должно быть больше нуля.");
        }

        var samples = new float[frames * channels];

        if (OutputEnabled)
        {
            var modulationIndex = ModulationIndex;
            var basebandAmplitude = BasebandAmplitude;
            var basebandIncrement = BasebandFrequencyHz / sampleRate;

            for (var frame = 0; frame < frames; frame++)
            {
                var carrierSample = Carrier.NextSample(sampleRate);
                var value = carrierSample;

                if (Modulate)
                {
                    var baseband = Math.Sin(TwoPi * _basebandPhase) * basebandAmplitude;
                    value = carrierSample * (1.0 + modulationIndex * baseband);
                    _basebandPhase = WrapPhase(_basebandPhase + basebandIncrement);
                }

                value = Math.Clamp(value, -1.0, 1.0);
                var sample = (float)value;
                var offset = frame * channels;

                for (var channel = 0; channel < channels; channel++)
                {
                    samples[offset + channel] = sample;
                }
            }
        }

        var sequence = _sequence++;
        return new SignalBlock(samples, channels, sampleRate, sequence, timestamp);
    }

    private static double WrapPhase(double phase)
    {
        if (!double.IsFinite(phase))
        {
            return 0.0;
        }

        phase -= Math.Floor(phase);
        return phase >= 1.0 ? 0.0 : phase;
    }
}
