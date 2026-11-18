namespace VibroAnalyzer.Core.Dsp;

/// <summary>
/// Тип оконной функции, применяемой перед БПФ.
/// </summary>
public enum WindowKind
{
    Rectangular,
    Hann,
    Hamming,
    BlackmanHarris,
    FlatTop
}

/// <summary>
/// Генерация и обработка оконных функций.
/// </summary>
public static class WindowFunctions
{
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>
    /// Создаёт окно заданного типа и длины.
    /// Для длины 1 возвращает массив из одного элемента 1.0, для длины 0 — пустой массив.
    /// </summary>
    public static double[] Create(WindowKind kind, int length)
    {
        if (length <= 0)
        {
            return Array.Empty<double>();
        }

        if (length == 1)
        {
            return new[] { 1.0 };
        }

        var window = new double[length];
        var denominator = length - 1;

        for (var n = 0; n < length; n++)
        {
            var phase = TwoPi * n / denominator;

            window[n] = kind switch
            {
                WindowKind.Rectangular => 1.0,
                WindowKind.Hann => 0.5 * (1.0 - Math.Cos(phase)),
                WindowKind.Hamming => 0.54 - 0.46 * Math.Cos(phase),
                WindowKind.BlackmanHarris =>
                    0.35875
                    - 0.48829 * Math.Cos(phase)
                    + 0.14128 * Math.Cos(2.0 * phase)
                    - 0.01168 * Math.Cos(3.0 * phase),
                WindowKind.FlatTop =>
                    0.21557895
                    - 0.41663158 * Math.Cos(phase)
                    + 0.277263158 * Math.Cos(2.0 * phase)
                    - 0.083578947 * Math.Cos(3.0 * phase)
                    + 0.006947368 * Math.Cos(4.0 * phase),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный тип окна.")
            };
        }

        return window;
    }

    /// <summary>
    /// Сумма коэффициентов окна (когерентное усиление, умноженное на длину).
    /// </summary>
    public static double Sum(double[] window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var sum = 0.0;
        foreach (var value in window)
        {
            sum += value;
        }

        return sum;
    }
}
