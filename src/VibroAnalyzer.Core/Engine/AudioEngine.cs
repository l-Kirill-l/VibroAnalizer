using System.Reflection;
using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Calibration;
using VibroAnalyzer.Core.Devices;
using VibroAnalyzer.Core.Recording;
using VibroAnalyzer.Core.Routing;
using VibroAnalyzer.Core.Session;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Engine;

/// <summary>
/// Маршрутизируемый движок: несколько логических входов и выходов,
/// виртуальные (смешанные) входы, анализаторы и отчёты.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int FusionIntervalMs = 20;

    private readonly object _sync = new();
    private readonly SessionContext _session;
    private readonly SessionWriter _writer;
    private readonly List<InputBus> _inputs = new();
    private readonly List<OutputBus> _outputs = new();
    private readonly List<VirtualInputBus> _virtualInputs = new();
    private readonly List<DeviceStreamGroup> _groups = new();

    private IAudioBackend _backend;
    private IReadOnlyList<AudioDeviceInfo> _devices = Array.Empty<AudioDeviceInfo>();
    private System.Threading.Timer? _fusionTimer;
    private SessionRecorder? _recorder;
    private int _fusionInFlight;
    private volatile bool _running;
    private bool _disposed;

    public AudioEngine(IAudioBackend backend, SessionContext session)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _writer = new SessionWriter(_session);

        RefreshDevices();
    }

    public IAudioBackend Backend => _backend;

    public IReadOnlyList<AudioDeviceInfo> Devices
    {
        get
        {
            lock (_sync)
            {
                return _devices.ToArray();
            }
        }
    }

    public IReadOnlyList<InputBus> Inputs
    {
        get
        {
            lock (_sync)
            {
                return _inputs.ToArray();
            }
        }
    }

    public IReadOnlyList<OutputBus> Outputs
    {
        get
        {
            lock (_sync)
            {
                return _outputs.ToArray();
            }
        }
    }

    public IReadOnlyList<VirtualInputBus> VirtualInputs
    {
        get
        {
            lock (_sync)
            {
                return _virtualInputs.ToArray();
            }
        }
    }

    public int SampleRate { get; set; } = 48000;

    public int BufferFrames { get; set; } = 1024;

    public bool IsRunning => _running;

    /// <summary>Идёт ли сейчас запись.</summary>
    public bool IsRecording => _recorder is { IsRecording: true };

    /// <summary>Каталог текущей записи, если она идёт.</summary>
    public string? RecordingDirectory => _recorder?.Directory;

    /// <summary>Шаблон калибровки для новых входов.</summary>
    public CalibrationProfile DefaultCalibration { get; } = new();

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Список входов, выходов или виртуальных шин изменился.</summary>
    public event EventHandler? RoutesChanged;

    /// <summary>Перечитывает устройства у текущего бэкенда.</summary>
    public void RefreshDevices()
    {
        try
        {
            var devices = _backend.EnumerateDevices();
            lock (_sync)
            {
                _devices = devices;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _devices = Array.Empty<AudioDeviceInfo>();
            }

            ErrorOccurred?.Invoke(this, $"Не удалось получить список устройств: {ex.Message}");
        }
    }

    /// <summary>Добавляет логический вход на физический канал устройства.</summary>
    public InputBus AddInput(AudioDeviceInfo device, int physicalChannel, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        InputBus bus;

        lock (_sync)
        {
            EnsureStopped();
            ValidateInputDevice(device, physicalChannel);
            ValidatePortConflict(_inputs.Select(input => input.DeviceId), device);

            bus = new InputBus(
                name ?? $"Вход {_inputs.Count + 1}",
                device.Id,
                physicalChannel,
                SampleRate,
                CreateCalibration());

            _inputs.Add(bus);
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
        return bus;
    }

    /// <summary>Удаляет логический вход.</summary>
    public void RemoveInput(InputBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        lock (_sync)
        {
            EnsureStopped();

            if (_virtualInputs.Any(virtualInput => virtualInput.DependsOn(bus.Id)))
            {
                throw new InvalidOperationException(
                    "На этот вход ссылается виртуальный вход. Сначала удалите микшер.");
            }

            if (!_inputs.Remove(bus))
            {
                return;
            }
        }

        bus.Dispose();
        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Добавляет логический выход на физический канал устройства.</summary>
    public OutputBus AddOutput(AudioDeviceInfo device, int physicalChannel, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        OutputBus bus;

        lock (_sync)
        {
            EnsureStopped();
            ValidateOutputDevice(device, physicalChannel);
            ValidatePortConflict(_outputs.Select(output => output.DeviceId), device);

            bus = new OutputBus(
                name ?? $"Выход {_outputs.Count + 1}",
                device.Id,
                physicalChannel);

            _outputs.Add(bus);
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
        return bus;
    }

    /// <summary>Удаляет логический выход.</summary>
    public void RemoveOutput(OutputBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        lock (_sync)
        {
            EnsureStopped();

            if (!_outputs.Remove(bus))
            {
                return;
            }
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Создаёт виртуальный вход из нескольких источников.
    /// </summary>
    public VirtualInputBus AddVirtualInput(
        string? name,
        IEnumerable<FusionSource> sources,
        FusionMode mode)
    {
        ArgumentNullException.ThrowIfNull(sources);

        VirtualInputBus bus;

        lock (_sync)
        {
            EnsureStopped();

            var list = sources.ToList();
            foreach (var source in list)
            {
                if (source.Source is null)
                {
                    throw new ArgumentException("Источник виртуального входа не задан.", nameof(sources));
                }
            }

            bus = new VirtualInputBus(
                name ?? $"Микшер {_virtualInputs.Count + 1}",
                list,
                mode,
                SampleRate,
                CreateCalibration());

            _virtualInputs.Add(bus);
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
        return bus;
    }

    /// <summary>Удаляет виртуальный вход.</summary>
    public void RemoveVirtualInput(VirtualInputBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        lock (_sync)
        {
            EnsureStopped();

            if (_virtualInputs.Any(other => !ReferenceEquals(other, bus) && other.DependsOn(bus.Id)))
            {
                throw new InvalidOperationException(
                    "На этот микшер ссылается другой виртуальный вход.");
            }

            if (!_virtualInputs.Remove(bus))
            {
                return;
            }
        }

        bus.Dispose();
        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                return;
            }

            if (_inputs.Count == 0 && _outputs.Count == 0)
            {
                throw new InvalidOperationException(
                    "Добавьте хотя бы один вход или выход перед запуском.");
            }

            foreach (var input in _inputs)
            {
                input.Configure(SampleRate);
            }

            foreach (var virtualInput in _virtualInputs)
            {
                virtualInput.Configure(SampleRate);
            }

            foreach (var output in _outputs)
            {
                output.Configure(SampleRate);
            }

            var groups = BuildGroups();
            var started = new List<DeviceStreamGroup>();

            try
            {
                foreach (var group in groups)
                {
                    StartGroup(group);
                    started.Add(group);
                }

                foreach (var input in _inputs)
                {
                    input.Start();
                }

                foreach (var virtualInput in _virtualInputs)
                {
                    virtualInput.Start();
                }

                foreach (var output in _outputs)
                {
                    output.Start();
                }

                if (_virtualInputs.Count > 0)
                {
                    _fusionTimer = new System.Threading.Timer(
                        OnFusionTick,
                        null,
                        FusionIntervalMs,
                        FusionIntervalMs);
                }

                _groups.AddRange(groups);
                _running = true;
            }
            catch
            {
                foreach (var group in started)
                {
                    StopGroup(group);
                }

                foreach (var input in _inputs)
                {
                    input.Stop();
                }

                foreach (var virtualInput in _virtualInputs)
                {
                    virtualInput.Stop();
                }

                _fusionTimer?.Dispose();
                _fusionTimer = null;
                throw;
            }

            StatusChanged?.Invoke(this, BuildStartStatus());
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _running = false;

            _fusionTimer?.Dispose();
            _fusionTimer = null;

            foreach (var group in _groups)
            {
                StopGroup(group);
            }

            _groups.Clear();

            foreach (var input in _inputs)
            {
                input.Stop();
            }

            foreach (var virtualInput in _virtualInputs)
            {
                virtualInput.Stop();
            }

            foreach (var output in _outputs)
            {
                output.Stop();
            }

            StopRecording();
            StatusChanged?.Invoke(this, "Измерение остановлено.");
        }
    }

    /// <summary>
    /// Начинает запись выбранных шин. По умолчанию пишутся все входы и
    /// виртуальные входы; при includeOutputs добавляются мониторы выходов.
    /// </summary>
    public void StartRecording(
        string? name = null,
        IEnumerable<ISignalBus>? buses = null,
        bool includeOutputs = false)
    {
        string directory;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_running)
            {
                throw new InvalidOperationException(
                    "Запись доступна только во время измерения.");
            }

            if (_recorder is not null)
            {
                throw new InvalidOperationException("Запись уже идёт.");
            }

            var selected = buses?.ToList()
                ?? _inputs.Cast<ISignalBus>().Concat(_virtualInputs).ToList();

            if (selected.Count == 0)
            {
                throw new InvalidOperationException("Не выбрано ни одной шины для записи.");
            }

            var targets = selected.Select(RecordingTarget.ForBus).ToList();

            if (includeOutputs)
            {
                targets.AddRange(_outputs.Select(RecordingTarget.ForOutput));
            }

            if (targets.Count == 0)
            {
                throw new InvalidOperationException("Нет данных для записи.");
            }

            directory = Path.Combine(
                _session.ModuleDirectory,
                "recordings",
                $"{DateTime.Now:yyyyMMdd-HHmmss}-{SanitizeName(name ?? "session")}");

            var recorder = new SessionRecorder(directory, SampleRate, targets);
            recorder.Start();
            _recorder = recorder;
        }

        StatusChanged?.Invoke(this, $"Запись начата: {directory}");
    }

    /// <summary>
    /// Останавливает запись, сохраняет manifest.json и возвращает каталог записи.
    /// </summary>
    public string? StopRecording()
    {
        SessionRecorder? recorder;

        lock (_sync)
        {
            recorder = _recorder;
            _recorder = null;
        }

        if (recorder is null)
        {
            return null;
        }

        string path;

        try
        {
            path = recorder.Stop();
        }
        finally
        {
            recorder.Dispose();
        }

        StatusChanged?.Invoke(this, $"Запись сохранена: {path}");
        return path;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = name
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray();

        var result = new string(characters).Trim('-', '.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "session" : result;
    }

    /// <summary>
    /// Сохраняет текущие показания всех шин в отчёт сессии и общий журнал.
    /// </summary>
    public string SaveMeasurement()
    {
        var inputs = Inputs;
        var virtualInputs = VirtualInputs;
        var outputs = Outputs;
        var inputBuses = inputs.Cast<ISignalBus>().Concat(virtualInputs).ToList();
        var outputBuses = outputs.Cast<ISignalBus>().ToList();
        var allBuses = inputBuses.Concat(outputBuses).ToList();

        var report = new ReportData
        {
            ModuleId = _session.ModuleId,
            CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "2.0",
            Backend = _backend.Name,
            InputDevice = string.Join("; ", inputBuses.Select(bus => bus.Name)),
            OutputDevice = string.Join("; ", outputBuses.Select(bus => bus.Name)),
            SampleRate = SampleRate,
            InputChannels = inputBuses.Count,
            OutputChannels = outputBuses.Count,
        };

        foreach (var bus in allBuses)
        {
            var analyzers = bus.Analyzers.Analyzers;
            var scope = analyzers.OfType<OscilloscopeAnalyzer>().FirstOrDefault();
            var spectrum = analyzers.OfType<SpectrumAnalyzer>().FirstOrDefault();
            var unit = bus.Calibration.Unit;

            if (scope is not null)
            {
                report.Measurements.Add(new Measurement($"{bus.Name}: частота", scope.FrequencyHz, "Гц"));
                report.Measurements.Add(new Measurement($"{bus.Name}: размах", scope.Vpp, unit));
                report.Measurements.Add(new Measurement($"{bus.Name}: СКЗ", scope.Rms, unit));
                report.Measurements.Add(new Measurement($"{bus.Name}: постоянная", scope.DcOffset, unit));
            }

            if (spectrum is not null)
            {
                report.Measurements.Add(new Measurement($"{bus.Name}: пик", spectrum.PeakFrequencyHz, "Гц"));
                report.Measurements.Add(new Measurement($"{bus.Name}: уровень пика", spectrum.PeakLevelDb, "дБFS"));
                report.Measurements.Add(new Measurement($"{bus.Name}: уровень шума", spectrum.NoiseFloorDb, "дБFS"));

                var peaks = ExtractTopPeaks(spectrum, 5);
                for (var index = 0; index < peaks.Count; index++)
                {
                    report.Measurements.Add(new Measurement(
                        $"{bus.Name}: пик {index + 1}",
                        peaks[index].LevelDb,
                        $"дБFS @ {peaks[index].FrequencyHz:0.##} Гц"));
                    report.SpectrumPeaks.Add(peaks[index]);
                }
            }
        }

        var path = _writer.WriteReport(report);

        try
        {
            _writer.AppendMeasurements(report.Measurements);
            _writer.WriteStatus("measurement_saved", $"Отчёт: {path}");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Отчёт сохранён, но общий журнал недоступен: {ex.Message}");
        }

        StatusChanged?.Invoke(this, $"Измерение сохранено: {path}");
        return path;
    }

    public void SetBackend(IAudioBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        if (IsRunning)
        {
            Stop();
        }

        lock (_sync)
        {
            _backend = backend;
            ClearRoutes();
        }

        RefreshDevices();
        RoutesChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, $"Бэкенд: {backend.Name}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();

        lock (_sync)
        {
            StopRecording();
            ClearRoutes();
            _disposed = true;
        }
    }

    private List<DeviceStreamGroup> BuildGroups()
    {
        var groups = new Dictionary<string, DeviceStreamGroup>(StringComparer.Ordinal);

        DeviceStreamGroup GetGroup(AudioDeviceInfo device)
        {
            var key = device.EffectiveStreamId;
            if (!groups.TryGetValue(key, out var group))
            {
                group = new DeviceStreamGroup(device);
                groups.Add(key, group);
            }

            return group;
        }

        foreach (var input in _inputs)
        {
            var device = FindDevice(input.DeviceId)
                ?? throw new InvalidOperationException(
                    $"Устройство входа «{input.DeviceId}» больше недоступно.");

            ValidateInputDevice(device, input.PhysicalChannel);
            var group = GetGroup(device);
            if (!string.IsNullOrWhiteSpace(device.PortId))
            {
                group.InputPortId ??= device.PortId;
            }

            group.Inputs.Add(input);
        }

        foreach (var output in _outputs)
        {
            var device = FindDevice(output.DeviceId)
                ?? throw new InvalidOperationException(
                    $"Устройство выхода «{output.DeviceId}» больше недоступно.");

            ValidateOutputDevice(device, output.PhysicalChannel);
            var group = GetGroup(device);
            if (!string.IsNullOrWhiteSpace(device.PortId))
            {
                group.OutputPortId ??= device.PortId;
            }

            group.Outputs.Add(output);
        }

        return groups.Values.ToList();
    }

    private void StartGroup(DeviceStreamGroup group)
    {
        var device = group.Device;
        var hasInput = group.Inputs.Count > 0;
        var hasOutput = group.Outputs.Count > 0;

        if (_backend is IAudioPortControl portControl)
        {
            var streamId = device.EffectiveStreamId;

            if (hasInput
                && !string.IsNullOrWhiteSpace(group.InputPortId)
                && !portControl.TrySelectPort(streamId, group.InputPortId, true, out var inputError))
            {
                ErrorOccurred?.Invoke(
                    this,
                    $"Устройство «{device.Name}»: не удалось активировать входной разъём: {inputError}");
            }

            if (hasOutput
                && !string.IsNullOrWhiteSpace(group.OutputPortId)
                && !portControl.TrySelectPort(streamId, group.OutputPortId, false, out var outputError))
            {
                ErrorOccurred?.Invoke(
                    this,
                    $"Устройство «{device.Name}»: не удалось активировать выходной разъём: {outputError}");
            }
        }

        var streamIdForOptions = device.EffectiveStreamId;
        var options = new AudioStreamOptions(
            hasInput ? streamIdForOptions : null,
            hasOutput ? streamIdForOptions : null,
            SampleRate,
            hasInput ? Math.Clamp(device.InputChannels, 1, 32) : 0,
            hasOutput ? Math.Clamp(device.OutputChannels, 1, 32) : 0,
            Math.Max(64, BufferFrames));

        group.OutputChannels = hasOutput ? Math.Clamp(device.OutputChannels, 1, 32) : 0;

        var stream = _backend.OpenStream(options);
        group.Stream = stream;
        stream.InputAvailable += (_, block) => OnGroupInput(group, block);
        stream.Error += (_, message) => OnGroupError(group, message);

        try
        {
            if (hasOutput)
            {
                var period = Math.Max(5, (int)Math.Round(options.BufferFrames * 1000.0 / SampleRate));
                group.OutputTimer = new System.Threading.Timer(
                    _ => OnGroupOutput(group),
                    null,
                    0,
                    period);
            }

            stream.Start();

            if (!stream.IsRunning)
            {
                throw new AudioBackendException(
                    $"Поток устройства «{device.Name}» не перешёл в рабочее состояние.");
            }
        }
        catch
        {
            StopGroup(group);
            throw;
        }
    }

    private static void StopGroup(DeviceStreamGroup group)
    {
        group.OutputTimer?.Dispose();
        group.OutputTimer = null;

        var stream = group.Stream;
        group.Stream = null;

        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Stop();
        }
        catch
        {
            // Остановка не должна мешать освобождению остальных устройств.
        }

        stream.Dispose();
    }

    private void OnGroupInput(DeviceStreamGroup group, SignalBlock block)
    {
        if (group.Stream is null)
        {
            return;
        }

        if (block.SampleRate != SampleRate && !group.RateWarningShown)
        {
            group.RateWarningShown = true;
            ErrorOccurred?.Invoke(
                this,
                $"Устройство «{group.Device.Name}» выдаёт {block.SampleRate} Гц вместо {SampleRate} Гц. "
                + "Частоты в анализаторах будут смещены — выберите частоту устройства.");
        }

        foreach (var input in group.Inputs)
        {
            try
            {
                input.Process(block);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Вход «{input.Name}»: {ex.Message}");
            }
        }
    }

    private void OnGroupOutput(DeviceStreamGroup group)
    {
        if (group.Stream is null || group.OutputChannels <= 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref group.OutputInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            var stream = group.Stream;
            if (stream is null || !stream.IsRunning)
            {
                return;
            }

            var frames = Math.Max(64, BufferFrames);
            var samples = OutputMixer.Mix(
                group.Outputs,
                frames,
                SampleRate,
                group.OutputChannels);

            stream.Write(new SignalBlock(
                samples,
                group.OutputChannels,
                SampleRate,
                Interlocked.Increment(ref group.Sequence),
                DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Выход устройства «{group.Device.Name}»: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref group.OutputInFlight, 0);
        }
    }

    private void OnFusionTick(object? state)
    {
        if (!_running)
        {
            return;
        }

        if (Interlocked.Exchange(ref _fusionInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var virtualInput in _virtualInputs)
            {
                try
                {
                    virtualInput.Update();
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"Микшер «{virtualInput.Name}»: {ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _fusionInFlight, 0);
        }
    }

    private void OnGroupError(DeviceStreamGroup group, string message)
    {
        ErrorOccurred?.Invoke(this, $"Устройство «{group.Device.Name}»: {message}");
    }

    private string BuildStartStatus()
    {
        return $"Запущено · {_backend.Name} · {SampleRate} Гц · "
            + $"входов: {_inputs.Count} · выходов: {_outputs.Count}"
            + (_virtualInputs.Count > 0 ? $" · микшеров: {_virtualInputs.Count}" : string.Empty);
    }

    private AudioDeviceInfo? FindDevice(string deviceId)
    {
        foreach (var device in _devices)
        {
            if (device.Id == deviceId)
            {
                return device;
            }
        }

        return null;
    }

    private void ValidatePortConflict(IEnumerable<string> existingDeviceIds, AudioDeviceInfo device)
    {
        if (string.IsNullOrWhiteSpace(device.PortId))
        {
            return;
        }

        foreach (var existingId in existingDeviceIds)
        {
            var existing = FindDevice(existingId);
            if (existing is null
                || !string.Equals(
                    existing.EffectiveStreamId,
                    device.EffectiveStreamId,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(existing.PortId)
                || string.Equals(existing.PortId, device.PortId, StringComparison.Ordinal))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Нельзя одновременно использовать разные разъёмы одного устройства "
                + $"«{device.Name}». Выберите один разъём.");
        }
    }

    private static void ValidateInputDevice(AudioDeviceInfo device, int physicalChannel)
    {        if (!device.IsInput || device.InputChannels <= 0)
        {
            throw new ArgumentException($"Устройство «{device.Name}» не имеет входа.");
        }

        if ((uint)physicalChannel >= (uint)device.InputChannels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalChannel),
                $"У устройства «{device.Name}» нет канала {physicalChannel + 1}.");
        }
    }

    private static void ValidateOutputDevice(AudioDeviceInfo device, int physicalChannel)
    {
        if (!device.IsOutput || device.OutputChannels <= 0)
        {
            throw new ArgumentException($"Устройство «{device.Name}» не имеет выхода.");
        }

        if ((uint)physicalChannel >= (uint)device.OutputChannels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalChannel),
                $"У устройства «{device.Name}» нет канала {physicalChannel + 1}.");
        }
    }

    private void EnsureStopped()
    {
        if (_running)
        {
            throw new InvalidOperationException(
                "Остановите измерение перед изменением маршрутизации.");
        }
    }

    private void ClearRoutes()
    {
        foreach (var input in _inputs)
        {
            input.Dispose();
        }

        foreach (var virtualInput in _virtualInputs)
        {
            virtualInput.Dispose();
        }

        foreach (var output in _outputs)
        {
            output.Dispose();
        }

        _inputs.Clear();
        _virtualInputs.Clear();
        _outputs.Clear();
    }

    private CalibrationProfile CreateCalibration()
    {
        return new CalibrationProfile
        {
            Name = DefaultCalibration.Name,
            FullScaleVolts = DefaultCalibration.FullScaleVolts,
            InputGain = DefaultCalibration.InputGain,
            SensorSensitivity = DefaultCalibration.SensorSensitivity,
            ShuntOhms = DefaultCalibration.ShuntOhms,
            Mode = DefaultCalibration.Mode,
            PhysicalUnit = DefaultCalibration.PhysicalUnit,
        };
    }

    private static List<SpectrumPeak> ExtractTopPeaks(SpectrumAnalyzer analyzer, int count)
    {
        var result = new List<SpectrumPeak>();
        var frequencies = analyzer.Frequencies;
        var levels = analyzer.MagnitudesDb;
        var length = Math.Min(frequencies.Length, levels.Length);

        if (length == 0)
        {
            return result;
        }

        var indices = Enumerable.Range(1, Math.Max(0, length - 1))
            .OrderByDescending(index => levels[index])
            .Take(count);

        foreach (var index in indices)
        {
            if (!double.IsFinite(levels[index]))
            {
                continue;
            }

            result.Add(new SpectrumPeak(frequencies[index], levels[index]));
        }

        return result;
    }

    private sealed class DeviceStreamGroup
    {
        public DeviceStreamGroup(AudioDeviceInfo device)
        {
            Device = device;
        }

        public AudioDeviceInfo Device { get; }

        public List<InputBus> Inputs { get; } = new();

        public List<OutputBus> Outputs { get; } = new();

        /// <summary>Выбранный входной разъём общего потока.</summary>
        public string? InputPortId { get; set; }

        /// <summary>Выбранный выходной разъём общего потока.</summary>
        public string? OutputPortId { get; set; }

        public IAudioStream? Stream { get; set; }

        public System.Threading.Timer? OutputTimer { get; set; }

        public int OutputChannels { get; set; }

        public int OutputInFlight;

        public bool RateWarningShown;

        public long Sequence;
    }
}
