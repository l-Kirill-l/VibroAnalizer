namespace VibroAnalyzer.Core.Analysis;

/// <summary>Строка показаний анализатора для универсального окна.</summary>
public sealed record AnalyzerReadout(string Name, double Value, string Units);

/// <summary>
/// Универсальное представление результата: одна кривая X/Y и набор показаний.
/// Анализатор может вернуть пустые массивы, если ему достаточно показаний.
/// </summary>
public interface IAnalyzerView
{
    string XLabel { get; }

    string YLabel { get; }

    double[] X { get; }

    double[] Y { get; }

    IReadOnlyList<AnalyzerReadout> Readouts { get; }
}

/// <summary>
/// Настраиваемый параметр анализатора для универсального окна.
/// </summary>
public sealed record AnalyzerParameter(
    string Name,
    double Minimum,
    double Maximum,
    Func<double> Getter,
    Action<double> Setter,
    string Units = "");

/// <summary>
/// Анализатор с настраиваемыми параметрами (обороты, полосы, пороги).
/// </summary>
public interface IAnalyzerParameterized
{
    IReadOnlyList<AnalyzerParameter> Parameters { get; }
}

/// <summary>Логический переключатель анализатора (например, фильтр).</summary>
public sealed record AnalyzerToggle(
    string Name,
    Func<bool> Getter,
    Action<bool> Setter);

/// <summary>Анализатор с отключаемыми настройками.</summary>
public interface IAnalyzerToggleable
{
    IReadOnlyList<AnalyzerToggle> Toggles { get; }
}
