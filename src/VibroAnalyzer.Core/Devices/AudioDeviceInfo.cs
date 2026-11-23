namespace VibroAnalyzer.Core.Devices;

/// <summary>
/// Тип аудиоразъёма или назначения канала.
/// </summary>
public enum AudioJackType
{
    Unknown,
    Microphone,
    LineIn,
    LineOut,
    Headphone,
    Speaker,
    Headset,
    Digital,
    Monitor
}

/// <summary>
/// Описание одного аудиоканала устройства.
/// </summary>
public sealed record AudioChannelInfo(
    int Index,
    string Name,
    bool IsInput,
    AudioJackType JackType,
    string Notes = "");

/// <summary>
/// Возможности аудиоустройства, важные для измерительных сценариев.
/// </summary>
public sealed record DeviceCapabilities(
    bool SupportsDuplex,
    bool IsAcCoupled,
    double? MaxInputVoltageRms,
    bool SupportsExclusiveMode,
    bool SupportsLoopback,
    bool SupportsChannelMapping,
    string Notes = "",
    double? MaxInputCurrentMa = null,
    double? InputImpedanceOhms = null,
    double? MinFrequencyHz = 20.0,
    double? MaxFrequencyHz = 20000.0,
    int? BitDepth = null);

/// <summary>
/// Сведения об аудиоустройстве, полученные от выбранного бэкенда.
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    string Backend,
    bool IsInput,
    bool IsOutput,
    int InputChannels,
    int OutputChannels,
    int DefaultSampleRate,
    IReadOnlyList<int> SupportedSampleRates,
    IReadOnlyList<AudioChannelInfo> Channels,
    DeviceCapabilities Capabilities,
    bool IsMonitor = false)
{
    /// <summary>Устройство одновременно поддерживает вход и выход.</summary>
    public bool IsDuplex => IsInput && IsOutput;

    /// <summary>Идентификатор физической звуковой карты (например, alsa_card.*).</summary>
    public string CardId { get; init; } = "";

    /// <summary>
    /// Идентификатор потока/узла, который открывает бэкенд.
    /// Для логического устройства одного разъёма может совпадать с другими разъёмами.
    /// </summary>
    public string StreamId { get; init; } = "";

    /// <summary>Идентификатор выбранного физического разъёма (порта).</summary>
    public string PortId { get; init; } = "";

    /// <summary>Короткое имя карты/кодека для интерфейса.</summary>
    public string ControllerName { get; init; } = "";

    /// <summary>Идентификатор потока с запасным значением <see cref="Id"/>.</summary>
    public string EffectiveStreamId => string.IsNullOrWhiteSpace(StreamId) ? Id : StreamId;

    /// <summary>Идентификатор активного физического разъёма, если он известен.</summary>
    public string ActivePortId { get; init; } = "";

    /// <summary>
    /// Физические разъёмы карты: входы, выходы и совмещённые гнёзда.
    /// Пустой список означает, что драйвер не сообщает топологию разъёмов.
    /// </summary>
    public IReadOnlyList<AudioPortInfo> Ports { get; init; } = Array.Empty<AudioPortInfo>();

    /// <summary>Для отображения в списках достаточно имени устройства.</summary>
    public override string ToString() => Name;
}
