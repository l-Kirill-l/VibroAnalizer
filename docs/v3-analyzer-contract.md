# VibroAnalyzer v3 — контракт анализаторов

Общие типы уже есть в `src/VibroAnalyzer.Core/Analysis`:

```csharp
public abstract class AnalyzerBase
{
    public Guid Id { get; }
    public abstract string Title { get; }
    public virtual string Category => "Общие измерения";
    public virtual string Description => "Описание анализатора не задано.";
    public virtual AnalyzerHelp Help => AnalyzerHelpCatalog.Get(GetType(), Description);
    public bool IsEnabled { get; set; }
    public AnalyzerSource Source { get; set; }
    public int Channel { get; set; }
    public CalibrationProfile Calibration { get; set; }
    public DateTimeOffset LastUpdated { get; protected set; }
    public abstract int GetWindowFrames(int sampleRate);
    public abstract void Process(double[] samples, int sampleRate);
    protected void MarkUpdated();
}

public sealed record AnalyzerHelp(
    string Purpose,
    string Formulas,
    string Details,
    string Usage,
    string Limitations);

public sealed record AnalyzerReadout(string Name, double Value, string Units);

public interface IAnalyzerView
{
    string XLabel { get; }
    string YLabel { get; }
    double[] X { get; }
    double[] Y { get; }
    IReadOnlyList<AnalyzerReadout> Readouts { get; }
}

public sealed record AnalyzerParameter(
    string Name, double Minimum, double Maximum,
    Func<double> Getter, Action<double> Setter, string Units = "");

public interface IAnalyzerParameterized
{
    IReadOnlyList<AnalyzerParameter> Parameters { get; }
}

public sealed record AnalyzerToggle(
    string Name, Func<bool> Getter, Action<bool> Setter);

public interface IAnalyzerToggleable
{
    IReadOnlyList<AnalyzerToggle> Toggles { get; }
}

public sealed class AnalysisFilterSettings
{
    public bool BandPassEnabled { get; set; }
    public double HighPassHz { get; set; }
    public double LowPassHz { get; set; }
    public bool MainsNotchEnabled { get; set; }
    public bool Notch50Enabled { get; set; }
    public bool Notch60Enabled { get; set; }
    public double[] Apply(double[] samples, int sampleRate);
}
```

## Правила

- Наследоваться от `AnalyzerBase`; при наличии кривой реализовать `IAnalyzerView`;
  при настраиваемых параметрах — `IAnalyzerParameterized`.
- `Process` не должен бросать исключения на пустом/коротком массиве.
- Результаты публиковать новыми массивами; UI читает их из другого потока.
- `GetWindowFrames`: обычно 4096–16384; для медленных процессов можно 8192.
- Параметры — свойства с разумными значениями по умолчанию; параметры
  создавать в `Parameters` каждый раз заново (или кэшировать).
- Отключаемые фильтры оформлять через `IAnalyzerToggleable` и
  `AnalysisFilterSettings`; фильтрация не должна изменять входной массив.
- В справке обязательно указывать, что меняется при отключении фильтра.
- Комментарии и пользовательские строки — по-русски.
- Не менять `.csproj`, `.slnx`, общие файлы, `VibroWaveLab`.
- Не запускать `dotnet build/test` (сборку делает главный агент).
- Тесты создавать отдельными файлами в `tests/VibroAnalyzer.Tests/`.

## Категории

`Общие измерения`, `Акустика`, `Электрика`, `Музыка`, `Диагностика`,
`Вибрация и механика`, `Сейсмология`, `Медицина`, `Частоты`, `Время`.
