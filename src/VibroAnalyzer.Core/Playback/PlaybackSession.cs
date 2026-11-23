using System.Text;
using System.Text.Json;
using VibroAnalyzer.Core.Recording;

namespace VibroAnalyzer.Core.Playback;

/// <summary>
/// Сеанс воспроизведения записи: читает manifest.json и WAV-файлы,
/// создаёт шины воспроизведения и управляет позицией/скоростью.
/// </summary>
public sealed class PlaybackSession : IDisposable
{
    private const string ManifestFileName = "manifest.json";
    private const int TimerIntervalMs = 20;
    private const double MinSpeed = 0.1;
    private const double MaxSpeed = 8.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _sync = new();
    private readonly PlaybackBus[] _buses;
    private readonly string _name;
    private readonly int _sampleRate;
    private readonly long _durationFrames;

    private Timer? _timer;
    private double _positionFrames;
    private double _speed = 1.0;
    private int _playing;
    private int _ticking;
    private bool _disposed;

    private PlaybackSession(string directory, string name, int sampleRate, long durationFrames, PlaybackBus[] buses)
    {
        Directory = directory;
        _name = name;
        _sampleRate = sampleRate;
        _durationFrames = durationFrames;
        _buses = buses;
    }

    public string Directory { get; }

    public string Name => _name;

    public int SampleRate => _sampleRate;

    public TimeSpan Duration => TimeSpan.FromSeconds(_durationFrames / (double)_sampleRate);

    public TimeSpan Position
    {
        get
        {
            lock (_sync)
            {
                return TimeSpan.FromSeconds(_positionFrames / _sampleRate);
            }
        }
    }

    /// <summary>Скорость воспроизведения в диапазоне 0.1…8. Применяется сразу.</summary>
    public double Speed
    {
        get
        {
            lock (_sync)
            {
                return _speed;
            }
        }
        set
        {
            lock (_sync)
            {
                _speed = double.IsFinite(value)
                    ? Math.Clamp(value, MinSpeed, MaxSpeed)
                    : 1.0;
            }
        }
    }

    public bool IsPlaying => Volatile.Read(ref _playing) == 1;

    public IReadOnlyList<PlaybackBus> Buses => _buses;

    /// <summary>
    /// Загружает запись из каталога. Битые и отсутствующие WAV-файлы
    /// пропускаются, остальные шины загружаются как обычно.
    /// </summary>
    public static PlaybackSession Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var fullDirectory = Path.GetFullPath(directory);
        if (!System.IO.Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException($"Каталог записи не найден: {fullDirectory}");
        }

        var manifestPath = Path.Combine(fullDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("В каталоге записи нет manifest.json.", manifestPath);
        }

        RecordingManifest? manifest;

        try
        {
            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            manifest = JsonSerializer.Deserialize<RecordingManifest>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Не удалось разобрать manifest.json.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("Не удалось прочитать manifest.json.", exception);
        }

        if (manifest is null)
        {
            throw new InvalidDataException("manifest.json пуст или имеет неверный формат.");
        }

        var rate = manifest.SampleRate;
        var infos = manifest.Buses ?? new List<RecordingBusInfo>();
        var loaded = new List<(RecordingBusInfo Info, float[] Raw, float[]? Demod)>();

        foreach (var info in infos)
        {
            if (info is null)
            {
                continue;
            }

            var isOutput = string.Equals(info.Kind, "output", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrWhiteSpace(info.RawFile) && !string.IsNullOrWhiteSpace(info.MonitorFile));

            float[]? raw;
            float[]? demod = null;
            var wavRate = 0;

            if (isOutput)
            {
                raw = ReadFirst(fullDirectory, out wavRate, info.MonitorFile, info.RawFile, info.DemodFile);
            }
            else
            {
                raw = ReadFirst(fullDirectory, out wavRate, info.RawFile);

                if (raw is not null)
                {
                    if (!string.IsNullOrWhiteSpace(info.DemodFile)
                        && !string.Equals(info.DemodFile, info.RawFile, StringComparison.Ordinal))
                    {
                        demod = TryReadWav(fullDirectory, info.DemodFile, out _);
                    }
                }
                else
                {
                    // Старая запись без raw: используем демодулированный файл как источник.
                    raw = TryReadWav(fullDirectory, info.DemodFile, out wavRate);
                }
            }

            if (raw is null)
            {
                // Битая или отсутствующая шина — пропускаем.
                continue;
            }

            if (rate <= 0 && wavRate > 0)
            {
                rate = wavRate;
            }

            loaded.Add((info, raw, demod));
        }

        if (rate <= 0)
        {
            rate = 48000;
        }

        var buses = new List<PlaybackBus>(loaded.Count);
        var durationFrames = 0L;

        foreach (var item in loaded)
        {
            PlaybackBus? bus = null;

            try
            {
                bus = new PlaybackBus(item.Info, item.Raw, item.Demod, rate);
                bus.Start();
                buses.Add(bus);

                if (item.Raw.Length > durationFrames)
                {
                    durationFrames = item.Raw.Length;
                }
            }
            catch (Exception)
            {
                // Одна проблемная шина не должна мешать загрузке остальных.
                bus?.Dispose();
            }
        }

        var name = !string.IsNullOrWhiteSpace(manifest.Name)
            ? manifest.Name
            : ResolveName(directory);

        return new PlaybackSession(directory, name, rate, durationFrames, buses.ToArray());
    }

    /// <summary>Запускает воспроизведение с текущей позиции.</summary>
    public void Play()
    {
        lock (_sync)
        {
            if (_disposed || _durationFrames <= 0)
            {
                return;
            }

            if (_positionFrames >= _durationFrames)
            {
                _positionFrames = 0;
                ClearBuffers();
            }

            Volatile.Write(ref _playing, 1);

            if (_timer is null)
            {
                _timer = new Timer(OnTick, null, TimerIntervalMs, TimerIntervalMs);
            }
            else
            {
                _timer.Change(TimerIntervalMs, TimerIntervalMs);
            }
        }
    }

    /// <summary>Приостанавливает воспроизведение, сохраняя позицию.</summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            PauseLocked();
        }
    }

    /// <summary>Останавливает воспроизведение, сбрасывает позицию и очищает буферы.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            PauseLocked();
            _positionFrames = 0;
            ClearBuffers();
        }
    }

    /// <summary>
    /// Переходит к указанной позиции. Буферы очищаются, затем в них
    /// записывается до 20 секунд истории непосредственно перед позицией.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var seconds = position.TotalSeconds;
            if (!double.IsFinite(seconds) || seconds <= 0)
            {
                _positionFrames = 0;
                ClearBuffers();
                return;
            }

            var target = Math.Min(seconds * _sampleRate, (double)_durationFrames);
            _positionFrames = target;
            ClearBuffers();

            if (target <= 0)
            {
                return;
            }

            var history = (int)Math.Min(target, (double)_sampleRate * 20.0);
            if (history <= 0)
            {
                return;
            }

            var start = target - history;
            foreach (var bus in _buses)
            {
                bus.WriteFrames(start, history);
            }
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
            Volatile.Write(ref _playing, 0);
            _timer?.Dispose();
            _timer = null;

            foreach (var bus in _buses)
            {
                bus.Dispose();
            }
        }
    }

    private void OnTick(object? state)
    {
        if (Volatile.Read(ref _playing) == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _ticking, 1) != 0)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                if (_disposed || Volatile.Read(ref _playing) == 0)
                {
                    return;
                }

                if (_durationFrames <= 0 || _positionFrames >= _durationFrames)
                {
                    FinishLocked();
                    return;
                }

                var frames = (int)Math.Round(_sampleRate * (TimerIntervalMs / 1000.0) * _speed);
                if (frames < 1)
                {
                    frames = 1;
                }

                var remaining = _durationFrames - _positionFrames;
                if (frames > remaining)
                {
                    frames = (int)Math.Ceiling(remaining);
                }

                if (frames < 1)
                {
                    frames = 1;
                }

                foreach (var bus in _buses)
                {
                    bus.WriteFrames(_positionFrames, frames);
                }

                _positionFrames += frames;

                if (_positionFrames >= _durationFrames)
                {
                    _positionFrames = _durationFrames;
                    FinishLocked();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void PauseLocked()
    {
        Volatile.Write(ref _playing, 0);
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void FinishLocked()
    {
        if (_positionFrames > _durationFrames)
        {
            _positionFrames = _durationFrames;
        }

        PauseLocked();
    }

    private void ClearBuffers()
    {
        foreach (var bus in _buses)
        {
            bus.RawBuffer.Clear();
            bus.DemodBuffer.Clear();
        }
    }

    private static float[]? ReadFirst(string directory, out int rate, params string?[] fileNames)
    {
        rate = 0;

        foreach (var fileName in fileNames)
        {
            var data = TryReadWav(directory, fileName, out var fileRate);
            if (data is not null)
            {
                rate = fileRate;
                return data;
            }
        }

        return null;
    }

    private static float[]? TryReadWav(string directory, string? fileName, out int rate)
    {
        rate = 0;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return WavFile.Read(path, out rate);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException
            or ArgumentException
            or FormatException)
        {
            rate = 0;
            return null;
        }
    }

    private static string ResolveName(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var name = Path.GetFileName(full);
        return string.IsNullOrWhiteSpace(name) ? full : name;
    }
}
