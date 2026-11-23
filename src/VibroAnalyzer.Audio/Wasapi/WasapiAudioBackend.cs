using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Devices;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Audio.Wasapi;

/// <summary>
/// Аудиобэкенд WASAPI (Windows Core Audio) на основе NAudio.
/// </summary>
/// <remarks>
/// Бэкенд доступен только в Windows: перечисление устройств использует COM,
/// поэтому на других платформах возвращается пустой список, а открытие потока
/// завершается <see cref="AudioBackendException"/>.
/// <para>
/// Входной поток всегда открывается в фактическом формате устройства
/// (<see cref="WasapiCapture"/> берёт формат микшера), поэтому приём не навязывает
/// частоту дискретизации и число каналов. Выход открывается в запрошенном формате
/// float32; если устройство его не принимает, используется формат микшера,
/// а наружу отправляется предупреждение через событие <see cref="IAudioStream.Error"/>.
/// Передискретизация не выполняется: блоки пишутся с той частотой, с которой созданы.
/// </para>
/// </remarks>
public sealed class WasapiAudioBackend : IAudioBackend
{
    /// <summary>Имя бэкенда для интерфейса.</summary>
    public const string BackendName = "WASAPI";

    private const int OutputLatencyMilliseconds = 100;
    private const int OutputBufferMilliseconds = 200;
    private const int FallbackSampleRate = 48000;

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: подформат 32-битного float для WaveFormatExtensible.</summary>
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>KSDATAFORMAT_SUBTYPE_PCM: подформат целочисленного PCM для WaveFormatExtensible.</summary>
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");

    private static readonly IReadOnlyList<int> SupportedRates =
        Array.AsReadOnly(new[] { 44100, 48000, 96000, 192000 });

    public string Name => BackendName;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<AudioDeviceInfo>();
        }

        try
        {
            var result = new List<AudioDeviceInfo>();
            using var enumerator = new MMDeviceEnumerator();
            AddDevices(enumerator, DataFlow.Capture, result);
            AddDevices(enumerator, DataFlow.Render, result);
            return result;
        }
        catch
        {
            // На Linux COM недоступен, а на Windows перечисление может упасть
            // из-за службы аудио — в обоих случаях отдаём пустой список.
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    public IAudioStream OpenStream(AudioStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsWindows())
        {
            throw new AudioBackendException("WASAPI доступен только в Windows.");
        }

        return new WasapiAudioStream(options);
    }

    private static void AddDevices(MMDeviceEnumerator enumerator, DataFlow flow, List<AudioDeviceInfo> target)
    {
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active | DeviceState.Unplugged);
        foreach (var device in devices)
        {
            try
            {
                WaveFormat mixFormat;
                using (var client = device.AudioClient)
                {
                    mixFormat = client.MixFormat;
                }

                var channels = mixFormat.Channels > 0
                    ? mixFormat.Channels
                    : flow == DataFlow.Capture ? 1 : 2;
                var sampleRate = mixFormat.SampleRate > 0 ? mixFormat.SampleRate : FallbackSampleRate;
                var isInput = flow == DataFlow.Capture;

                var channelInfos = new List<AudioChannelInfo>(channels);
                for (var i = 0; i < channels; i++)
                {
                    channelInfos.Add(new AudioChannelInfo(
                        i,
                        $"Канал {i + 1}",
                        isInput,
                        isInput ? AudioJackType.Microphone : AudioJackType.LineOut,
                        isInput ? "Вход WASAPI (формат микшера устройства)." : "Выход WASAPI (формат микшера устройства)."));
                }

                target.Add(new AudioDeviceInfo(
                    device.ID,
                    string.IsNullOrWhiteSpace(device.FriendlyName) ? "Устройство WASAPI" : device.FriendlyName,
                    BackendName,
                    IsInput: isInput,
                    IsOutput: !isInput,
                    InputChannels: isInput ? channels : 0,
                    OutputChannels: isInput ? 0 : channels,
                    DefaultSampleRate: sampleRate,
                    SupportedSampleRates: SupportedRates,
                    Channels: channelInfos,
                    Capabilities: new DeviceCapabilities(
                        SupportsDuplex: true,
                        IsAcCoupled: true,
                        MaxInputVoltageRms: 1.0,
                        SupportsExclusiveMode: true,
                        SupportsLoopback: true,
                        SupportsChannelMapping: true,
                        Notes: CreateNotes(isInput, sampleRate, channels, mixFormat.BitsPerSample))));
            }
            catch
            {
                // Устройство могло исчезнуть между перечислением и чтением формата.
            }
            finally
            {
                try
                {
                    device.Dispose();
                }
                catch
                {
                    // COM-объект уже мог быть освобождён.
                }
            }
        }
    }

    private static string CreateNotes(bool isInput, int sampleRate, int channels, int bits)
    {
        var role = isInput ? "захвата" : "воспроизведения";
        return $"WASAPI, устройство {role}. По умолчанию используется общий (shared) режим, "
             + $"эксклюзивный режим доступен через AudioClient. Микшер: {sampleRate} Гц, {channels} кан., {bits} бит. "
             + "В WASAPI нет единого дуплексного endpoint: для одновременного приёма и выдачи нужны разные устройства. "
             + "Loopback-захват выполняется с устройства воспроизведения.";
    }

    private static MMDevice OpenDevice(string? deviceId, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device is not null)
                {
                    return device;
                }
            }
            catch
            {
                // Устройство не найдено или недоступно — берём устройство по умолчанию.
            }
        }

        return enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
    }

    /// <summary>
    /// Форматы отсчётов, которые умеет преобразовывать поток.
    /// </summary>
    private enum SampleKind
    {
        Unknown,
        Float32,
        Pcm16,
        Pcm24,
        Pcm32
    }

    /// <summary>
    /// Определяет формат отсчётов с учётом WaveFormatExtensible.
    /// В NAudio 2.3.0 у WaveFormatExtensible.Encoding всегда равно Extensible,
    /// поэтому подформат проверяется по GUID (AudioSubtypes с полем IeeeFloat в этой версии нет).
    /// </summary>
    private static SampleKind ResolveSampleKind(WaveFormat format, out int bytesPerSample)
    {
        bytesPerSample = 0;

        SampleKind kind;
        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == IeeeFloatSubFormat)
            {
                kind = format.BitsPerSample == 32 ? SampleKind.Float32 : SampleKind.Unknown;
            }
            else if (extensible.SubFormat == PcmSubFormat)
            {
                kind = format.BitsPerSample switch
                {
                    16 => SampleKind.Pcm16,
                    24 => SampleKind.Pcm24,
                    32 => SampleKind.Pcm32,
                    _ => SampleKind.Unknown
                };
            }
            else
            {
                kind = SampleKind.Unknown;
            }
        }
        else
        {
            kind = format.Encoding switch
            {
                WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 => SampleKind.Float32,
                WaveFormatEncoding.Pcm => format.BitsPerSample switch
                {
                    16 => SampleKind.Pcm16,
                    24 => SampleKind.Pcm24,
                    32 => SampleKind.Pcm32,
                    _ => SampleKind.Unknown
                },
                _ => SampleKind.Unknown
            };
        }

        if (kind == SampleKind.Unknown)
        {
            return SampleKind.Unknown;
        }

        bytesPerSample = format.BitsPerSample / 8;
        return bytesPerSample > 0 ? kind : SampleKind.Unknown;
    }

    private static string DescribeFormat(WaveFormat format)
    {
        var kind = ResolveSampleKind(format, out _);
        var encoding = kind switch
        {
            SampleKind.Float32 => "float32",
            SampleKind.Pcm16 => "PCM 16",
            SampleKind.Pcm24 => "PCM 24",
            SampleKind.Pcm32 => "PCM 32",
            _ => $"{format.Encoding}, {format.BitsPerSample} бит"
        };

        return $"{format.SampleRate} Гц, {format.Channels} кан., {encoding}";
    }

    private static bool SameLayout(WaveFormat left, WaveFormat right)
    {
        var leftKind = ResolveSampleKind(left, out var leftBytes);
        var rightKind = ResolveSampleKind(right, out var rightBytes);

        return left.SampleRate == right.SampleRate
            && left.Channels == right.Channels
            && leftKind == rightKind
            && leftBytes == rightBytes;
    }

    /// <summary>
    /// Переводит входной буфер устройства в чередующиеся float-отсчёты.
    /// Возвращает null, если формат не поддерживается.
    /// </summary>
    private static float[]? ConvertInputBuffer(byte[] buffer, int byteCount, WaveFormat format, out int frames)
    {
        frames = 0;

        var kind = ResolveSampleKind(format, out var bytesPerSample);
        if (kind == SampleKind.Unknown)
        {
            return null;
        }

        var channels = format.Channels;
        if (channels <= 0 || byteCount <= 0 || bytesPerSample <= 0)
        {
            return null;
        }

        var frameStride = channels * bytesPerSample;
        var blockAlign = format.BlockAlign > 0 ? format.BlockAlign : frameStride;
        frames = byteCount / blockAlign;
        if (frames <= 0)
        {
            return null;
        }

        if (frames * frameStride > byteCount)
        {
            frames = byteCount / frameStride;
        }

        if (frames <= 0)
        {
            return null;
        }

        var samples = new float[frames * channels];
        var offset = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = ReadSample(buffer.AsSpan(offset, bytesPerSample), kind);
            offset += bytesPerSample;
        }

        return samples;
    }

    private static float ReadSample(ReadOnlySpan<byte> source, SampleKind kind)
    {
        return kind switch
        {
            SampleKind.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source)),
            SampleKind.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(source) / 32768f,
            SampleKind.Pcm24 => ReadPcm24(source) / 8388608f,
            SampleKind.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(source) / 2147483648f,
            _ => 0f
        };
    }

    private static int ReadPcm24(ReadOnlySpan<byte> source)
    {
        var value = source[0] | (source[1] << 8) | (source[2] << 16);
        if ((value & 0x00800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value;
    }

    /// <summary>
    /// Переводит блок сигнала в байты формата провайдера вывода.
    /// Если каналов у провайдера больше, лишние заполняются первым каналом блока.
    /// </summary>
    private static byte[]? ConvertBlockToBytes(SignalBlock block, WaveFormat format)
    {
        var kind = ResolveSampleKind(format, out var bytesPerSample);
        if (kind == SampleKind.Unknown || bytesPerSample <= 0)
        {
            return null;
        }

        var frames = block.FrameCount;
        var sourceChannels = block.Channels;
        var targetChannels = format.Channels;
        if (frames <= 0 || sourceChannels <= 0 || targetChannels <= 0)
        {
            return null;
        }

        var samples = block.Samples;
        if (samples.Length < frames * sourceChannels)
        {
            return null;
        }

        var bytes = new byte[frames * targetChannels * bytesPerSample];
        var copiedChannels = Math.Min(sourceChannels, targetChannels);
        var offset = 0;

        for (var frame = 0; frame < frames; frame++)
        {
            var sourceIndex = frame * sourceChannels;
            var firstChannel = samples[sourceIndex];
            for (var channel = 0; channel < targetChannels; channel++)
            {
                var value = channel < copiedChannels ? samples[sourceIndex + channel] : firstChannel;
                WriteSample(bytes, offset, value, kind);
                offset += bytesPerSample;
            }
        }

        return bytes;
    }

    private static void WriteSample(byte[] destination, int offset, float value, SampleKind kind)
    {
        if (!float.IsFinite(value))
        {
            value = 0f;
        }

        value = Math.Clamp(value, -1f, 1f);

        switch (kind)
        {
            case SampleKind.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(destination.AsSpan(offset, 4), value);
                break;

            case SampleKind.Pcm16:
                var pcm16 = (short)Math.Clamp((int)MathF.Round(value * 32767f), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(offset, 2), pcm16);
                break;

            case SampleKind.Pcm24:
                var pcm24 = Math.Clamp((int)MathF.Round(value * 8388607f), -8388608, 8388607);
                destination[offset] = (byte)pcm24;
                destination[offset + 1] = (byte)(pcm24 >> 8);
                destination[offset + 2] = (byte)(pcm24 >> 16);
                break;

            case SampleKind.Pcm32:
                var pcm32 = (int)Math.Clamp((long)Math.Round(value * 2147483647.0), int.MinValue, int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset, 4), pcm32);
                break;
        }
    }

    /// <summary>
    /// Поток WASAPI: отдельные объекты захвата и воспроизведения,
    /// так как в WASAPI нет единого дуплексного endpoint.
    /// </summary>
    private sealed class WasapiAudioStream : IAudioStream
    {
        private readonly object _sync = new();
        private readonly AudioStreamOptions _options;

        private WasapiCapture? _capture;
        private MMDevice? _captureDevice;
        private volatile WaveFormat? _captureFormat;

        private WasapiOut? _output;
        private MMDevice? _outputDevice;
        private BufferedWaveProvider? _provider;

        private volatile bool _running;
        private volatile bool _disposed;
        private long _sequence;

        private volatile int _captureThreadId;
        private volatile int _playbackThreadId;
        private int _inputFormatWarningShown;
        private int _outputFormatWarningShown;

        public WasapiAudioStream(AudioStreamOptions options)
        {
            _options = options;
        }

        public AudioStreamOptions Options => _options;

        public bool IsRunning => _running;

        public event EventHandler<SignalBlock>? InputAvailable;

        public event EventHandler<string>? Error;

        public void Start()
        {
            var messages = new List<string>();

            lock (_sync)
            {
                if (_disposed || _running)
                {
                    return;
                }

                if (!OperatingSystem.IsWindows())
                {
                    messages.Add("WASAPI доступен только в Windows.");
                }
                else
                {
                    var inputFailed = false;

                    if (_options.HasInput)
                    {
                        try
                        {
                            StartCapture();
                        }
                        catch (Exception ex)
                        {
                            // Без входа измерение невозможно — выход не запускаем.
                            inputFailed = true;
                            messages.Add($"Не удалось запустить захват WASAPI: {ex.Message}");
                        }
                    }

                    if (!inputFailed && _options.HasOutput)
                    {
                        try
                        {
                            StartOutput(messages);
                        }
                        catch (Exception ex)
                        {
                            // Выход вторичен по отношению к измерению: вход продолжает работать,
                            // а пользователь получает сообщение об ошибке.
                            messages.Add(ex.Message);
                        }
                    }

                    _running = _capture is not null || _output is not null;
                }
            }

            foreach (var message in messages)
            {
                PublishError(message);
            }
        }

        public void Stop()
        {
            StreamResources resources;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                resources = TakeResources();
            }

            // Ресурсы освобождаются вне блокировки: Dispose/Join не должны ждать
            // обработчики, которые могут обращаться к Start/Stop/Write.
            ReleaseResources(resources);
        }

        public bool Write(SignalBlock block)
        {
            if (block is null)
            {
                return false;
            }

            string? error = null;
            var written = false;

            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                var provider = _provider;
                if (provider is null)
                {
                    return false;
                }

                var format = provider.WaveFormat;
                var bytes = ConvertBlockToBytes(block, format);
                if (bytes is null)
                {
                    if (Interlocked.Exchange(ref _outputFormatWarningShown, 1) == 0)
                    {
                        error = $"Формат вывода {DescribeFormat(format)} не поддерживается. "
                              + "Ожидается float32 или PCM 16/24/32 бита.";
                    }
                }
                else
                {
                    try
                    {
                        provider.AddSamples(bytes, 0, bytes.Length);
                        written = true;
                    }
                    catch (Exception ex)
                    {
                        error = $"Не удалось записать блок в WASAPI-вывод: {ex.Message}";
                    }
                }
            }

            if (error is not null)
            {
                PublishError(error);
            }

            return written;
        }

        public void Dispose()
        {
            StreamResources resources;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                resources = TakeResources();
            }

            ReleaseResources(resources);

            InputAvailable = null;
            Error = null;
        }

        private void StartCapture()
        {
            var device = OpenDevice(_options.InputDeviceId, DataFlow.Capture);
            WasapiCapture? capture = null;

            try
            {
                capture = new WasapiCapture(device);
                var format = capture.WaveFormat;
                if (format is null || format.SampleRate <= 0 || format.Channels <= 0)
                {
                    throw new AudioBackendException("Устройство захвата вернуло некорректный формат.");
                }

                _inputFormatWarningShown = 0;
                _captureThreadId = 0;
                _captureFormat = format;
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                _capture = capture;
                _captureDevice = device;

                capture.StartRecording();
            }
            catch
            {
                _capture = null;
                _captureDevice = null;
                _captureFormat = null;

                DetachCapture(capture);

                // Освобождаем в фоне: синхронный Join под блокировкой мог бы
                // столкнуться с обработчиком, который вызывает Start/Stop.
                if (capture is not null || device is not null)
                {
                    ThreadPool.QueueUserWorkItem(_ => ReleaseCapture(capture, device));
                }

                throw;
            }
        }

        private void StartOutput(List<string> messages)
        {
            var sampleRate = _options.SampleRate > 0 ? _options.SampleRate : FallbackSampleRate;
            var channels = _options.OutputChannels > 0 ? _options.OutputChannels : 1;
            var desired = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            Exception? firstError;
            try
            {
                CreateOutput(desired);
                return;
            }
            catch (Exception ex)
            {
                firstError = ex;
            }

            WaveFormat? mixFormat = null;
            try
            {
                mixFormat = ReadOutputMixFormat();
            }
            catch
            {
                // Формат микшера недоступен — сообщим исходную ошибку.
            }

            if (mixFormat is not null && !SameLayout(mixFormat, desired))
            {
                try
                {
                    CreateOutput(mixFormat);
                    messages.Add($"Устройство вывода не приняло формат {DescribeFormat(desired)}; "
                               + $"используется формат микшера {DescribeFormat(mixFormat)}.");
                    return;
                }
                catch (Exception ex)
                {
                    throw new AudioBackendException($"Не удалось открыть WASAPI-вывод: {ex.Message}", ex);
                }
            }

            throw new AudioBackendException($"Не удалось открыть WASAPI-вывод: {firstError.Message}", firstError);
        }

        private WaveFormat? ReadOutputMixFormat()
        {
            using var device = OpenDevice(_options.OutputDeviceId, DataFlow.Render);
            using var client = device.AudioClient;
            return client.MixFormat;
        }

        /// <summary>
        /// Создаёт BufferedWaveProvider и WasapiOut с общим режимом и латентностью 100 мс.
        /// Объекты публикуются в поля только после успешного Init/Play.
        /// </summary>
        private void CreateOutput(WaveFormat format)
        {
            var device = OpenDevice(_options.OutputDeviceId, DataFlow.Render);
            WasapiOut? output = null;

            try
            {
                var provider = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(OutputBufferMilliseconds),
                    ReadFully = true,
                    DiscardOnBufferOverflow = true
                };

                output = new WasapiOut(device, AudioClientShareMode.Shared, false, OutputLatencyMilliseconds);
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(provider);
                output.Play();

                _outputFormatWarningShown = 0;
                _playbackThreadId = 0;
                _provider = provider;
                _output = output;
                _outputDevice = device;
            }
            catch
            {
                // WasapiOut.Dispose останавливает поток и делает Join, поэтому
                // неудачный вывод освобождаем в фоне, не держа блокировку.
                if (output is not null)
                {
                    DetachOutput(output);
                    var failedOutput = output;
                    ThreadPool.QueueUserWorkItem(_ => ReleaseOutput(failedOutput, device));
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(_ => ReleaseOutput(null, device));
                }

                throw;
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (_disposed || _capture is null)
                {
                    return;
                }

                _captureThreadId = Environment.CurrentManagedThreadId;

                var format = _captureFormat;
                if (format is null || e.Buffer is null || e.BytesRecorded <= 0)
                {
                    return;
                }

                var samples = ConvertInputBuffer(e.Buffer, e.BytesRecorded, format, out var frames);
                if (samples is null || frames <= 0)
                {
                    if (Interlocked.Exchange(ref _inputFormatWarningShown, 1) == 0)
                    {
                        PublishError($"Формат входа {DescribeFormat(format)} не поддерживается. "
                                   + "Ожидается float32 или PCM 16/24/32 бита.");
                    }

                    return;
                }

                // Поток мог быть остановлен, пока блок конвертировался.
                if (_disposed || _capture is null)
                {
                    return;
                }

                var block = new SignalBlock(
                    samples,
                    format.Channels,
                    format.SampleRate,
                    Interlocked.Increment(ref _sequence));

                RaiseInput(block);
            }
            catch (Exception ex)
            {
                // Исключение из события NAudio не должно ронять поток захвата.
                PublishError($"Ошибка обработки входных данных WASAPI: {ex.Message}");
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            try
            {
                if (e.Exception is not null)
                {
                    PublishError($"Запись WASAPI остановлена с ошибкой: {e.Exception.Message}");
                }
            }
            catch
            {
                // Ошибка обработчика события не должна покидать поток NAudio.
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            try
            {
                _playbackThreadId = Environment.CurrentManagedThreadId;

                if (e.Exception is not null)
                {
                    PublishError($"Воспроизведение WASAPI остановлено с ошибкой: {e.Exception.Message}");
                }
            }
            catch
            {
                // Ошибка обработчика события не должна покидать поток NAudio.
            }
        }

        private void RaiseInput(SignalBlock block)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                InputAvailable?.Invoke(this, block);
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

        /// <summary>
        /// Снимает текущие ресурсы с полей (вызывать под <see cref="_sync"/>) и отписывает обработчики.
        /// </summary>
        private StreamResources TakeResources()
        {
            _running = false;

            var resources = new StreamResources(_capture, _captureDevice, _output, _outputDevice);

            _capture = null;
            _captureDevice = null;
            _captureFormat = null;
            _output = null;
            _outputDevice = null;
            _provider = null;

            DetachCapture(resources.Capture);
            DetachOutput(resources.Output);

            return resources;
        }

        private void ReleaseResources(StreamResources resources)
        {
            if (resources.Capture is not null || resources.CaptureDevice is not null)
            {
                if (_captureThreadId != 0 && Environment.CurrentManagedThreadId == _captureThreadId)
                {
                    // Stop вызван из обработчика захвата: синхронный Join привёл бы к взаимной блокировке.
                    ThreadPool.QueueUserWorkItem(_ => ReleaseCapture(resources.Capture, resources.CaptureDevice));
                }
                else
                {
                    ReleaseCapture(resources.Capture, resources.CaptureDevice);
                }
            }

            if (resources.Output is not null || resources.OutputDevice is not null)
            {
                if (_playbackThreadId != 0 && Environment.CurrentManagedThreadId == _playbackThreadId)
                {
                    // Stop вызван из обработчика воспроизведения: освобождаем в фоне.
                    ThreadPool.QueueUserWorkItem(_ => ReleaseOutput(resources.Output, resources.OutputDevice));
                }
                else
                {
                    ReleaseOutput(resources.Output, resources.OutputDevice);
                }
            }
        }

        private sealed record StreamResources(
            WasapiCapture? Capture,
            MMDevice? CaptureDevice,
            WasapiOut? Output,
            MMDevice? OutputDevice);

        private void DetachCapture(WasapiCapture? capture)
        {
            if (capture is null)
            {
                return;
            }

            try
            {
                capture.DataAvailable -= OnDataAvailable;
            }
            catch
            {
                // Обработчик уже мог быть снят.
            }

            try
            {
                capture.RecordingStopped -= OnRecordingStopped;
            }
            catch
            {
                // Обработчик уже мог быть снят.
            }
        }

        private void DetachOutput(WasapiOut? output)
        {
            if (output is null)
            {
                return;
            }

            try
            {
                output.PlaybackStopped -= OnPlaybackStopped;
            }
            catch
            {
                // Обработчик уже мог быть снят.
            }
        }

        private static void ReleaseCapture(WasapiCapture? capture, MMDevice? device)
        {
            if (capture is not null)
            {
                try
                {
                    capture.StopRecording();
                }
                catch
                {
                    // Захват уже мог быть остановлен.
                }

                try
                {
                    capture.Dispose();
                }
                catch
                {
                    // Освобождение COM-объекта не должно мешать выходу.
                }
            }

            if (device is not null)
            {
                try
                {
                    device.Dispose();
                }
                catch
                {
                    // COM-объект уже мог быть освобождён.
                }
            }
        }

        private static void ReleaseOutput(WasapiOut? output, MMDevice? device)
        {
            if (output is not null)
            {
                try
                {
                    output.Dispose();
                }
                catch
                {
                    // WasapiOut.Dispose сам останавливает воспроизведение; ошибку игнорируем.
                }
            }

            if (device is not null)
            {
                try
                {
                    device.Dispose();
                }
                catch
                {
                    // COM-объект уже мог быть освобождён.
                }
            }
        }
    }
}
