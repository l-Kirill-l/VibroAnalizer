using System.Text.Json;
using VibroAnalyzer.Core.Devices;

namespace VibroAnalyzer.Audio.PipeWire;

/// <summary>Сведения о звуковой карте PipeWire и её физических разъёмах.</summary>
internal sealed record PipeWireCard(
    string Name,
    string Description,
    IReadOnlyList<AudioPortRoute> Routes);

/// <summary>
/// Читает топологию физических разъёмов из pw-dump, а при его отсутствии —
/// из JSON-вывода pactl. Топология нужна только для отображения паспортных
/// возможностей разъёмов и не влияет на открытие аудиопотоков.
/// </summary>
internal static class PipeWireCardReader
{
    private static readonly IReadOnlyDictionary<string, PipeWireCard> Empty =
        new Dictionary<string, PipeWireCard>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, PipeWireCard> Read()
    {
        var cards = TryReadFromPwDump();
        return cards.Count > 0 ? cards : TryReadFromPactl();
    }

    private static IReadOnlyDictionary<string, PipeWireCard> TryReadFromPwDump()
    {
        var json = PipeWireAudioBackend.RunTool("pw-dump", Array.Empty<string>(), out _);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Empty;
            }

            var cards = new Dictionary<string, PipeWireCard>(StringComparer.Ordinal);

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !string.Equals(GetString(item, "type"), "PipeWire:Interface:Device", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!item.TryGetProperty("info", out var info)
                    || info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("props", out var properties)
                    || properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = Clean(GetString(properties, "device.name"));
                if (name.Length == 0)
                {
                    continue;
                }

                var description = Clean(GetString(properties, "device.description"));
                var routes = ParsePwDumpRoutes(info);
                cards[name] = new PipeWireCard(name, description, routes);
            }

            return cards;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static IReadOnlyList<AudioPortRoute> ParsePwDumpRoutes(JsonElement info)
    {
        if (!info.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("EnumRoute", out var routesElement)
            || routesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AudioPortRoute>();
        }

        var routes = new List<AudioPortRoute>();

        foreach (var route in routesElement.EnumerateArray())
        {
            if (route.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Clean(GetString(route, "name"));
            if (id.Length == 0)
            {
                continue;
            }

            var isInput = string.Equals(
                GetString(route, "direction"),
                "Input",
                StringComparison.OrdinalIgnoreCase);

            var description = Clean(GetString(route, "description"));
            if (description.Length == 0)
            {
                description = PrettyName(id, isInput);
            }

            var availability = ParseAvailability(GetString(route, "available"));
            var (portType, group) = ParsePortInfo(route);

            routes.Add(new AudioPortRoute(
                id,
                description,
                isInput,
                MapJackType(portType, id, isInput),
                availability,
                group,
                Channels: 2));
        }

        return routes;
    }

    private static (string PortType, string Group) ParsePortInfo(JsonElement route)
    {
        if (!route.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Array)
        {
            return ("", "");
        }

        var portType = "";
        var group = "";
        var length = info.GetArrayLength();

        for (var index = 1; index + 1 < length; index += 2)
        {
            var key = info[index].ValueKind == JsonValueKind.String
                ? info[index].GetString()
                : null;
            var value = info[index + 1].ValueKind == JsonValueKind.String
                ? info[index + 1].GetString()
                : info[index + 1].ToString();

            if (string.Equals(key, "port.type", StringComparison.Ordinal))
            {
                portType = value ?? "";
            }
            else if (string.Equals(key, "port.availability-group", StringComparison.Ordinal))
            {
                group = value ?? "";
            }
        }

        return (portType, group);
    }

    private static IReadOnlyDictionary<string, PipeWireCard> TryReadFromPactl()
    {
        var json = PipeWireAudioBackend.RunTool(
            "pactl",
            new[] { "-f", "json", "list", "cards" },
            out _);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Empty;
            }

            var cards = new Dictionary<string, PipeWireCard>(StringComparer.Ordinal);

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = Clean(GetString(item, "name"));
                if (name.Length == 0)
                {
                    continue;
                }

                var properties = item.TryGetProperty("properties", out var props)
                                 && props.ValueKind == JsonValueKind.Object
                    ? props
                    : default;
                var description = Clean(GetString(properties, "device.description"));
                var routes = ParsePactlRoutes(item);
                cards[name] = new PipeWireCard(name, description, routes);
            }

            return cards;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static IReadOnlyList<AudioPortRoute> ParsePactlRoutes(JsonElement card)
    {
        if (!card.TryGetProperty("ports", out var ports)
            || ports.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<AudioPortRoute>();
        }

        var routes = new List<AudioPortRoute>();

        foreach (var property in ports.EnumerateObject())
        {
            var id = property.Name;
            var port = property.Value;
            if (port.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var isInput = InferIsInput(id, port);
            var description = Clean(GetString(port, "description"));
            if (description.Length == 0)
            {
                description = PrettyName(id, isInput);
            }

            var portProperties = port.TryGetProperty("properties", out var props)
                                 && props.ValueKind == JsonValueKind.Object
                ? props
                : default;

            routes.Add(new AudioPortRoute(
                id,
                description,
                isInput,
                MapJackType(GetString(portProperties, "port.type"), id, isInput),
                PortAvailability.Unknown,
                Clean(GetString(port, "availability_group")),
                Channels: 2));
        }

        return routes;
    }

    private static bool InferIsInput(string id, JsonElement port)
    {
        var portType = GetString(
            port.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
                ? properties
                : default,
            "port.type");

        var text = (portType + " " + id).ToLowerInvariant();
        if (text.Contains("input", StringComparison.Ordinal)
            || text.Contains("mic", StringComparison.Ordinal)
            || text.Contains("capture", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Contains("output", StringComparison.Ordinal)
            || text.Contains("speaker", StringComparison.Ordinal)
            || text.Contains("headphone", StringComparison.Ordinal)
            || text.Contains("playback", StringComparison.Ordinal))
        {
            return false;
        }

        return false;
    }

    private static PortAvailability ParseAvailability(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "yes" or "available" => PortAvailability.Available,
            "no" or "unavailable" => PortAvailability.Unavailable,
            _ => PortAvailability.Unknown,
        };
    }

    private static AudioJackType MapJackType(string portType, string id, bool isInput)
    {
        var text = (portType + " " + id).ToLowerInvariant();

        if (text.Contains("hdmi", StringComparison.Ordinal)
            || text.Contains("displayport", StringComparison.Ordinal)
            || text.Contains("digital", StringComparison.Ordinal))
        {
            return AudioJackType.Digital;
        }

        if (text.Contains("headset", StringComparison.Ordinal))
        {
            return AudioJackType.Headset;
        }

        if (text.Contains("headphone", StringComparison.Ordinal))
        {
            return AudioJackType.Headphone;
        }

        if (text.Contains("speaker", StringComparison.Ordinal))
        {
            return AudioJackType.Speaker;
        }

        if (text.Contains("mic", StringComparison.Ordinal))
        {
            return AudioJackType.Microphone;
        }

        if (text.Contains("line", StringComparison.Ordinal))
        {
            return isInput ? AudioJackType.LineIn : AudioJackType.LineOut;
        }

        return isInput ? AudioJackType.LineIn : AudioJackType.LineOut;
    }

    private static string PrettyName(string id, bool isInput)
    {
        var text = id.ToLowerInvariant();

        if (text.Contains("internal-mic", StringComparison.Ordinal))
        {
            return "Встроенный микрофон";
        }

        if (text.Contains("headset-mic", StringComparison.Ordinal))
        {
            return "Микрофон гарнитуры";
        }

        if (text.Contains("headphones", StringComparison.Ordinal))
        {
            return "Наушники";
        }

        if (text.Contains("headset", StringComparison.Ordinal))
        {
            return "Гарнитура";
        }

        if (text.Contains("speaker", StringComparison.Ordinal))
        {
            return "Динамики";
        }

        if (text.Contains("line-in", StringComparison.Ordinal))
        {
            return "Линейный вход";
        }

        if (text.Contains("line-out", StringComparison.Ordinal))
        {
            return "Линейный выход";
        }

        if (text.Contains("hdmi", StringComparison.Ordinal))
        {
            return "HDMI / DisplayPort";
        }

        if (text.Contains("mic", StringComparison.Ordinal))
        {
            return "Микрофон";
        }

        if (text.Contains("line", StringComparison.Ordinal))
        {
            return isInput ? "Линейный вход" : "Линейный выход";
        }

        return id;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var trimmed = value.Trim();
        return string.Equals(trimmed, "(null)", StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
    }
}
