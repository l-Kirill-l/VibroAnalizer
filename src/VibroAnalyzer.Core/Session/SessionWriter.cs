using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VibroAnalyzer.Core.Session;

/// <summary>
/// Запись отчётов, статусов и измерений в общий каталог сессии.
/// </summary>
public sealed class SessionWriter
{
    private const string ReportFileName = "report.json";
    private const string StatusFileName = "status.json";
    private const string SessionStateFileName = "session_state.json";
    private const int RetryDelayMs = 25;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StateReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SessionContext _context;

    public SessionWriter(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// Записывает report.json атомарно и создаёт его копию с отметкой времени.
    /// Возвращает путь к report.json.
    /// </summary>
    public string WriteReport(ReportData report)
    {
        ArgumentNullException.ThrowIfNull(report);

        _context.EnsureDirectories();

        var reportPath = Path.Combine(_context.ModuleDirectory, ReportFileName);
        var json = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        WriteBytesAtomic(reportPath, json);

        var copyName = $"report-{FormatFileTimestamp(DateTime.Now)}.json";
        var copyPath = Path.Combine(_context.ModuleDirectory, copyName);
        WriteBytesAtomic(copyPath, json);

        return reportPath;
    }

    /// <summary>
    /// Добавляет измерения в конец SharedSessionData/session_state.json,
    /// сохраняя ранее записанные записи.
    /// </summary>
    public void AppendMeasurements(IEnumerable<Measurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var pending = measurements.Where(static measurement => measurement is not null).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(_context.SessionDirectory);

        var statePath = Path.Combine(_context.SessionDirectory, SessionStateFileName);
        var lockPath = statePath + ".lock";

        // Лок защищает от одновременной записи нескольких экземпляров приложения.
        // Если взять его не удалось, запись всё равно выполняется.
        using var lockStream = TryAcquireLock(lockPath, LockTimeout);

        var state = ReadState(statePath);
        state.Measurements ??= new List<SessionMeasurement>();

        var timestamp = FormatTimestamp(DateTime.Now);
        foreach (var measurement in pending)
        {
            state.Measurements.Add(new SessionMeasurement
            {
                Timestamp = timestamp,
                ModuleId = _context.ModuleId,
                Parameter = measurement.Parameter ?? "",
                Value = measurement.Value,
                Units = measurement.Units ?? "",
            });
        }

        WriteJsonAtomic(statePath, state);
    }

    /// <summary>
    /// Записывает status.json в каталог модуля.
    /// </summary>
    public void WriteStatus(string state, string details = "")
    {
        _context.EnsureDirectories();

        var status = new
        {
            state = state ?? "",
            details = details ?? "",
            updatedAt = FormatTimestamp(DateTime.Now),
            moduleId = _context.ModuleId,
        };

        var statusPath = Path.Combine(_context.ModuleDirectory, StatusFileName);
        WriteJsonAtomic(statusPath, status);
    }

    private static SessionStateFile ReadState(string path)
    {
        if (!File.Exists(path))
        {
            return new SessionStateFile();
        }

        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                var json = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new SessionStateFile();
                }

                var state = JsonSerializer.Deserialize<SessionStateFile>(json, JsonOptions);
                if (state is null)
                {
                    return new SessionStateFile();
                }

                state.Measurements ??= new List<SessionMeasurement>();
                return state;
            }
            catch (FileNotFoundException)
            {
                return new SessionStateFile();
            }
            catch (DirectoryNotFoundException)
            {
                return new SessionStateFile();
            }
            catch (JsonException)
            {
                // Повреждённый файл не должен мешать записи новых измерений.
                PreserveBrokenState(path);
                return new SessionStateFile();
            }
            catch (IOException) when (stopwatch.Elapsed < StateReadTimeout)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (IOException)
            {
                // Файл занят другим процессом дольше таймаута: сохраняем копию
                // и продолжаем запись, чтобы не потерять новые измерения.
                PreserveBrokenState(path);
                return new SessionStateFile();
            }
        }
    }

    private static FileStream? TryAcquireLock(string lockPath, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < timeout)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (UnauthorizedAccessException) when (stopwatch.Elapsed < timeout)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static void PreserveBrokenState(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            var backupName = $"session_state.broken-{FormatFileTimestamp(DateTime.Now)}.json";
            var backupPath = string.IsNullOrEmpty(directory)
                ? backupName
                : Path.Combine(directory, backupName);

            File.Copy(path, backupPath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        WriteBytesAtomic(path, json);
    }

    private static void WriteBytesAtomic(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
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
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FormatTimestamp(DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatFileTimestamp(DateTime value)
    {
        return value.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }
}
