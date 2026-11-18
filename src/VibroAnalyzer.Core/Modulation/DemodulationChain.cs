using VibroAnalyzer.Core.Dsp;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Modulation;

/// <summary>
/// Способ демодуляции.
/// </summary>
public enum DemodulationKind
{
    None,
    Envelope,
    Synchronous
}

/// <summary>
/// Цепь демодуляции: огибающая или синхронное детектирование (I/Q).
/// </summary>
public sealed class DemodulationChain
{
    private const double TwoPi = Math.PI * 2.0;
    private const double DefaultQ = 0.70710678;
    private const double MinCutoffHz = 1e-3;

    private BiquadFilter? _envelopeFilter;
    private BiquadFilter? _iFilter;
    private BiquadFilter? _qFilter;

    private int _sampleRate;
    private double _configuredCutoffHz;
    private double _loPhase;

    /// <summary>
    /// Включение демодуляции.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Вид демодуляции.
    /// </summary>
    public DemodulationKind Kind { get; set; } = DemodulationKind.Envelope;

    /// <summary>
    /// Частота опорного генератора для синхронного детектирования, Гц.
    /// </summary>
    public double CarrierFrequencyHz { get; set; } = 1000.0;

    /// <summary>
    /// Частота среза фильтра нижних частот, Гц.
    /// </summary>
    public double LowPassCutoffHz { get; set; } = 500.0;

    /// <summary>
    /// Номер исходного канала.
    /// </summary>
    public int SourceChannel { get; set; }

    /// <summary>
    /// Максимальная амплитуда, полученная при последней демодуляции.
    /// </summary>
    public double LastAmplitude { get; private set; }

    /// <summary>
    /// Фаза последнего отсчёта при синхронной демодуляции, градусы.
    /// </summary>
    public double LastPhaseDegrees { get; private set; }

    /// <summary>
    /// Создаёт или пересоздаёт фильтры нижних частот для текущей частоты дискретизации.
    /// </summary>
    public void Configure(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        _sampleRate = sampleRate;
        _configuredCutoffHz = LowPassCutoffHz;

        var cutoff = ClampCutoff(sampleRate, LowPassCutoffHz);

        _envelopeFilter = BiquadFilter.CreateLowPass(sampleRate, cutoff, DefaultQ);
        _iFilter = BiquadFilter.CreateLowPass(sampleRate, cutoff, DefaultQ);
        _qFilter = BiquadFilter.CreateLowPass(sampleRate, cutoff, DefaultQ);
    }

    /// <summary>
    /// Демодулирует блок и возвращает массив отсчётов длиной FrameCount.
    /// </summary>
    public double[] Process(SignalBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var channel = Math.Clamp(SourceChannel, 0, block.Channels - 1);

        if (!Enabled || Kind == DemodulationKind.None)
        {
            return block.ToChannelDouble(channel);
        }

        EnsureConfigured(block.SampleRate);

        var source = block.ToChannelDouble(channel);
        var result = new double[source.Length];

        if (Kind == DemodulationKind.Envelope)
        {
            var maximum = 0.0;

            for (var i = 0; i < source.Length; i++)
            {
                var envelope = _envelopeFilter!.Process(Math.Abs(source[i]));
                result[i] = envelope;

                var magnitude = Math.Abs(envelope);
                if (magnitude > maximum)
                {
                    maximum = magnitude;
                }
            }

            LastAmplitude = maximum;
            return result;
        }

        var phaseIncrement = TwoPi * CarrierFrequencyHz / block.SampleRate;
        var maxAmplitude = 0.0;
        var lastPhase = LastPhaseDegrees;

        for (var i = 0; i < source.Length; i++)
        {
            var sample = source[i];
            var cosine = Math.Cos(_loPhase);
            var sine = Math.Sin(_loPhase);

            var iValue = _iFilter!.Process(sample * cosine);
            var qValue = _qFilter!.Process(sample * sine);

            var amplitude = Math.Sqrt(iValue * iValue + qValue * qValue);
            result[i] = amplitude;

            if (amplitude > maxAmplitude)
            {
                maxAmplitude = amplitude;
            }

            lastPhase = Math.Atan2(qValue, iValue) * 180.0 / Math.PI;
            _loPhase = WrapRadians(_loPhase + phaseIncrement);
        }

        LastAmplitude = maxAmplitude;
        LastPhaseDegrees = lastPhase;
        return result;
    }

    /// <summary>
    /// Сбрасывает состояние фильтров, фазу опорного генератора и накопленные значения.
    /// </summary>
    public void Reset()
    {
        _envelopeFilter?.Reset();
        _iFilter?.Reset();
        _qFilter?.Reset();

        _loPhase = 0.0;
        LastAmplitude = 0.0;
        LastPhaseDegrees = 0.0;
    }

    private void EnsureConfigured(int sampleRate)
    {
        if (_envelopeFilter is null ||
            _iFilter is null ||
            _qFilter is null ||
            _sampleRate != sampleRate ||
            !_configuredCutoffHz.Equals(LowPassCutoffHz))
        {
            Configure(sampleRate);
        }
    }

    private static double ClampCutoff(int sampleRate, double cutoffHz)
    {
        var maximum = Math.Max(sampleRate * 0.49, MinCutoffHz);
        var cutoff = double.IsFinite(cutoffHz) ? cutoffHz : MinCutoffHz;
        return Math.Clamp(cutoff, MinCutoffHz, maximum);
    }

    private static double WrapRadians(double phase)
    {
        if (!double.IsFinite(phase))
        {
            return 0.0;
        }

        phase %= TwoPi;
        if (phase < 0.0)
        {
            phase += TwoPi;
        }

        return phase;
    }
}
