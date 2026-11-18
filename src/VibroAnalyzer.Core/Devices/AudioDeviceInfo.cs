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
    string Notes = "");

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

    /// <summary>Для отображения в списках достаточно имени устройства.</summary>
    public override string ToString() => Name;
}
