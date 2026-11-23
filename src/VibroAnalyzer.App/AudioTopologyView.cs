using System;
using System.Collections.Generic;
using System.Linq;
using VibroAnalyzer.Core.Devices;

namespace VibroAnalyzer.App;

internal sealed record CardOption(
    string Id,
    string Name,
    IReadOnlyList<PortOption> Inputs,
    IReadOnlyList<PortOption> Outputs)
{
    public override string ToString() => Name;
}

internal sealed record PortOption(
    string Key,
    string Name,
    bool IsInput,
    bool IsOutput,
    AudioJackType JackType,
    PortAvailability Availability,
    AudioDeviceInfo Device,
    AudioPortInfo Port,
    int ChannelCount,
    IReadOnlyList<ChannelOption> Channels)
{
    public string Direction => IsInput && IsOutput ? "вход/выход" : IsInput ? "вход" : "выход";

    public string Display
    {
        get
        {
            var parts = new List<string> { Direction, Name };
            if (ChannelCount > 0)
            {
                parts.Add($"{ChannelCount} кан.");
            }

            if (Availability == PortAvailability.Unavailable)
            {
                parts.Add("не подключён");
            }

            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => Display;
}

internal sealed record ChannelOption(
    int Index,
    string Name,
    string Purpose,
    AudioDeviceInfo Device,
    bool IsInput)
{
    public int PhysicalChannel => Index;

    public string Display => $"{Index + 1} · {Purpose}";

    public override string ToString() => Display;
}

internal static class AudioTopologyView
{
    public static IReadOnlyList<CardOption> Build(IReadOnlyList<AudioDeviceInfo> devices)
    {
        var cards = new Dictionary<string, CardBuilder>(StringComparer.Ordinal);

        foreach (var device in devices.Where(item => !item.IsMonitor))
        {
            var cardKey = string.IsNullOrWhiteSpace(device.CardId)
                ? device.Id
                : device.CardId;

            if (!cards.TryGetValue(cardKey, out var card))
            {
                card = new CardBuilder(cardKey, GetCardName(device));
                cards.Add(cardKey, card);
            }

            card.Name = ChooseCardName(card.Name, GetCardName(device));

            if (device.Ports.Count == 0)
            {
                card.AddFallback(device);
                continue;
            }

            foreach (var port in device.Ports)
            {
                if (port.IsInput
                    && device.IsInput
                    && Matches(device, port, isInput: true))
                {
                    card.AddInput(port, device);
                }

                if (port.IsOutput
                    && device.IsOutput
                    && Matches(device, port, isInput: false))
                {
                    card.AddOutput(port, device);
                }
            }
        }

        return cards.Values
            .Select(card => card.Build())
            .OrderBy(card => card.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static string GetPortDirectionId(AudioPortInfo port, bool isInput)
    {
        var ids = port.Id.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 2)
        {
            return isInput ? ids[1] : ids[0];
        }

        return port.Id;
    }

    public static string DescribePurpose(
        AudioJackType jackType,
        bool isInput,
        int index,
        int count)
    {
        if (jackType is AudioJackType.Digital or AudioJackType.Monitor)
        {
            return $"цифровой канал {index + 1}";
        }

        if (isInput)
        {
            if (count <= 1)
            {
                return jackType switch
                {
                    AudioJackType.Microphone => "микрофонный вход",
                    AudioJackType.Headset => "микрофонный вход гарнитуры",
                    AudioJackType.LineIn => "линейный вход",
                    _ => "вход",
                };
            }

            return count == 2
                ? index == 0 ? "левый вход" : "правый вход"
                : $"вход · канал {index + 1}";
        }

        if (count <= 1)
        {
            return jackType == AudioJackType.Speaker ? "выход на динамик" : "выход";
        }

        return count == 2
            ? index == 0 ? "левый выход" : "правый выход"
            : $"выход · канал {index + 1}";
    }

    private static bool Matches(AudioDeviceInfo device, AudioPortInfo port, bool isInput)
    {
        if (string.IsNullOrWhiteSpace(device.PortId))
        {
            return true;
        }

        return string.Equals(
            device.PortId,
            GetPortDirectionId(port, isInput),
            StringComparison.Ordinal);
    }

    private static string GetCardName(AudioDeviceInfo device)
    {
        if (!string.IsNullOrWhiteSpace(device.ControllerName))
        {
            return device.ControllerName;
        }

        if (!string.IsNullOrWhiteSpace(device.PortName))
        {
            return device.PortName;
        }

        return device.Name;
    }

    private static string ChooseCardName(string current, string candidate)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return candidate;
        }

        if (string.IsNullOrWhiteSpace(candidate)
            || string.Equals(current, candidate, StringComparison.Ordinal))
        {
            return current;
        }

        // Для одной карты устройства входа/выхода могут давать разные имена.
        // Оставляем более короткое/информативное, а полное имя доступно в подсказках.
        return candidate.Length < current.Length ? candidate : current;
    }

    private sealed class CardBuilder
    {
        private readonly Dictionary<string, PortBuilder> _inputs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PortBuilder> _outputs = new(StringComparer.Ordinal);

        public CardBuilder(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }

        public string Name { get; set; }

        public void AddInput(AudioPortInfo port, AudioDeviceInfo device)
        {
            if (!_inputs.TryGetValue(port.Id, out var builder))
            {
                builder = new PortBuilder(port, isInput: true);
                _inputs.Add(port.Id, builder);
            }

            builder.SetDevice(device);
        }

        public void AddOutput(AudioPortInfo port, AudioDeviceInfo device)
        {
            if (!_outputs.TryGetValue(port.Id, out var builder))
            {
                builder = new PortBuilder(port, isInput: false);
                _outputs.Add(port.Id, builder);
            }

            builder.SetDevice(device);
        }

        public void AddFallback(AudioDeviceInfo device)
        {
            var isInput = device.IsInput;
            var port = new AudioPortInfo(
                $"{device.Id}:fallback",
                isInput ? "Вход устройства" : "Выход устройства",
                device.Channels.FirstOrDefault()?.JackType
                    ?? (isInput ? AudioJackType.LineIn : AudioJackType.LineOut),
                isInput,
                !isInput,
                PortAvailability.Unknown,
                Array.Empty<AudioPinInfo>())
            {
                InputChannels = isInput ? Math.Max(1, device.InputChannels) : 0,
                OutputChannels = isInput ? 0 : Math.Max(1, device.OutputChannels),
            };

            if (isInput)
            {
                if (!_inputs.TryGetValue(port.Id, out var builder))
                {
                    builder = new PortBuilder(port, isInput: true);
                    _inputs.Add(port.Id, builder);
                }

                builder.SetDevice(device);
            }
            else
            {
                if (!_outputs.TryGetValue(port.Id, out var builder))
                {
                    builder = new PortBuilder(port, isInput: false);
                    _outputs.Add(port.Id, builder);
                }

                builder.SetDevice(device);
            }
        }

        public CardOption Build()
        {
            var inputs = _inputs.Values
                .OrderBy(port => port.Name, StringComparer.Ordinal)
                .Select(port => port.Build())
                .ToList();
            var outputs = _outputs.Values
                .OrderBy(port => port.Name, StringComparer.Ordinal)
                .Select(port => port.Build())
                .ToList();

            return new CardOption(Id, Name, inputs, outputs);
        }
    }

    private sealed class PortBuilder
    {
        private readonly AudioPortInfo _port;
        private readonly bool _isInput;
        private AudioDeviceInfo? _device;

        public PortBuilder(AudioPortInfo port, bool isInput)
        {
            _port = port;
            _isInput = isInput;
        }

        public string Name => _port.Name;

        public void SetDevice(AudioDeviceInfo device)
        {
            _device = device;
        }

        public PortOption Build()
        {
            if (_device is null)
            {
                throw new InvalidOperationException(
                    $"Для порта «{_port.Name}» не найдено устройство.");
            }

            var device = _device;
            var count = _isInput
                ? Math.Max(1, device.InputChannels)
                : Math.Max(1, device.OutputChannels);

            var channels = new List<ChannelOption>(count);
            for (var index = 0; index < count; index++)
            {
                var channel = device.Channels.FirstOrDefault(item =>
                    item.Index == index
                    && (item.IsInput == _isInput));
                var name = channel?.Name ?? $"Канал {index + 1}";
                var purpose = DescribePurpose(_port.JackType, _isInput, index, count);
                channels.Add(new ChannelOption(index, name, purpose, device, _isInput));
            }

            return new PortOption(
                $"{device.CardId}:{_isInput}:{_port.Id}",
                _port.Name,
                IsInput: _isInput,
                IsOutput: !_isInput,
                _port.JackType,
                _port.Availability,
                device,
                _port,
                count,
                channels);
        }
    }
}

internal sealed record SignalSelection(
    CardOption Card,
    PortOption Port,
    ChannelOption Channel);
