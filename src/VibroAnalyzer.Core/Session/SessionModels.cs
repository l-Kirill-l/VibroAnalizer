using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibroAnalyzer.Core.Session;

/// <summary>
/// Одно измерение: имя параметра, значение и единицы измерения.
/// </summary>
public sealed record Measurement(string Parameter, double Value, string Units);

/// <summary>
/// Пик спектра: частота и уровень в дБ.
/// </summary>
public sealed record SpectrumPeak(double FrequencyHz, double LevelDb);

/// <summary>
/// Данные отчёта модуля, сохраняемые в report.json.
/// </summary>
public sealed class ReportData
{
    public string Schema { get; set; } = "vibroanalyzer.report.v1";
    public string ModuleId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Backend { get; set; } = "";
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";
    public int SampleRate { get; set; }
    public int InputChannels { get; set; }
    public int OutputChannels { get; set; }
    public List<Measurement> Measurements { get; set; } = new();
    public List<SpectrumPeak> SpectrumPeaks { get; set; } = new();
}

/// <summary>
/// Запись об измерении в общем файле SharedSessionData/session_state.json.
/// Имена полей совпадают с форматом, который пишет UE5.
/// </summary>
internal sealed class SessionMeasurement
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("moduleId")]
    public string ModuleId { get; set; } = "";

    [JsonPropertyName("parameter")]
    public string Parameter { get; set; } = "";

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; } = "";

    /// <summary>
    /// Неизвестные поля, полученные от UE5, сохраняются без изменений.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
}

/// <summary>
/// Корневой объект session_state.json: массив измерений и, при наличии,
/// прочие поля, которые уже лежали в файле.
/// </summary>
internal sealed class SessionStateFile
{
    [JsonPropertyName("measurements")]
    public List<SessionMeasurement> Measurements { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
}
