using VibroAnalyzer.Core.Calibration;

namespace VibroAnalyzer.Core.Analysis;

/// <summary>
/// Источник данных, который использует анализатор.
/// </summary>
public enum AnalyzerSource
{
    /// <summary>Исходный сигнал с измерительного канала.</summary>
    Raw,

    /// <summary>Демодулированный сигнал.</summary>
    Demodulated
}

/// <summary>
/// Базовый класс анализатора: общие настройки, источник данных и калибровка.
/// </summary>
public abstract class AnalyzerBase
{
    /// <summary>Уникальный идентификатор экземпляра анализатора.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Отображаемое название анализатора.</summary>
    public abstract string Title { get; }

    /// <summary>Категория для каталога анализаторов.</summary>
    public virtual string Category => "Общие измерения";

    /// <summary>Краткое описание: как работает и для чего нужен.</summary>
    public virtual string Description => "Описание анализатора не задано.";

    /// <summary>Подробная справка с формулами и нюансами.</summary>
    public virtual AnalyzerHelp Help => AnalyzerHelpCatalog.Get(GetType(), Description);

    /// <summary>Признак участия анализатора в обработке.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Источник данных для обработки.</summary>
    public AnalyzerSource Source { get; set; } = AnalyzerSource.Raw;

    /// <summary>Индекс канала; при обработке ограничивается числом каналов буфера.</summary>
    public int Channel { get; set; }

    /// <summary>Профиль калибровки, применяемый к отсчётам.</summary>
    public CalibrationProfile Calibration { get; set; } = new();

    /// <summary>Время последнего успешного обновления результатов (UTC).</summary>
    public DateTimeOffset LastUpdated { get; protected set; }

    /// <summary>
    /// Возвращает число кадров, необходимое анализатору при заданной частоте дискретизации.
    /// </summary>
    public abstract int GetWindowFrames(int sampleRate);

    /// <summary>
    /// Обрабатывает очередное окно отсчётов.
    /// </summary>
    public abstract void Process(double[] samples, int sampleRate);

    /// <summary>Отмечает результаты как обновлённые текущим временем UTC.</summary>
    protected void MarkUpdated() => LastUpdated = DateTimeOffset.UtcNow;
}
