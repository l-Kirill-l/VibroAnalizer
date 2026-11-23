using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Devices;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Audio.PipeWire;

/// <summary>
/// Аудиобэкенд PipeWire (Linux) через утилиты pactl/parec/pacat.
/// Устройства перечисляются по JSON-выводу pactl, вход и выход открываются
/// раздельными процессами, поэтому возможен дрейф частоты дискретизации.
/// </summary>
public sealed class PipeWireAudioBackend : IAudioBackend, IAudioPortControl
{
    /// <summary>Имя бэкенда для интерфейса.</summary>
    public const string BackendName = "PipeWire";

    private const int DefaultSampleRate = 48000;

    private static readonly IReadOnlyList<int> SampleRates =
        Array.AsReadOnly(new[] { 44100, 48000, 96000 });

    private static readonly Lazy<bool> Availability = new(CheckAvailability, LazyThreadSafetyMode.ExecutionAndPublication);

    public string Name => BackendName;

    /// <summary>Бэкенд доступен, если в PATH есть и parec, и pacat.</summary>
    public bool IsAvailable => Availability.Value;

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        if (!OperatingSystem.IsLinux())
        {
            return Array.Empty<AudioDeviceInfo>();
        }

        var devices = new List<AudioDeviceInfo>();
        var errors = new List<string>();
        var cards = PipeWireCardReader.Read();

        AppendDevices(devices, "sources", isInput: true, errors, cards);
        AppendDevices(devices, "sinks", isInput: false, errors, cards);

        if (devices.Count == 0 && errors.Count > 0)
        {
            throw new AudioBackendException("Не удалось получить список устройств PipeWire: " + string.Join("; ", errors));
        }

        return devices;
    }

    public IAudioStream OpenStream(AudioStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PipeWireAudioStream(options);
    }

    private static void AppendDevices(
        List<AudioDeviceInfo> devices,
        string listKind,
        bool isInput,
        List<string> errors,
        IReadOnlyDictionary<string, PipeWireCard> cards)
    {
        var json = RunPactl(new[] { "-f", "json", "list", listKind }, out var error);
        if (json is null)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                errors.Add(error);
            }

            return;
        }

        try
        {
            ParseDevices(json, isInput, devices, cards);
        }
        catch (JsonException ex)
        {
            errors.Add($"Некорректный JSON pactl ({listKind}): {ex.Message}");
        }
        catch (Exception ex)
        {
            errors.Add($"Разбор списка {listKind}: {ex.Message}");
        }
    }

    private static string? RunPactl(string[] arguments, out string error)
        => RunTool("pactl", arguments, out error);

    internal static string? RunTool(
        string fileName,
        IReadOnlyList<string> arguments,
        out string error)
    {
        error = "";

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                error = $"{fileName} не запустился.";
                return null;
            }

            // Читаем оба потока одновременно, чтобы не заблокировать процесс.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Процесс мог завершиться самостоятельно.
                }

                error = $"{fileName} не ответил за 5 секунд.";
                return null;
            }

            var output = outputTask.GetAwaiter().GetResult();
            var stderr = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? $"{fileName} завершился с кодом {process.ExitCode}."
                    : $"{fileName}: {stderr.Trim()}";
                return null;
            }

            return output;
        }
        catch (Exception ex)
        {
            error = $"{fileName}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Переключает физический разъём источника или стока средствами pactl.
    /// Например, встроенный микрофон ↔ микрофон гарнитуры.
    /// </summary>
    public bool TrySelectPort(string deviceId, string portId, bool isInput, out string error)
    {
        if (!OperatingSystem.IsLinux())
        {
            error = "Выбор разъёма доступен только в PipeWire.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(portId))
        {
            error = "Не задано устройство или разъём.";
            return false;
        }

        var arguments = isInput
            ? new[] { "set-source-port", deviceId, portId }
            : new[] { "set-sink-port", deviceId, portId };

        return RunPactl(arguments, out error) is not null;
    }

    private static void ParseDevices(
        string json,
        bool isInput,
        List<AudioDeviceInfo> devices,
        IReadOnlyDictionary<string, PipeWireCard> cards)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Clean(GetString(item, "name"));
            if (id.Length == 0)
            {
                continue;
            }

            var properties = item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                ? props
                : default;

            var description = Clean(GetString(properties, "device.description"));
            if (description.Length == 0)
            {
                description = Clean(GetString(properties, "node.description"));
            }

            if (description.Length == 0)
            {
                description = Clean(GetString(properties, "description"));
            }

            if (description.Length == 0)
            {
                description = Clean(GetString(item, "description"));
            }

            if (description.Length == 0)
            {
                description = id;
            }

            var cardName = Clean(GetString(properties, "device.name"));
            cards.TryGetValue(cardName, out var card);

            var activePortId = Clean(GetString(item, "active_port"));
            var activeRoute = card?.Routes.FirstOrDefault(route =>
                string.Equals(route.Id, activePortId, StringComparison.Ordinal));

            var channelMapRaw = Clean(GetString(item, "channel_map"));
            if (channelMapRaw.Length == 0)
            {
                channelMapRaw = Clean(GetString(properties, "channel_map"));
            }

            var channelNames = channelMapRaw.Length == 0
                ? Array.Empty<string>()
                : channelMapRaw.Split(',');

            var channels = 0;
            var channelsRaw = Clean(GetString(properties, "audio.channels"));
            if (int.TryParse(channelsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedChannels)
                && parsedChannels > 0)
            {
                channels = parsedChannels;
            }

            if (channels <= 0 && channelNames.Length > 0)
            {
                channels = channelNames.Length;
            }

            if (channels <= 0)
            {
                channels = 2;
            }

            var isMonitor = id.Contains(".monitor", StringComparison.OrdinalIgnoreCase);

            var searchText = string.Join(
                ' ',
                new[]
                {
                    id,
                    description,
                    Clean(GetString(item, "description")),
                    Clean(GetString(properties, "device.description")),
                    Clean(GetString(properties, "node.description")),
                });

            var isMicrophone = searchText.Contains("mic", StringComparison.OrdinalIgnoreCase);
            var nodeNick = Clean(GetString(properties, "node.nick"));
            var controllerName = !string.IsNullOrWhiteSpace(nodeNick)
                                 && !description.Contains(nodeNick, StringComparison.OrdinalIgnoreCase)
                ? nodeNick
                : description;
            var bitDepth = ParseBitDepth(GetString(properties, "alsa.resolution_bits"));
            var supportsDuplex = card is not null
                                 && card.Routes.Any(route => route.IsInput)
                                 && card.Routes.Any(route => !route.IsInput);
            var ports = !isMonitor && card is { Routes.Count: > 0 }
                ? AudioPortTopology.Build(card.Routes)
                : Array.Empty<AudioPortInfo>();

            void AddDevice(
                string deviceId,
                string streamId,
                string portId,
                string displayName,
                AudioJackType jackType)
            {
                var isDigital = jackType is AudioJackType.Digital or AudioJackType.Monitor;
                var channelInfos = new List<AudioChannelInfo>(channels);

                for (var index = 0; index < channels; index++)
                {
                    var channelName = DescribeChannelName(
                        index < channelNames.Length ? channelNames[index] : "",
                        index);
                    channelInfos.Add(new AudioChannelInfo(
                        index,
                        channelName,
                        isInput,
                        jackType));
                }

                devices.Add(new AudioDeviceInfo(
                    deviceId,
                    displayName,
                    BackendName,
                    IsInput: isInput,
                    IsOutput: !isInput,
                    InputChannels: isInput ? channels : 0,
                    OutputChannels: isInput ? 0 : channels,
                    DefaultSampleRate,
                    SampleRates,
                    channelInfos,
                    new DeviceCapabilities(
                        SupportsDuplex: supportsDuplex,
                        IsAcCoupled: !isDigital,
                        MaxInputVoltageRms: isDigital ? null : 1.0,
                        SupportsExclusiveMode: false,
                        SupportsLoopback: true,
                        SupportsChannelMapping: true,
                        Notes: isDigital
                            ? "Цифровой интерфейс: параметры 3,5-мм аналогового входа неприменимы."
                            : "Раздельные потоки входа/выхода; возможен дрейф частоты — используйте ресинхронизацию.",
                        MaxInputCurrentMa: isDigital ? null : 0.1,
                        InputImpedanceOhms: isDigital ? null : 10000,
                        MinFrequencyHz: 20,
                        MaxFrequencyHz: Math.Min(20000, DefaultSampleRate * 0.5),
                        BitDepth: bitDepth),
                    IsMonitor: isMonitor)
                {
                    CardId = cardName,
                    StreamId = streamId,
                    PortId = portId,
                    ControllerName = controllerName,
                    ActivePortId = activePortId,
                    Ports = ports,
                });
            }

            if (isMonitor)
            {
                AddDevice(
                    id,
                    streamId: id,
                    portId: "",
                    displayName: MakeUniqueName(
                        devices,
                        BuildDeviceName(isInput, isMonitor: true, description, nodeNick)),
                    AudioJackType.Monitor);
                continue;
            }

            var routes = card?.Routes
                .Where(route => route.IsInput == isInput)
                .ToList() ?? new List<AudioPortRoute>();

            if (routes.Count == 0)
            {
                var jackType = activeRoute?.JackType
                    ?? (isInput
                        ? (isMicrophone ? AudioJackType.Microphone : AudioJackType.LineIn)
                        : AudioJackType.LineOut);

                AddDevice(
                    id,
                    streamId: id,
                    portId: "",
                    displayName: MakeUniqueName(
                        devices,
                        BuildDeviceName(isInput, isMonitor: false, description, nodeNick)),
                    jackType);
                continue;
            }

            // Один физический разъём — одно логическое устройство в основном списке.
            // Поток у них общий (StreamId), но выбранный порт различается.
            foreach (var route in routes)
            {
                AddDevice(
                    $"{id}#{route.Id}",
                    streamId: id,
                    portId: route.Id,
                    displayName: MakeUniqueName(
                        devices,
                        BuildDeviceName(
                            isInput,
                            isMonitor: false,
                            description,
                            nodeNick,
                            route.Name)),
                    route.JackType);
            }
        }
    }

    private static string BuildDeviceName(
        bool isInput,
        bool isMonitor,
        string description,
        string nodeNick,
        string? portName = null)
    {
        var prefix = isMonitor ? "Монитор" : isInput ? "Вход" : "Выход";
        var parts = new List<string> { prefix };

        if (!string.IsNullOrWhiteSpace(portName))
        {
            parts.Add(portName);
        }
        else if (!string.IsNullOrWhiteSpace(nodeNick)
                 && !description.Contains(nodeNick, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(nodeNick);
        }

        parts.Add(description);
        return string.Join(" · ", parts);
    }

    private static string MakeUniqueName(List<AudioDeviceInfo> devices, string name)
    {
        if (!devices.Any(device => string.Equals(device.Name, name, StringComparison.Ordinal)))
        {
            return name;
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{name} ({index})";
            if (!devices.Any(device => string.Equals(device.Name, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }
    }

    private static string DescribeChannelName(string raw, int index)
    {
        var name = Clean(raw);
        if (name.Length == 0)
        {
            return $"Канал {index + 1}";
        }

        return name.ToLowerInvariant() switch
        {
            "front-left" or "fl" => "Левый",
            "front-right" or "fr" => "Правый",
            "mono" => "Моно",
            "front-center" or "fc" => "Центр",
            "rear-left" or "rl" => "Левый тыл",
            "rear-right" or "rr" => "Правый тыл",
            "lfe" => "Сабвуфер",
            _ => name,
        };
    }

    private static int? ParseBitDepth(string value)
        => int.TryParse(value, out var bits) && bits > 0 ? bits : null;

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString() ?? "";
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var trimmed = value.Trim();
        return string.Equals(trimmed, "(null)", StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
    }

    private static bool CheckAvailability()
    {
        return OperatingSystem.IsLinux()
               && FindInPath("parec") is not null
               && FindInPath("pacat") is not null;
    }

    private static string? FindInPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Некорректный элемент PATH пропускаем.
            }
        }

        return null;
    }

    /// <summary>
    /// Поток PipeWire: parec для входа и pacat для выхода.
    /// Процессы работают раздельно, входной и выходной темпы не синхронизированы.
    /// </summary>
    private sealed class PipeWireAudioStream : IAudioStream
    {
        private const int OutputQueueCapacity = 8;
        private const int StderrLineLimit = 20;
        private const int ShutdownWaitMilliseconds = 500;
        private const int StderrDrainMilliseconds = 200;

        private readonly object _sync = new();
        private readonly object _stderrSync = new();
        private readonly AudioStreamOptions _options;
        private readonly int _sampleRate;
        private readonly int _inputChannels;
        private readonly int _outputChannels;
        private readonly int _bufferFrames;
        private readonly int _inputBlockBytes;
        private readonly Queue<string> _errorLines = new();

        private CancellationTokenSource? _cts;
        private Task? _inputTask;
        private Task? _outputTask;
        private Task? _inputStderrTask;
        private Task? _outputStderrTask;
        private Process? _inputProcess;
        private Process? _outputProcess;
        private Channel<SignalBlock>? _outputQueue;
        private volatile bool _running;
        private volatile bool _disposed;
        private long _sequence;

        public PipeWireAudioStream(AudioStreamOptions options)
        {
            _options = options;
            _sampleRate = options.SampleRate > 0 ? options.SampleRate : DefaultSampleRate;
            _inputChannels = Math.Clamp(options.InputChannels, 0, 64);
            _outputChannels = Math.Clamp(options.OutputChannels, 0, 64);
            _bufferFrames = Math.Clamp(options.BufferFrames, 1, 65536);
            _inputBlockBytes = checked(_bufferFrames * _inputChannels * sizeof(float));
        }

        public AudioStreamOptions Options => _options;

        public bool IsRunning => _running;

        public event EventHandler<SignalBlock>? InputAvailable;

        public event EventHandler<string>? Error;

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _running)
                {
                    return;
                }

                if (_inputChannels <= 0 && _outputChannels <= 0)
                {
                    return;
                }

                ClearErrorLines();

                var cts = new CancellationTokenSource();
                Process? inputProcess = null;
                Process? outputProcess = null;
                Task? inputTask = null;
                Task? outputTask = null;
                Task? inputStderrTask = null;
                Task? outputStderrTask = null;
                Channel<SignalBlock>? outputQueue = null;

                try
                {
                    var token = cts.Token;

                    if (_inputChannels > 0)
                    {
                        var process = CreateInputProcess();
                        process.Start();
                        inputProcess = process;

                        var stderrTask = Task.Run(
                            () => ReadStderrAsync(process.StandardError, "parec", token));
                        inputStderrTask = stderrTask;
                        inputTask = Task.Run(() => ReadInputLoopAsync(process, stderrTask, token));
                    }

                    if (_outputChannels > 0)
                    {
                        var queue = Channel.CreateBounded<SignalBlock>(new BoundedChannelOptions(OutputQueueCapacity)
                        {
                            FullMode = BoundedChannelFullMode.DropOldest,
                            SingleReader = true,
                            SingleWriter = false,
                        });
                        outputQueue = queue;

                        var process = CreateOutputProcess();
                        process.Start();
                        outputProcess = process;

                        var stderrTask = Task.Run(
                            () => ReadStderrAsync(process.StandardError, "pacat", token));
                        outputStderrTask = stderrTask;
                        outputTask = Task.Run(() => WriteOutputLoopAsync(process, queue, stderrTask, token));
                    }

                    _cts = cts;
                    _inputProcess = inputProcess;
                    _outputProcess = outputProcess;
                    _inputTask = inputTask;
                    _outputTask = outputTask;
                    _inputStderrTask = inputStderrTask;
                    _outputStderrTask = outputStderrTask;
                    _outputQueue = outputQueue;
                    _running = true;
                }
                catch (Exception ex)
                {
                    Shutdown(cts, inputProcess, outputProcess, inputTask, outputTask, inputStderrTask, outputStderrTask);

                    _cts = null;
                    _inputProcess = null;
                    _outputProcess = null;
                    _inputTask = null;
                    _outputTask = null;
                    _inputStderrTask = null;
                    _outputStderrTask = null;
                    _outputQueue = null;
                    _running = false;

                    var message = $"Не удалось запустить поток PipeWire: {ex.Message}";
                    PublishError(message);
                    throw new AudioBackendException(message, ex);
                }
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts;
            Task? inputTask;
            Task? outputTask;
            Task? inputStderrTask;
            Task? outputStderrTask;
            Process? inputProcess;
            Process? outputProcess;
            Channel<SignalBlock>? outputQueue;

            lock (_sync)
            {
                if (!_running
                    && _cts is null
                    && _inputProcess is null
                    && _outputProcess is null
                    && _inputTask is null
                    && _outputTask is null
                    && _outputQueue is null)
                {
                    return;
                }

                _running = false;
                cts = _cts;
                _cts = null;
                inputTask = _inputTask;
                _inputTask = null;
                outputTask = _outputTask;
                _outputTask = null;
                inputStderrTask = _inputStderrTask;
                _inputStderrTask = null;
                outputStderrTask = _outputStderrTask;
                _outputStderrTask = null;
                inputProcess = _inputProcess;
                _inputProcess = null;
                outputProcess = _outputProcess;
                _outputProcess = null;
                outputQueue = _outputQueue;
                _outputQueue = null;
            }

            outputQueue?.Writer.TryComplete();
            Shutdown(cts, inputProcess, outputProcess, inputTask, outputTask, inputStderrTask, outputStderrTask);
        }

        public bool Write(SignalBlock block)
        {
            if (block is null)
            {
                return false;
            }

            lock (_sync)
            {
                if (_disposed || !_running || _outputChannels <= 0)
                {
                    return false;
                }

                var queue = _outputQueue;
                if (queue is null)
                {
                    return false;
                }

                // Копируем отсчёты: вызывающий код может переиспользовать свой буфер.
                var copy = new SignalBlock(
                    (float[])block.Samples.Clone(),
                    block.Channels,
                    block.SampleRate,
                    block.Sequence,
                    block.Timestamp);

                return queue.Writer.TryWrite(copy);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();

            InputAvailable = null;
            Error = null;
        }

        private Process CreateInputProcess()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "parec",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Пустой идентификатор означает устройство ввода по умолчанию.
            if (!string.IsNullOrWhiteSpace(_options.InputDeviceId))
            {
                startInfo.ArgumentList.Add($"--device={_options.InputDeviceId.Trim()}");
            }

            startInfo.ArgumentList.Add("--format=float32le");
            startInfo.ArgumentList.Add($"--rate={_sampleRate}");
            startInfo.ArgumentList.Add($"--channels={_inputChannels}");
            startInfo.ArgumentList.Add("--latency-msec=20");
            startInfo.ArgumentList.Add("--raw");

            return new Process { StartInfo = startInfo };
        }

        private Process CreateOutputProcess()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pacat",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("--playback");

            if (!string.IsNullOrWhiteSpace(_options.OutputDeviceId))
            {
                startInfo.ArgumentList.Add($"--device={_options.OutputDeviceId.Trim()}");
            }

            startInfo.ArgumentList.Add("--format=float32le");
            startInfo.ArgumentList.Add($"--rate={_sampleRate}");
            startInfo.ArgumentList.Add($"--channels={_outputChannels}");
            startInfo.ArgumentList.Add("--latency-msec=20");
            startInfo.ArgumentList.Add("--raw");

            return new Process { StartInfo = startInfo };
        }

        private async Task ReadInputLoopAsync(Process process, Task stderrTask, CancellationToken token)
        {
            var blockBytes = _inputBlockBytes;
            var capacity = Math.Max(blockBytes * 4, 16384);
            // Ёмкость кратна размеру блока, чтобы остаток всегда помещался после переноса.
            capacity = (capacity + blockBytes - 1) / blockBytes * blockBytes;

            var buffer = new byte[capacity];
            var filled = 0;

            try
            {
                var stream = process.StandardOutput.BaseStream;
                while (!token.IsCancellationRequested)
                {
                    var read = await stream
                        .ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), token)
                        .ConfigureAwait(false);

                    if (read <= 0)
                    {
                        break;
                    }

                    filled += read;
                    filled = EmitInputBlocks(buffer, filled, blockBytes, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Штатная остановка.
            }
            catch (ObjectDisposedException)
            {
                // Поток закрыт при остановке.
            }
            catch (IOException ex) when (!token.IsCancellationRequested)
            {
                PublishError(BuildProcessError($"Чтение parec: {ex.Message}"));
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                PublishError(BuildProcessError($"Чтение parec: {ex.Message}"));
            }

            await ReportExitAsync(process, "parec", stderrTask, token).ConfigureAwait(false);
        }

        private int EmitInputBlocks(byte[] buffer, int filled, int blockBytes, CancellationToken token)
        {
            var offset = 0;

            while (filled - offset >= blockBytes)
            {
                var samples = new float[_bufferFrames * _inputChannels];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadSingleLittleEndian(
                        buffer.AsSpan(offset + i * sizeof(float), sizeof(float)));
                }

                var block = new SignalBlock(
                    samples,
                    _inputChannels,
                    _sampleRate,
                    Interlocked.Increment(ref _sequence),
                    DateTimeOffset.UtcNow);

                offset += blockBytes;
                RaiseInput(block, token);
            }

            var remaining = filled - offset;
            if (remaining > 0)
            {
                // Остаток байтов переносим в начало буфера.
                Buffer.BlockCopy(buffer, offset, buffer, 0, remaining);
            }

            return remaining;
        }

        private async Task WriteOutputLoopAsync(
            Process process,
            Channel<SignalBlock> queue,
            Task stderrTask,
            CancellationToken token)
        {
            var period = TimeSpan.FromSeconds((double)_bufferFrames / _sampleRate);
            var bytesPerBlock = checked(_bufferFrames * _outputChannels * sizeof(float));
            var silence = new byte[bytesPerBlock];
            var scratch = new byte[bytesPerBlock];
            var clock = Stopwatch.StartNew();
            var next = TimeSpan.Zero;

            try
            {
                var stdin = process.StandardInput.BaseStream;

                while (!token.IsCancellationRequested)
                {
                    var block = DrainLatest(queue);
                    var data = silence;

                    if (block is not null)
                    {
                        EncodeBlock(block, scratch, bytesPerBlock);
                        data = scratch;
                    }

                    await stdin
                        .WriteAsync(data.AsMemory(0, bytesPerBlock), token)
                        .ConfigureAwait(false);
                    await stdin.FlushAsync(token).ConfigureAwait(false);

                    next += period;
                    var delay = next - clock.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    else if (clock.Elapsed - next > period * 8)
                    {
                        // Сильно отстали — выравниваем темп, чтобы не копить задержку.
                        clock.Restart();
                        next = TimeSpan.Zero;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Штатная остановка.
            }
            catch (ObjectDisposedException)
            {
                // Поток закрыт при остановке.
            }
            catch (IOException ex) when (!token.IsCancellationRequested)
            {
                // Если процесс уже упал, подробности сообщит ReportExitAsync.
                if (!HasFailedExit(process))
                {
                    PublishError(BuildProcessError($"Запись в pacat: {ex.Message}"));
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                PublishError(BuildProcessError($"Запись в pacat: {ex.Message}"));
            }

            await ReportExitAsync(process, "pacat", stderrTask, token).ConfigureAwait(false);
        }

        private void EncodeBlock(SignalBlock block, byte[] destination, int bytesPerBlock)
        {
            Array.Clear(destination, 0, bytesPerBlock);

            var frames = Math.Min(block.FrameCount, _bufferFrames);
            var sourceChannels = block.Channels;

            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < _outputChannels; channel++)
                {
                    // При несовпадении числа каналов повторяем последний доступный.
                    var sourceChannel = Math.Min(channel, sourceChannels - 1);
                    var value = block.Samples[frame * sourceChannels + sourceChannel];
                    BinaryPrimitives.WriteSingleLittleEndian(
                        destination.AsSpan((frame * _outputChannels + channel) * sizeof(float), sizeof(float)),
                        value);
                }
            }
        }

        private static SignalBlock? DrainLatest(Channel<SignalBlock> queue)
        {
            SignalBlock? latest = null;
            while (queue.Reader.TryRead(out var block))
            {
                latest = block;
            }

            return latest;
        }

        private async Task ReadStderrAsync(StreamReader reader, string tool, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    lock (_stderrSync)
                    {
                        _errorLines.Enqueue($"{tool}: {line.Trim()}");
                        while (_errorLines.Count > StderrLineLimit)
                        {
                            _errorLines.Dequeue();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Штатная остановка.
            }
            catch (ObjectDisposedException)
            {
                // Процесс уже освобождён.
            }
            catch (IOException)
            {
                // Поток ошибок закрылся вместе с процессом.
            }
            catch (Exception)
            {
                // Диагностический поток не должен ронять приложение.
            }
        }

        private async Task ReportExitAsync(Process process, string tool, Task stderrTask, CancellationToken token)
        {
            // При штатной остановке ошибки процесса не интересуют.
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Даём диагностике шанс дочитать последние строки.
            try
            {
                await Task.WhenAny(stderrTask, Task.Delay(StderrDrainMilliseconds))
                    .ConfigureAwait(false);
            }
            catch
            {
                // Ошибки диагностического потока не важны.
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            int exitCode;
            try
            {
                if (!process.HasExited)
                {
                    return;
                }

                exitCode = process.ExitCode;
            }
            catch (Exception)
            {
                return;
            }

            if (exitCode != 0)
            {
                PublishError(BuildProcessError($"{tool} завершился с кодом {exitCode}."));
            }
        }

        private static bool HasFailedExit(Process process)
        {
            try
            {
                return process.HasExited && process.ExitCode != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void RaiseInput(SignalBlock block, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var handler = InputAvailable;
            if (handler is null)
            {
                return;
            }

            try
            {
                handler(this, block);
            }
            catch (Exception ex)
            {
                PublishError($"Обработчик входного сигнала: {ex.Message}");
            }
        }

        private void PublishError(string message)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Error?.Invoke(this, message);
            }
            catch
            {
                // Ошибка обработчика события не должна останавливать поток.
            }
        }

        private string BuildProcessError(string message)
        {
            lock (_stderrSync)
            {
                if (_errorLines.Count == 0)
                {
                    return message;
                }

                return message + " Последние сообщения: " + string.Join(" | ", _errorLines);
            }
        }

        private void ClearErrorLines()
        {
            lock (_stderrSync)
            {
                _errorLines.Clear();
            }
        }

        private static void Shutdown(
            CancellationTokenSource? cts,
            Process? inputProcess,
            Process? outputProcess,
            Task? inputTask,
            Task? outputTask,
            Task? inputStderrTask,
            Task? outputStderrTask)
        {
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Источник уже освобождён.
            }

            // Закрываем stdin, чтобы pacat успел дописать буфер.
            if (outputProcess is not null)
            {
                try
                {
                    outputProcess.StandardInput.Close();
                }
                catch
                {
                    // Процесс мог уже завершиться.
                }
            }

            var allTasks = new List<Task>(4);
            AddTask(allTasks, inputTask);
            AddTask(allTasks, outputTask);
            AddTask(allTasks, inputStderrTask);
            AddTask(allTasks, outputStderrTask);

            // Основные задачи ждём ограниченное время; диагностические завершатся после Kill.
            var mainTasks = new List<Task>(2);
            AddTask(mainTasks, inputTask);
            AddTask(mainTasks, outputTask);

            if (mainTasks.Count > 0)
            {
                try
                {
                    Task.WaitAll(mainTasks.ToArray(), ShutdownWaitMilliseconds);
                }
                catch (AggregateException)
                {
                    // Ошибки уже переданы через событие Error.
                }
            }

            KillProcess(inputProcess);
            KillProcess(outputProcess);

            inputProcess?.Dispose();
            outputProcess?.Dispose();

            if (cts is not null)
            {
                if (allTasks.Count == 0 || allTasks.TrueForAll(static task => task.IsCompleted))
                {
                    cts.Dispose();
                }
                else
                {
                    _ = Task.WhenAll(allTasks).ContinueWith(
                        _ => cts.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }

        private static void AddTask(List<Task> tasks, Task? task)
        {
            if (task is null)
            {
                return;
            }

            // Не ждём задачу, из которой вызвали Stop/Dispose (например, обработчик InputAvailable).
            if (task.Id == Task.CurrentId)
            {
                return;
            }

            tasks.Add(task);
        }

        private static void KillProcess(Process? process)
        {
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.WaitForExit(1000);
            }
            catch (InvalidOperationException)
            {
                // Процесс не был запущен или уже освобождён.
            }
            catch (Exception)
            {
                // Остановка не должна выбрасывать исключения в UI.
            }
        }
    }
}
