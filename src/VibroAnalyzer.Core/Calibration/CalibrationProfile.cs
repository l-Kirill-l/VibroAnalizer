namespace VibroAnalyzer.Core.Calibration;

/// <summary>
/// Режим отображения измеренных значений.
/// </summary>
public enum CalibrationMode
{
    Voltage,
    Current,
    Physical
}

/// <summary>
/// Профиль калибровки измерительного тракта: пересчёт нормализованных
/// отсчётов в вольты, амперы или физические единицы датчика.
/// </summary>
public sealed class CalibrationProfile
{
    /// <summary>Название профиля.</summary>
    public string Name { get; set; } = "По умолчанию";

    /// <summary>Напряжение полной шкалы АЦП, В.</summary>
    public double FullScaleVolts { get; set; } = 1.0;

    /// <summary>Коэффициент входного усиления.</summary>
    public double InputGain { get; set; } = 1.0;

    /// <summary>Чувствительность датчика.</summary>
    public double SensorSensitivity { get; set; } = 1.0;

    /// <summary>Сопротивление шунта, Ом.</summary>
    public double ShuntOhms { get; set; } = 1.0;

    /// <summary>Текущий режим отображения.</summary>
    public CalibrationMode Mode { get; set; } = CalibrationMode.Voltage;

    /// <summary>Единица измерения для режима <see cref="CalibrationMode.Physical"/>.</summary>
    public string PhysicalUnit { get; set; } = "В";

    /// <summary>Единица измерения, соответствующая текущему режиму.</summary>
    public string Unit => Mode switch
    {
        CalibrationMode.Voltage => "В",
        CalibrationMode.Current => "А",
        CalibrationMode.Physical => PhysicalUnit,
        _ => PhysicalUnit
    };

    /// <summary>
    /// Пересчитывает нормализованный отсчёт (обычно −1…1) в отображаемую величину
    /// согласно текущему режиму калибровки.
    /// </summary>
    public double ToDisplay(double normalizedSample)
    {
        var volts = normalizedSample * FullScaleVolts / InputGain;

        return Mode switch
        {
            CalibrationMode.Voltage => volts / SensorSensitivity,
            CalibrationMode.Current => volts / ShuntOhms / SensorSensitivity,
            CalibrationMode.Physical => volts / SensorSensitivity,
            _ => volts
        };
    }
}
