namespace VibroAnalyzer.Core.Devices;

/// <summary>
/// Направление физического контакта разъёма.
/// </summary>
public enum PinMode
{
    /// <summary>Контакт отсутствует или не используется.</summary>
    Absent,

    /// <summary>Контакт работает на вход.</summary>
    Read,

    /// <summary>Контакт работает на выход.</summary>
    Write,

    /// <summary>Контакт двунаправленный.</summary>
    ReadWrite
}

/// <summary>
/// Доступность физического разъёма по данным драйвера.
/// </summary>
public enum PortAvailability
{
    /// <summary>Драйвер не сообщает состояние.</summary>
    Unknown,

    /// <summary>Разъём доступен (устройство подключено).</summary>
    Available,

    /// <summary>Разъём сейчас недоступен (например, гарнитура не подключена).</summary>
    Unavailable
}

/// <summary>
/// Один контакт физического разъёма.
/// </summary>
public sealed record AudioPinInfo(
    int Number,
    PinMode Mode,
    string Purpose = "");

/// <summary>
/// Физический разъём звуковой карты: вход, выход или совмещённое гнездо.
/// </summary>
public sealed record AudioPortInfo(
    string Id,
    string Name,
    AudioJackType JackType,
    bool IsInput,
    bool IsOutput,
    PortAvailability Availability,
    IReadOnlyList<AudioPinInfo> Pins,
    string Notes = "")
{
    /// <summary>Цифровой интерфейс, для которого распиновка 3,5-мм разъёма неприменима.</summary>
    public bool IsDigital => JackType is AudioJackType.Digital or AudioJackType.Monitor;

    /// <summary>Краткая сводка направлений для списка каналов.</summary>
    public string DirectionSummary
        => IsInput && IsOutput ? "W + R" : IsInput ? "R" : IsOutput ? "W" : "—";
}

/// <summary>
/// Описание физического разъёма, полученное от аудиобэкенда.
/// </summary>
public sealed record AudioPortRoute(
    string Id,
    string Name,
    bool IsInput,
    AudioJackType JackType,
    PortAvailability Availability,
    string GroupId = "",
    int Channels = 2,
    string Notes = "");

/// <summary>
/// Собирает описание разъёмов из маршрутов драйвера.
/// Разъёмы, у которых совпадает группа доступности и есть оба направления,
/// объединяются в одно совмещённое гнездо (например, наушники + микрофон).
/// </summary>
public static class AudioPortTopology
{
    private const int MaxPins = 4;

    public static IReadOnlyList<AudioPortInfo> Build(IEnumerable<AudioPortRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var routeList = routes
            .Where(route => !string.IsNullOrWhiteSpace(route.Id))
            .ToList();
        var result = new List<AudioPortInfo>();

        foreach (var group in routeList.GroupBy(route =>
                     string.IsNullOrWhiteSpace(route.GroupId) ? route.Id : route.GroupId))
        {
            var groupRoutes = group.ToList();
            var inputs = groupRoutes.Where(route => route.IsInput).ToList();
            var outputs = groupRoutes.Where(route => !route.IsInput).ToList();

            if (inputs.Count > 0
                && outputs.Count > 0
                && !inputs.Any(route => IsDigital(route.JackType))
                && !outputs.Any(route => IsDigital(route.JackType)))
            {
                result.Add(BuildComposite(outputs, inputs));
                continue;
            }

            foreach (var route in groupRoutes)
            {
                result.Add(BuildSingle(route));
            }
        }

        return result
            .OrderByDescending(port => port.IsInput && port.IsOutput)
            .ThenByDescending(port => port.IsInput)
            .ThenBy(port => port.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static AudioPortInfo BuildComposite(
        IReadOnlyList<AudioPortRoute> outputs,
        IReadOnlyList<AudioPortRoute> inputs)
    {
        var output = outputs.OrderByDescending(route => route.Channels).First();
        var input = inputs.OrderByDescending(route => route.Channels).First();

        return new AudioPortInfo(
            $"{output.Id}|{input.Id}",
            BuildCompositeName(output, input),
            CombineJack(output.JackType, input.JackType),
            IsInput: true,
            IsOutput: true,
            CombineAvailability(output.Availability, input.Availability),
            BuildCompositePins(output.Channels, input.Channels),
            "наушники + микрофон");
    }

    private static AudioPortInfo BuildSingle(AudioPortRoute route)
    {
        var digital = IsDigital(route.JackType);
        var pins = digital
            ? Array.Empty<AudioPinInfo>()
            : BuildSinglePins(route.IsInput ? PinMode.Read : PinMode.Write, route.Channels);

        var notes = route.Notes;
        if (!digital && route.Channels > 2)
        {
            var suffix = $"Показаны первые 2 из {route.Channels} каналов.";
            notes = string.IsNullOrWhiteSpace(notes) ? suffix : $"{notes} {suffix}";
        }

        return new AudioPortInfo(
            route.Id,
            route.Name,
            route.JackType,
            route.IsInput,
            !route.IsInput,
            route.Availability,
            pins,
            notes);
    }

    private static IReadOnlyList<AudioPinInfo> BuildSinglePins(PinMode mode, int channels)
    {
        var signalPins = Math.Clamp(channels, 1, 2);
        return new[]
        {
            new AudioPinInfo(1, mode, signalPins >= 1 ? "канал 1" : "нет контакта"),
            new AudioPinInfo(2, signalPins >= 2 ? mode : PinMode.Absent,
                signalPins >= 2 ? "канал 2" : "земля"),
            new AudioPinInfo(3, PinMode.Absent, "земля"),
            new AudioPinInfo(4, PinMode.Absent, "нет контакта"),
        };
    }

    private static IReadOnlyList<AudioPinInfo> BuildCompositePins(
        int outputChannels,
        int inputChannels)
    {
        return new[]
        {
            new AudioPinInfo(1, outputChannels >= 1 ? PinMode.Write : PinMode.Absent,
                outputChannels >= 1 ? "левый выход" : "нет контакта"),
            new AudioPinInfo(2, outputChannels >= 2 ? PinMode.Write : PinMode.Absent,
                outputChannels >= 2 ? "правый выход" : "нет контакта"),
            new AudioPinInfo(3, PinMode.Absent, "земля"),
            new AudioPinInfo(4, inputChannels >= 1 ? PinMode.Read : PinMode.Absent,
                inputChannels >= 1 ? "микрофон/вход" : "нет контакта"),
        }.Take(MaxPins).ToArray();
    }

    private static string BuildCompositeName(AudioPortRoute output, AudioPortRoute input)
    {
        if ((output.JackType is AudioJackType.Headphone or AudioJackType.Headset)
            && (input.JackType is AudioJackType.Microphone or AudioJackType.Headset))
        {
            return "Комбо-разъём гарнитуры";
        }

        return $"{output.Name} / {input.Name}";
    }

    private static AudioJackType CombineJack(AudioJackType output, AudioJackType input)
    {
        if (input == AudioJackType.Headset || output == AudioJackType.Headset)
        {
            return AudioJackType.Headset;
        }

        if (output is AudioJackType.Headphone or AudioJackType.Speaker
            && input is AudioJackType.Microphone)
        {
            return AudioJackType.Headset;
        }

        return output;
    }

    private static PortAvailability CombineAvailability(
        PortAvailability first,
        PortAvailability second)
    {
        if (first == PortAvailability.Available || second == PortAvailability.Available)
        {
            return PortAvailability.Available;
        }

        if (first == PortAvailability.Unavailable || second == PortAvailability.Unavailable)
        {
            return PortAvailability.Unavailable;
        }

        return PortAvailability.Unknown;
    }

    private static bool IsDigital(AudioJackType jackType)
        => jackType is AudioJackType.Digital or AudioJackType.Monitor;
}
