using System;
using System.Threading;
using System.Threading.Tasks;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Devices;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Audio.Simulated;

/// <summary>
/// Демонстрационный бэкенд: не требует оборудования и выдаёт тестовый сигнал.
/// Полезен для проверки тракта, анализаторов и сохранения отчётов.
/// </summary>
public sealed class SimulatedAudioBackend : IAudioBackend
{
    private const string BackendName = "Демо";

    private static readonly int[] Rates = { 44100, 48000, 96000 };

    public SimulatedAudioBackend(DemoSignalConfig? config = null)
    {
        Config = (config ?? DemoSignalConfig.CreateDefault()).Clone();
    }

    /// <summary>Конструктор демонстрационного сигнала.</summary>
    public DemoSignalConfig Config { get; }

    private static readonly AudioDeviceInfo InputDevice = new(
        "sim-input",
        "Демо-вход (генератор)",
        BackendName,
        IsInput: true,
        IsOutput: false,
        InputChannels: 2,
        OutputChannels: 0,
        DefaultSampleRate: 48000,
        SupportedSampleRates: Rates,
        Channels: new[]
        {
            new AudioChannelInfo(0, "Вход 1 · конструктор", IsInput: true, AudioJackType.LineIn),
            new AudioChannelInfo(1, "Вход 2 · конструктор", IsInput: true, AudioJackType.LineIn),
        },
        Capabilities: new DeviceCapabilities(
            SupportsDuplex: false,
            IsAcCoupled: true,
            MaxInputVoltageRms: 1.0,
            SupportsExclusiveMode: false,
            SupportsLoopback: true,
            SupportsChannelMapping: true,
            Notes: "Виртуальный генератор с настраиваемым конструктором тонов."));

    private static readonly AudioDeviceInfo OutputDevice = new(
        "sim-output",
        "Демо-выход",
        BackendName,
        IsInput: false,
        IsOutput: true,
        InputChannels: 0,
        OutputChannels: 2,
        DefaultSampleRate: 48000,
        SupportedSampleRates: Rates,
        Channels: new[]
        {
            new AudioChannelInfo(0, "Левый", IsInput: false, AudioJackType.LineOut),
            new AudioChannelInfo(1, "Правый", IsInput: false, AudioJackType.LineOut),
        },
        Capabilities: new DeviceCapabilities(
            SupportsDuplex: false,
            IsAcCoupled: true,
            MaxInputVoltageRms: null,
            SupportsExclusiveMode: false,
            SupportsLoopback: true,
            SupportsChannelMapping: true,
            Notes: "Виртуальный выход: сигнал никуда не воспроизводится."));

    private static readonly AudioDeviceInfo DuplexDevice = new(
        "sim-duplex",
        "Демо-дуплекс",
        BackendName,
        IsInput: true,
        IsOutput: true,
        InputChannels: 2,
        OutputChannels: 2,
        DefaultSampleRate: 48000,
        SupportedSampleRates: Rates,
        Channels: new[]
        {
            new AudioChannelInfo(0, "Вход 1 · 1 кГц", IsInput: true, AudioJackType.LineIn),
            new AudioChannelInfo(1, "Вход 2 · 100 Гц", IsInput: true, AudioJackType.LineIn),
            new AudioChannelInfo(0, "Левый", IsInput: false, AudioJackType.LineOut),
            new AudioChannelInfo(1, "Правый", IsInput: false, AudioJackType.LineOut),
        },
        Capabilities: new DeviceCapabilities(
            SupportsDuplex: true,
            IsAcCoupled: true,
            MaxInputVoltageRms: 1.0,
            SupportsExclusiveMode: false,
            SupportsLoopback: true,
            SupportsChannelMapping: true,
            Notes: "Виртуальный дуплекс: запись последнего выхода подмешивается во вход."));

    public string Name => BackendName;

    public bool IsAvailable => true;

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
        => new[] { InputDevice, OutputDevice, DuplexDevice };

    public IAudioStream OpenStream(AudioStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.HasInput && !options.HasOutput)
        {
            throw new AudioBackendException("Демо-поток должен содержать вход или выход.");
        }

        if (options.HasInput && options.InputChannels <= 0)
        {
            throw new AudioBackendException("Для входа демо-потока нужно хотя бы один канал.");
        }

        if (options.HasOutput && options.OutputChannels <= 0)
        {
            throw new AudioBackendException("Для выхода демо-потока нужен хотя бы один канал.");
        }

        if (options.SampleRate <= 0)
        {
            throw new AudioBackendException("Частота дискретизации должна быть больше нуля.");
        }

        return new SimulatedAudioStream(options, Config.Clone());
    }

    private sealed class SimulatedAudioStream : IAudioStream
    {
        private readonly object _sync = new();
        private readonly object _outputSync = new();
        private readonly AudioStreamOptions _options;
        private readonly DemoSignalConfig _config;
        private readonly DemoTone[] _tones;
        private readonly double[] _phases;
        private readonly double _noiseLevel;
        private readonly double _loopbackLevel;

        private CancellationTokenSource? _cts;
        private Task? _inputTask;
        private float[]? _lastOutput;
        private long _sequence;
        private bool _running;
        private bool _disposed;

        public SimulatedAudioStream(AudioStreamOptions options, DemoSignalConfig config)
        {
            _options = options;
            _config = config;
            _tones = config.Tones.ToArray();
            _phases = _tones.Select(tone => WrapPhase(tone.Phase)).ToArray();
            _noiseLevel = Math.Clamp(config.NoiseLevel, 0.0, 1.0);
            _loopbackLevel = Math.Clamp(config.LoopbackLevel, 0.0, 1.0);
        }

        public AudioStreamOptions Options => _options;

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _running;
                }
            }
        }

        public event EventHandler<SignalBlock>? InputAvailable;

        public event EventHandler<string>? Error;

        public void Start()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_running)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
                _running = true;

                if (_options.HasInput)
                {
                    var token = _cts.Token;
                    _inputTask = Task.Run(() => RunInputAsync(token), CancellationToken.None);
                }
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts;
            Task? task;

            lock (_sync)
            {
                if (!_running && _cts is null)
                {
                    return;
                }

                _running = false;
                cts = _cts;
                _cts = null;
                task = _inputTask;
                _inputTask = null;
            }

            cts?.Cancel();

            if (task is not null)
            {
                try
                {
                    task.Wait(TimeSpan.FromSeconds(1));
                }
                catch (AggregateException)
                {
                    // Остановка не должна выбрасывать исключения в UI.
                }
            }

            cts?.Dispose();
        }

        public bool Write(SignalBlock block)
        {
            ArgumentNullException.ThrowIfNull(block);

            if (!_options.HasOutput || !IsRunning)
            {
                return false;
            }

            lock (_outputSync)
            {
                _lastOutput = (float[])block.Samples.Clone();
            }

            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }

        private async Task RunInputAsync(CancellationToken token)
        {
            var frames = Math.Max(1, _options.BufferFrames);
            var channels = Math.Max(1, _options.InputChannels);
            var period = TimeSpan.FromSeconds((double)frames / _options.SampleRate);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var block = GenerateInputBlock(frames, channels);

                    try
                    {
                        InputAvailable?.Invoke(this, block);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(this, $"Подписчик демо-входа: {ex.Message}");
                    }

                    await Task.Delay(period, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Штатная остановка.
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Демо-вход остановлен: {ex.Message}");
            }
        }

        private SignalBlock GenerateInputBlock(int frames, int channels)
        {
            var samples = new float[frames * channels];
            var sampleRate = _options.SampleRate;
            var noiseAmplitude = _noiseLevel * 0.5;

            float[]? loop = null;
            lock (_outputSync)
            {
                if (_lastOutput is { Length: > 0 })
                {
                    loop = (float[])_lastOutput.Clone();
                }
            }

            var toneValues = new double[_tones.Length];

            for (var frame = 0; frame < frames; frame++)
            {
                for (var toneIndex = 0; toneIndex < _tones.Length; toneIndex++)
                {
                    var tone = _tones[toneIndex];
                    toneValues[toneIndex] = RenderWaveform(tone.Waveform, _phases[toneIndex]) * tone.Amplitude;
                    _phases[toneIndex] = WrapPhase(
                        _phases[toneIndex] + Math.Max(0.0, tone.FrequencyHz) / sampleRate);
                }

                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = 0.0;

                    for (var toneIndex = 0; toneIndex < _tones.Length; toneIndex++)
                    {
                        var tone = _tones[toneIndex];
                        if (tone.Channel < 0 || tone.Channel == channel)
                        {
                            sample += toneValues[toneIndex];
                        }
                    }

                    if (noiseAmplitude > 0.0)
                    {
                        sample += (Random.Shared.NextDouble() - 0.5) * 2.0 * noiseAmplitude;
                    }

                    if (loop is { Length: > 0 } && _loopbackLevel > 0.0)
                    {
                        var loopChannels = Math.Max(1, _options.OutputChannels);
                        var index = frame * loopChannels + channel % loopChannels;
                        sample += _loopbackLevel * loop[index % loop.Length];
                    }

                    samples[frame * channels + channel] = (float)Math.Clamp(sample, -1.0, 1.0);
                }
            }

            return new SignalBlock(
                samples,
                channels,
                sampleRate,
                Interlocked.Increment(ref _sequence),
                DateTimeOffset.UtcNow);
        }

        private static double RenderWaveform(CarrierWaveform waveform, double phase)
        {
            var p = WrapPhase(phase);

            return waveform switch
            {
                CarrierWaveform.Square => p < 0.5 ? 1.0 : -1.0,
                CarrierWaveform.Triangle => p < 0.25
                    ? 4.0 * p
                    : p < 0.75
                        ? 2.0 - 4.0 * p
                        : 4.0 * p - 4.0,
                _ => Math.Sin(2.0 * Math.PI * p),
            };
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
}
