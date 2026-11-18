using System;
using System.Threading;
using System.Threading.Tasks;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Devices;
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
            new AudioChannelInfo(0, "Вход 1 · 1 кГц", IsInput: true, AudioJackType.LineIn),
            new AudioChannelInfo(1, "Вход 2 · 100 Гц", IsInput: true, AudioJackType.LineIn),
        },
        Capabilities: new DeviceCapabilities(
            SupportsDuplex: false,
            IsAcCoupled: true,
            MaxInputVoltageRms: 1.0,
            SupportsExclusiveMode: false,
            SupportsLoopback: true,
            SupportsChannelMapping: true,
            Notes: "Виртуальный генератор: канал 1 — 1/3 кГц, канал 2 — 100/500 Гц."));

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

        return new SimulatedAudioStream(options);
    }

    private sealed class SimulatedAudioStream : IAudioStream
    {
        private readonly object _sync = new();
        private readonly object _outputSync = new();
        private readonly AudioStreamOptions _options;

        private CancellationTokenSource? _cts;
        private Task? _inputTask;
        private float[]? _lastOutput;
        private long _sequence;
        private bool _running;
        private bool _disposed;

        private double _phase1000;
        private double _phase3000;
        private double _phase50;
        private double _phase100;
        private double _phase500;

        public SimulatedAudioStream(AudioStreamOptions options)
        {
            _options = options;
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

            float[]? loop = null;
            lock (_outputSync)
            {
                if (_lastOutput is { Length: > 0 })
                {
                    loop = (float[])_lastOutput.Clone();
                }
            }

            var sampleRate = _options.SampleRate;
            var step1000 = 2 * Math.PI * 1000 / sampleRate;
            var step3000 = 2 * Math.PI * 3000 / sampleRate;
            var step50 = 2 * Math.PI * 50 / sampleRate;
            var step100 = 2 * Math.PI * 100 / sampleRate;
            var step500 = 2 * Math.PI * 500 / sampleRate;

            for (var frame = 0; frame < frames; frame++)
            {
                var noise = (Random.Shared.NextDouble() - 0.5) * 0.004;
                var value0 = 0.30 * Math.Sin(_phase1000)
                             + 0.06 * Math.Sin(_phase3000)
                             + 0.02 * Math.Sin(_phase50)
                             + noise;
                var value1 = 0.25 * Math.Sin(_phase100)
                             + 0.10 * Math.Sin(_phase500)
                             + noise * 0.5;

                _phase1000 += step1000;
                _phase3000 += step3000;
                _phase50 += step50;
                _phase100 += step100;
                _phase500 += step500;

                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = channel % 2 == 0 ? value0 : value1;

                    if (loop is { Length: > 0 })
                    {
                        var loopChannels = Math.Max(1, _options.OutputChannels);
                        var index = frame * loopChannels + channel % loopChannels;
                        sample += 0.15 * loop[index % loop.Length];
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
    }
}
