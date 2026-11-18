using System;
using System.Collections.Generic;
using VibroAnalyzer.Audio.PipeWire;
using VibroAnalyzer.Audio.Simulated;
using VibroAnalyzer.Audio.Wasapi;
using VibroAnalyzer.Core.Audio;

namespace VibroAnalyzer.Audio;

/// <summary>
/// Выбор и создание аудиобэкенда. По умолчанию берётся первый доступный
/// в текущей системе, а если ничего не найдено — демонстрационный тракт.
/// </summary>
public static class AudioBackendFactory
{
    private const string PipeWireName = "PipeWire";
    private const string WasapiName = "WASAPI";
    private const string DemoName = "Демо";

    /// <summary>
    /// Имена бэкендов, доступных в текущей системе. Демо-бэкенд присутствует всегда.
    /// </summary>
    public static IReadOnlyList<string> AvailableBackendNames
    {
        get
        {
            var names = new List<string>();

            if (IsSafeAvailable(() => new PipeWireAudioBackend().IsAvailable))
            {
                names.Add(PipeWireName);
            }

            if (IsSafeAvailable(() => new WasapiAudioBackend().IsAvailable))
            {
                names.Add(WasapiName);
            }

            names.Add(DemoName);
            return names;
        }
    }

    /// <summary>
    /// Создаёт бэкенд по имени. Без имени выбирает первый доступный.
    /// </summary>
    public static IAudioBackend Create(string? name = null)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return CreateByName(name.Trim());
        }

        var pipeWire = new PipeWireAudioBackend();
        if (pipeWire.IsAvailable)
        {
            return pipeWire;
        }

        var wasapi = new WasapiAudioBackend();
        if (wasapi.IsAvailable)
        {
            return wasapi;
        }

        return new SimulatedAudioBackend();
    }

    private static IAudioBackend CreateByName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "pipewire" or "pipe wire" or "pw" => new PipeWireAudioBackend(),
            "wasapi" or "windows audio" => new WasapiAudioBackend(),
            "демо" or "demo" or "sim" or "simulated" or "симулятор" => new SimulatedAudioBackend(),
            _ => throw new AudioBackendException($"Неизвестный аудиобэкенд: «{name}»."),
        };
    }

    private static bool IsSafeAvailable(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
