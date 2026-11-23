namespace VibroAnalyzer.Core.Recording;

/// <summary>
/// Манифест записи сигналов: общие параметры сеанса и список шин.
/// Сохраняется в manifest.json рядом с WAV-файлами.
/// </summary>
public sealed class RecordingManifest
{
    /// <summary>Версия формата манифеста.</summary>
    public string Schema { get; set; } = "vibroanalyzer.recording.v1";

    /// <summary>Отображаемое имя записи.</summary>
    public string Name { get; set; } = "";

    /// <summary>Момент создания записи.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Частота дискретизации всех файлов записи, Гц.</summary>
    public int SampleRate { get; set; }

    /// <summary>Длительность записи в секундах.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Описание записанных шин.</summary>
    public List<RecordingBusInfo> Buses { get; set; } = new();
}

/// <summary>
/// Описание одной записанной шины и её WAV-файлов.
/// Имена файлов задаются относительно каталога записи.
/// </summary>
public sealed class RecordingBusInfo
{
    /// <summary>Идентификатор исходной шины.</summary>
    public Guid Id { get; set; }

    /// <summary>Отображаемое имя шины.</summary>
    public string Name { get; set; } = "";

    /// <summary>Тип шины: input, virtual или output.</summary>
    public string Kind { get; set; } = "input";

    /// <summary>Идентификатор аудиоустройства.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Номер физического канала.</summary>
    public int PhysicalChannel { get; set; }

    /// <summary>Файл сырого сигнала.</summary>
    public string? RawFile { get; set; }

    /// <summary>Файл демодулированного сигнала.</summary>
    public string? DemodFile { get; set; }

    /// <summary>Файл мониторинга выхода.</summary>
    public string? MonitorFile { get; set; }
}
