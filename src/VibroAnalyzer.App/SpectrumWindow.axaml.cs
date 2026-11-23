using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.App;

/// <summary>
/// Окно спектра: отображает АЧХ в дБ относительно полной шкалы и измерения пика.
/// </summary>
public partial class SpectrumWindow : Window
{
    private const double MinAveragingFactor = 0.01;
    private const double MaxAveragingFactor = 1.0;
    private const int MinChannel = 0;
    private const int MaxChannel = 7;

    private static readonly int[] FftSizes = [1024, 2048, 4096, 8192, 16384, 32768];

    private static readonly WindowKind[] WindowKinds =
    [
        WindowKind.Rectangular,
        WindowKind.Hann,
        WindowKind.Hamming,
        WindowKind.BlackmanHarris,
        WindowKind.FlatTop,
    ];

    private static readonly string[] WindowNames =
    [
        "Rectangular",
        "Hann",
        "Hamming",
        "BlackmanHarris",
        "FlatTop",
    ];

    private readonly SpectrumAnalyzer _analyzer;
    private readonly DispatcherTimer _timer;

    public SpectrumWindow(SpectrumAnalyzer analyzer)
        : this(analyzer, null)
    {
    }

    public SpectrumWindow(SpectrumAnalyzer analyzer, string? busName)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        InitializeComponent();

        BoundText.Text = string.IsNullOrWhiteSpace(busName)
            ? "Шина не задана"
            : $"Шина: {busName}";
        HelpUi.Attach(HelpButton, analyzer.Help, analyzer.Title);

        FftSizeBox.ItemsSource = FftSizes;
        WindowBox.ItemsSource = WindowNames;

        // Приводим настройки анализатора к значениям, которые поддерживает UI.
        int fftSizeIndex = FindFftSizeIndex(_analyzer.FftSize);
        FftSizeBox.SelectedIndex = fftSizeIndex;
        _analyzer.FftSize = FftSizes[fftSizeIndex];

        int windowIndex = Array.IndexOf(WindowKinds, _analyzer.Window);
        if (windowIndex < 0)
        {
            windowIndex = Array.IndexOf(WindowKinds, WindowKind.Hann);
        }

        WindowBox.SelectedIndex = windowIndex;
        _analyzer.Window = WindowKinds[windowIndex];

        AveragingBox.Minimum = (decimal)MinAveragingFactor;
        AveragingBox.Maximum = (decimal)MaxAveragingFactor;
        double averagingFactor = Math.Clamp(
            _analyzer.AveragingFactor,
            MinAveragingFactor,
            MaxAveragingFactor);
        AveragingBox.Value = (decimal)averagingFactor;
        _analyzer.AveragingFactor = averagingFactor;

        ChannelBox.Minimum = MinChannel;
        ChannelBox.Maximum = MaxChannel;
        int channel = Math.Clamp(_analyzer.Channel, MinChannel, MaxChannel);
        ChannelBox.Value = channel;
        _analyzer.Channel = channel;

        bool demodulated = _analyzer.Source == AnalyzerSource.Demodulated;
        SourceBox.SelectedIndex = demodulated ? 1 : 0;
        _analyzer.Source = demodulated ? AnalyzerSource.Demodulated : AnalyzerSource.Raw;

        // Обратная связь: изменения в UI сразу попадают в анализатор.
        FftSizeBox.SelectionChanged += (_, _) =>
        {
            int index = FftSizeBox.SelectedIndex;
            if (index >= 0 && index < FftSizes.Length)
            {
                _analyzer.FftSize = FftSizes[index];
            }
        };

        WindowBox.SelectionChanged += (_, _) =>
        {
            int index = WindowBox.SelectedIndex;
            if (index >= 0 && index < WindowKinds.Length)
            {
                _analyzer.Window = WindowKinds[index];
            }
        };

        AveragingBox.ValueChanged += (_, _) =>
        {
            if (AveragingBox.Value is { } value)
            {
                _analyzer.AveragingFactor = Math.Clamp(
                    (double)value,
                    MinAveragingFactor,
                    MaxAveragingFactor);
            }
        };

        ChannelBox.ValueChanged += (_, _) =>
        {
            if (ChannelBox.Value is { } value)
            {
                _analyzer.Channel = (int)Math.Clamp(value, MinChannel, MaxChannel);
            }
        };

        SourceBox.SelectionChanged += (_, _) =>
        {
            if (SourceBox.SelectedIndex < 0)
            {
                return;
            }

            _analyzer.Source = SourceBox.SelectedIndex == 1
                ? AnalyzerSource.Demodulated
                : AnalyzerSource.Raw;
        };

        PlotTheme.Apply(PlotView.Plot);
        PlotView.Plot.XLabel("Частота, Гц");
        PlotView.Plot.YLabel("Уровень, дБFS (ампл.)");

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += (_, _) => UpdateSpectrum();

        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateSpectrum()
    {
        try
        {
            double[] frequencies = _analyzer.Frequencies;
            double[] magnitudes = _analyzer.MagnitudesDb;
            int count = Math.Min(frequencies.Length, magnitudes.Length);

            var plot = PlotView.Plot;
            plot.Clear();

            // До первого измерения показываем прочерки, а не нули.
            if (_analyzer.LastUpdated == default || count <= 1)
            {
                PeakLabel.Text = "—";
                PeakLevelLabel.Text = "—";
                NoiseFloorLabel.Text = "—";
                PlotView.Refresh();
                return;
            }

            var signal = frequencies.Length == magnitudes.Length
                ? plot.Add.SignalXY(frequencies, magnitudes, PlotTheme.AccentBlue)
                : plot.Add.SignalXY(
                    frequencies.AsSpan(0, count).ToArray(),
                    magnitudes.AsSpan(0, count).ToArray(),
                    PlotTheme.AccentBlue);

            signal.LineWidth = 1.2f;
            plot.Axes.AutoScale();

            PeakLabel.Text = _analyzer.PeakFrequencyHz.ToString("0.##");
            PeakLevelLabel.Text = _analyzer.PeakLevelDb.ToString("0.#");
            NoiseFloorLabel.Text = _analyzer.NoiseFloorDb.ToString("0.#");

            PlotView.Refresh();
        }
        catch (Exception ex)
        {
            // Анализатор может обновляться параллельно — окно не должно падать.
            Debug.WriteLine($"Спектр: не удалось обновить график: {ex.Message}");
        }
    }

    private static int FindFftSizeIndex(int fftSize)
    {
        int index = Array.IndexOf(FftSizes, fftSize);
        if (index >= 0)
        {
            return index;
        }

        // Неподдерживаемый размер заменяем ближайшим допустимым.
        int best = 0;
        for (int i = 1; i < FftSizes.Length; i++)
        {
            if (Math.Abs(FftSizes[i] - fftSize) < Math.Abs(FftSizes[best] - fftSize))
            {
                best = i;
            }
        }

        return best;
    }
}
