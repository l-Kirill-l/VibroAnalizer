namespace VibroAnalyzer.Core.Routing;

/// <summary>
/// Полоса, выделяемая из источника перед смешиванием.
/// </summary>
public enum FusionBand
{
    /// <summary>Полный спектр источника.</summary>
    Full,

    /// <summary>Только низкие частоты (фильтр нижних частот).</summary>
    LowPass,

    /// <summary>Только высокие частоты (фильтр верхних частот).</summary>
    HighPass
}

/// <summary>
/// Способ объединения нескольких источников в один виртуальный вход.
/// </summary>
public enum FusionMode
{
    /// <summary>Среднее арифметическое без учёта весов.</summary>
    Average,

    /// <summary>Нормированное взвешенное среднее.</summary>
    Weighted,

    /// <summary>Сумма с учётом весов.</summary>
    Sum,

    /// <summary>Разность первого источника и суммы остальных.</summary>
    Difference,

    /// <summary>Поэлементный минимум.</summary>
    Minimum,

    /// <summary>Поэлементный максимум.</summary>
    Maximum,

    /// <summary>Сумма полос: например, НЧ-датчик + ВЧ-датчик.</summary>
    BandMerge
}

/// <summary>
/// Описание одного источника виртуального входа.
/// </summary>
public sealed record FusionSource(
    ISignalBus Source,
    double Gain = 1.0,
    FusionBand Band = FusionBand.Full,
    double CutoffHz = 1000.0);
