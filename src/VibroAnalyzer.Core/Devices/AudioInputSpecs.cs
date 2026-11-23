using System.Collections.Generic;

namespace VibroAnalyzer.Core.Devices;

/// <summary>
/// Типичные электрические параметры входа для выбранного типа разъёма.
/// Значения ориентировочные: точные зависят от аналогового тракта карты.
/// </summary>
public sealed record AudioInputSpecs(
    string Kind,
    bool IsInput,
    bool IsOutput,
    double? MaxVoltageRms,
    double? MaxCurrentMa,
    double? ImpedanceOhms,
    double? MinFrequencyHz,
    double? MaxFrequencyHz,
    int? BitDepth,
    string Notes);

/// <summary>
/// Подбирает параметры входа по типу разъёма: микрофонный, линейный,
/// комбинированный или цифровой.
/// </summary>
public static class AudioInputSpecsCatalog
{
    public static AudioInputSpecs For(
        AudioJackType jackType,
        bool isInput,
        bool isOutput,
        int? bitDepth)
    {
        if (!isInput)
        {
            return new AudioInputSpecs(
                "выход",
                IsInput: false,
                IsOutput: true,
                MaxVoltageRms: null,
                MaxCurrentMa: null,
                ImpedanceOhms: null,
                MinFrequencyHz: null,
                MaxFrequencyHz: null,
                BitDepth: bitDepth,
                Notes: "");
        }

        var baseKind = jackType switch
        {
            AudioJackType.Microphone => "микрофонный вход",
            AudioJackType.Headset => "микрофонный вход гарнитуры",
            AudioJackType.LineIn => "линейный вход",
            AudioJackType.Digital => "цифровой вход",
            _ => "вход",
        };

        var kind = isOutput ? $"комбо-разъём: {baseKind} + выход" : baseKind;

        if (jackType == AudioJackType.Digital)
        {
            return new AudioInputSpecs(
                kind,
                IsInput: true,
                IsOutput: isOutput,
                MaxVoltageRms: null,
                MaxCurrentMa: null,
                ImpedanceOhms: null,
                MinFrequencyHz: null,
                MaxFrequencyHz: null,
                BitDepth: bitDepth,
                Notes: "");
        }

        return jackType switch
        {
            AudioJackType.Microphone or AudioJackType.Headset => new AudioInputSpecs(
                kind,
                IsInput: true,
                IsOutput: isOutput,
                MaxVoltageRms: 0.1,
                MaxCurrentMa: 0.5,
                ImpedanceOhms: 2000,
                MinFrequencyHz: 20,
                MaxFrequencyHz: 20000,
                BitDepth: bitDepth,
                Notes: "микрофонное питание и импеданс — типичные оценки"),

            AudioJackType.LineIn => new AudioInputSpecs(
                kind,
                IsInput: true,
                IsOutput: isOutput,
                MaxVoltageRms: 2.0,
                MaxCurrentMa: 0.1,
                ImpedanceOhms: 10000,
                MinFrequencyHz: 20,
                MaxFrequencyHz: 20000,
                BitDepth: bitDepth,
                Notes: "номинальный линейный уровень — типичный"),

            _ => new AudioInputSpecs(
                kind,
                IsInput: true,
                IsOutput: isOutput,
                MaxVoltageRms: 1.0,
                MaxCurrentMa: 0.1,
                ImpedanceOhms: 10000,
                MinFrequencyHz: 20,
                MaxFrequencyHz: 20000,
                BitDepth: bitDepth,
                Notes: "тип входа не определён — значения ориентировочные"),
        };
    }

    public static string Describe(AudioInputSpecs specs)
    {
        ArgumentNullException.ThrowIfNull(specs);

        var parts = new List<string> { specs.Kind };

        if (!specs.IsInput)
        {
            parts.Add("параметры входа неприменимы");
        }
        else if (specs.MaxVoltageRms is not { } volts)
        {
            parts.Add("аналоговые параметры неприменимы");
        }
        else
        {
            parts.Add($"напряжение (типично) до {volts:0.###} В СКЗ");

            if (specs.MaxCurrentMa is { } current)
            {
                parts.Add($"ток (оценка) до {current:0.###} мА");
            }

            if (specs.ImpedanceOhms is { } impedance)
            {
                parts.Add($"импеданс (типично) {impedance / 1000.0:0.###} кОм");
            }

            if (specs.MinFrequencyHz is { } min && specs.MaxFrequencyHz is { } max)
            {
                parts.Add($"полоса {min:0}–{max:0} Гц");
            }
        }

        if (specs.BitDepth is { } bits)
        {
            parts.Add($"{bits} бит");
        }

        if (!string.IsNullOrWhiteSpace(specs.Notes))
        {
            parts.Add(specs.Notes);
        }

        return string.Join(" · ", parts);
    }
}
