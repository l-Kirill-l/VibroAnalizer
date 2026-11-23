using VibroAnalyzer.Core.Devices;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Audio;

/// <summary>
/// Параметры открываемого аудиопотока.
/// </summary>
public sealed record AudioStreamOptions(
    string? InputDeviceId,
    string? OutputDeviceId,
    int SampleRate,
    int InputChannels,
    int OutputChannels,
    int BufferFrames = 1024)
{
    /// <summary>Поток содержит вход (задано хотя бы одно входное направление).</summary>
    public bool HasInput => InputChannels > 0;

    /// <summary>Поток содержит выход (задано хотя бы одно выходное направление).</summary>
    public bool HasOutput => OutputChannels > 0;

    /// <summary>Поток одновременно содержит вход и выход.</summary>
    public bool IsDuplex => HasInput && HasOutput;
}

/// <summary>
/// Открытый аудиопоток: приём входных блоков, выдача выходных и управление работой.
/// </summary>
public interface IAudioStream : IDisposable
{
    /// <summary>Параметры, с которыми был открыт поток.</summary>
    AudioStreamOptions Options { get; }

    /// <summary>Признак того, что поток запущен.</summary>
    bool IsRunning { get; }

    /// <summary>Событие появления нового входного блока.</summary>
    event EventHandler<SignalBlock>? InputAvailable;

    /// <summary>Сообщение об ошибке потока.</summary>
    event EventHandler<string>? Error;

    /// <summary>Запускает поток.</summary>
    void Start();

    /// <summary>Останавливает поток.</summary>
    void Stop();

    /// <summary>Записывает блок в выходной поток; возвращает признак успеха.</summary>
    bool Write(SignalBlock block);
}

/// <summary>
/// Аудиобэкенд: перечисление устройств и открытие потоков.
/// </summary>
public interface IAudioBackend
{
    /// <summary>Имя бэкенда для пользовательского интерфейса.</summary>
    string Name { get; }

    /// <summary>Признак доступности бэкенда в текущей системе.</summary>
    bool IsAvailable { get; }

    /// <summary>Возвращает список доступных аудиоустройств.</summary>
    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();

    /// <summary>Открывает поток с заданными параметрами.</summary>
    IAudioStream OpenStream(AudioStreamOptions options);
}

/// <summary>
/// Бэкенд, который умеет переключать физический разъём устройства
/// (например, встроенный микрофон ↔ микрофон гарнитуры в PipeWire).
/// </summary>
public interface IAudioPortControl
{
    /// <summary>
    /// Активирует разъём <paramref name="portId"/> у указанного источника или стока.
    /// </summary>
    bool TrySelectPort(string deviceId, string portId, bool isInput, out string error);
}

/// <summary>
/// Исключение аудиобэкенда с понятным пользователю сообщением.
/// </summary>
public sealed class AudioBackendException : Exception
{
    public AudioBackendException(string message)
        : base(message)
    {
    }

    public AudioBackendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
