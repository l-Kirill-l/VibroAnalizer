using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Calibration;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Core.Routing;

/// <summary>
/// Логическая шина сигнала: вход устройства, виртуальный (смешанный) вход
/// или выход. Окна анализаторов и демодуляции привязываются к шине.
/// </summary>
public interface ISignalBus
{
    /// <summary>Уникальный идентификатор шины.</summary>
    Guid Id { get; }

    /// <summary>Отображаемое имя шины.</summary>
    string Name { get; }

    /// <summary>Частота дискретизации шины, Гц.</summary>
    int SampleRate { get; }

    /// <summary>Число каналов шины. Логические входы всегда моно.</summary>
    int Channels { get; }

    /// <summary>Признак виртуального (смешанного) входа.</summary>
    bool IsVirtual { get; }

    /// <summary>Калибровка шины.</summary>
    CalibrationProfile Calibration { get; }

    /// <summary>Буфер сырого сигнала.</summary>
    SignalRingBuffer RawBuffer { get; }

    /// <summary>Буфер демодулированного сигнала.</summary>
    SignalRingBuffer DemodBuffer { get; }

    /// <summary>Анализаторы, привязанные к этой шине.</summary>
    AnalyzerHost Analyzers { get; }

    /// <summary>Цепь демодуляции этой шины.</summary>
    DemodulationChain Demodulation { get; }

    /// <summary>Подключает анализатор к шине.</summary>
    void AddAnalyzer(AnalyzerBase analyzer);

    /// <summary>Отключает анализатор от шины.</summary>
    void RemoveAnalyzer(AnalyzerBase analyzer);
}
