using System.Text.Encodings.Web;
using System.Text.Json;
using VibroAnalyzer.Core.Routing;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Recording;

/// <summary>
/// Шина, которую нужно записать: сырой и демодулированный сигнал
/// логической шины либо монитор логического выхода.
/// </summary>
public sealed class RecordingTarget
{
    private RecordingTarget(RecordingBusInfo info, ISignalBus? bus, OutputBus? output)
    {
        Info = info;
        Bus = bus;
        Output = output;
    }

    /// <summary>Описание шины для manifest.json.</summary>
    public RecordingBusInfo Info { get; }

    internal ISignalBus? Bus { get; }

    internal OutputBus? Output { get; }

    /// <summary>Создаёт цель записи для логической шины (raw + demod).</summary>
    public static RecordingTarget ForBus(ISignalBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        var input = bus as InputBus;

        var info = new RecordingBusInfo
        {
            Id = bus.Id,
            Name = bus.Name,
            Kind = bus.IsVirtual ? "virtual" : "input",
            DeviceId = input?.DeviceId ?? "",
            PhysicalChannel = input?.PhysicalChannel ?? 0,
        };

        return new RecordingTarget(info, bus, null);
    }

    /// <summary>Создаёт цель записи для монитора логического выхода.</summary>
    public static RecordingTarget ForOutput(OutputBus output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var info = new RecordingBusInfo
        {
            Id = output.Id,
            Name = output.Name,
            Kind = "output",
            DeviceId = output.DeviceId,
            PhysicalChannel = output.PhysicalChannel,
        };

        return new RecordingTarget(info, null, output);
    }
}

/// <summary>
/// Записывает сигналы выбранных шин в каталог: WAV-файлы и manifest.json.
/// Таймер каждые 20 мс забирает только новые кадры из кольцевых буферов.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private const int TimerIntervalMs = 20;
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _name;
    private readonly int _sampleRate;
    private readonly TargetState[] _states;

    private DateTimeOffset _createdAt;
    private Timer? _timer;
    private int _recording;
    private bool _stopped;
    private bool _disposed;

    public SessionRecorder(string directory, int sampleRate, IEnumerable<RecordingTarget> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(targets);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        _directory = directory;
        _sampleRate = sampleRate;
        _name = ResolveName(directory);
        _createdAt = DateTimeOffset.UtcNow;

        var states = new List<TargetState>();
        var busIndex = 0;
        var outputIndex = 0;

        foreach (var target in targets)
        {
            if (target is null)
            {
                continue;
            }

            if (target.Bus is not null)
            {
                busIndex++;
                target.Info.RawFile = $"bus-{busIndex:D2}-raw.wav";
                target.Info.DemodFile = $"bus-{busIndex:D2}-demod.wav";
            }
            else if (target.Output is not null)
            {
                outputIndex++;
                target.Info.MonitorFile = $"out-{outputIndex:D2}-monitor.wav";
            }
            else
            {
                continue;
            }

            states.Add(new TargetState(target));
        }

        _states = states.ToArray();
        System.IO.Directory.CreateDirectory(_directory);
    }

    /// <summary>Каталог записи.</summary>
    public string Directory => _directory;

    /// <summary>Идёт ли запись в данный момент.</summary>
    public bool IsRecording => Volatile.Read(ref _recording) == 1;

    /// <summary>
    /// Открывает WAV-файлы и запускает запись. Повторный вызов во время
    /// записи ничего не меняет.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Volatile.Read(ref _recording) == 1)
            {
                return;
            }

            _stopped = false;
            _createdAt = DateTimeOffset.UtcNow;

            try
            {
                foreach (var state in _states)
                {
                    OpenState(state);
                }
            }
            catch
            {
                foreach (var state in _states)
                {
                    CloseState(state);
                }

                throw;
            }

            Volatile.Write(ref _recording, 1);
            _timer = new Timer(_ => Pump(), null, TimerIntervalMs, TimerIntervalMs);
        }
    }

    /// <summary>
    /// Останавливает запись, финализирует WAV-файлы и manifest.json.
    /// Повторный вызов безопасен и возвращает тот же каталог.
    /// </summary>
    public string Stop()
    {
        lock (_sync)
        {
            StopCore();
            return _directory;
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

            try
            {
                StopCore();
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    private void StopCore()
    {
        if (_stopped)
        {
            return;
        }

        _timer?.Dispose();
        _timer = null;

        // Забираем остаток данных, накопленных между последним тиком и остановкой.
        try
        {
            Pump();
        }
        catch
        {
            // Ошибка финального чтения не должна мешать закрытию файлов.
        }

        Volatile.Write(ref _recording, 0);
        _stopped = true;

        var maxFrames = 0L;

        foreach (var state in _states)
        {
            maxFrames = Math.Max(maxFrames, FinalizeWriter(ref state.RawWriter));
            maxFrames = Math.Max(maxFrames, FinalizeWriter(ref state.DemodWriter));
            maxFrames = Math.Max(maxFrames, FinalizeWriter(ref state.MonitorWriter));
        }

        var durationSeconds = _sampleRate > 0 ? (double)maxFrames / _sampleRate : 0.0;
        WriteManifest(durationSeconds);
    }

    private void OpenState(TargetState state)
    {
        if (state.Target.Bus is { } bus)
        {
            var raw = bus.RawBuffer;
            state.RawWriter = TryCreateWriter(state.Target.Info.RawFile);
            state.LastRaw = raw.TotalFramesWritten;
            state.RawInitialized = true;

            var demod = bus.DemodBuffer;
            state.DemodWriter = TryCreateWriter(state.Target.Info.DemodFile);
            state.LastDemod = demod.TotalFramesWritten;
            state.DemodInitialized = true;
            return;
        }

        if (state.Target.Output is { } output)
        {
            var monitor = output.MonitorBuffer;
            state.MonitorWriter = TryCreateWriter(state.Target.Info.MonitorFile);

            // Если буфер уже существует, его старые кадры не записываем.
            // Если буфера ещё нет, все появившиеся в нём кадры созданы после старта.
            state.LastMonitor = monitor?.TotalFramesWritten ?? 0;
            state.MonitorInitialized = true;
        }
    }

    private void CloseState(TargetState state)
    {
        FinalizeWriter(ref state.RawWriter);
        FinalizeWriter(ref state.DemodWriter);
        FinalizeWriter(ref state.MonitorWriter);
    }

    private WavWriter? TryCreateWriter(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        try
        {
            return new WavWriter(Path.Combine(_directory, fileName), _sampleRate);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void Pump()
    {
        if (Volatile.Read(ref _recording) == 0)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                if (_disposed || Volatile.Read(ref _recording) == 0)
                {
                    return;
                }

                foreach (var state in _states)
                {
                    try
                    {
                        PumpState(state);
                    }
                    catch (IOException)
                    {
                        // Сбой записи одной шины не должен останавливать остальные.
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
        catch (Exception)
        {
            // Исключение из таймера не должно завершать процесс.
        }
    }

    private static void PumpState(TargetState state)
    {
        if (state.Target.Bus is { } bus)
        {
            PumpBuffer(state.RawWriter, bus.RawBuffer, ref state.LastRaw, ref state.RawInitialized);
            PumpBuffer(state.DemodWriter, bus.DemodBuffer, ref state.LastDemod, ref state.DemodInitialized);
            return;
        }

        if (state.Target.Output is { } output)
        {
            var monitor = output.MonitorBuffer;
            if (monitor is null)
            {
                return;
            }

            PumpBuffer(state.MonitorWriter, monitor, ref state.LastMonitor, ref state.MonitorInitialized);
        }
    }

    private static void PumpBuffer(
        WavWriter? writer,
        SignalRingBuffer buffer,
        ref long lastTotal,
        ref bool initialized)
    {
        var total = buffer.TotalFramesWritten;

        if (!initialized)
        {
            initialized = true;
            lastTotal = total;
            return;
        }

        if (total <= lastTotal)
        {
            if (total < lastTotal)
            {
                // Буфер пересоздали или очистили: начинаем отсчёт заново.
                lastTotal = total;
            }

            return;
        }

        if (writer is null)
        {
            lastTotal = total;
            return;
        }

        var available = total - lastTotal;
        var count = (int)Math.Min(available, buffer.CapacityFrames);

        if (count <= 0)
        {
            lastTotal = total;
            return;
        }

        var snapshot = buffer.Snapshot(count, 0);
        var samples = new float[snapshot.Length];

        for (var i = 0; i < snapshot.Length; i++)
        {
            samples[i] = (float)snapshot[i];
        }

        writer.Write(samples);
        lastTotal = total;
    }

    private static long FinalizeWriter(ref WavWriter? writer)
    {
        var current = writer;
        if (current is null)
        {
            return 0;
        }

        var frames = current.FramesWritten;
        writer = null;

        try
        {
            current.Dispose();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return frames;
    }

    private void WriteManifest(double durationSeconds)
    {
        var manifest = new RecordingManifest
        {
            Schema = "vibroanalyzer.recording.v1",
            Name = _name,
            CreatedAt = _createdAt,
            SampleRate = _sampleRate,
            DurationSeconds = durationSeconds,
            Buses = _states.Select(state => state.Target.Info).ToList(),
        };

        var contents = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        WriteBytesAtomic(Path.Combine(_directory, ManifestFileName), contents);
    }

    private static void WriteBytesAtomic(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ResolveName(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var name = Path.GetFileName(full);
        return string.IsNullOrWhiteSpace(name) ? full : name;
    }

    private sealed class TargetState
    {
        public TargetState(RecordingTarget target)
        {
            Target = target;
        }

        public readonly RecordingTarget Target;

        public WavWriter? RawWriter;

        public WavWriter? DemodWriter;

        public WavWriter? MonitorWriter;

        public long LastRaw;

        public long LastDemod;

        public long LastMonitor;

        public bool RawInitialized;

        public bool DemodInitialized;

        public bool MonitorInitialized;
    }
}
