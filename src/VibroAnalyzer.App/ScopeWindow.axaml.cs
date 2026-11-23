using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using VibroAnalyzer.Core.Analysis;

namespace VibroAnalyzer.App;

/// <summary>
/// Окно осциллографа: отображает временную форму сигнала и основные измерения.
/// </summary>
public partial class ScopeWindow : Window
{
    private const double MinTimeWindowSeconds = 0.005;
    private const double MaxTimeWindowSeconds = 0.5;
    private const int MinChannel = 0;
    private const int MaxChannel = 7;

    private readonly OscilloscopeAnalyzer _analyzer;
    private readonly DispatcherTimer _timer;

    public ScopeWindow(OscilloscopeAnalyzer analyzer)
        : this(analyzer, null)
    {
    }

    public ScopeWindow(OscilloscopeAnalyzer analyzer, string? busName)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        InitializeComponent();

        BoundText.Text = string.IsNullOrWhiteSpace(busName)
            ? "Шина не задана"
            : $"Шина: {busName}";
        HelpUi.Attach(HelpButton, analyzer.Help);

        // Начальные значения элементов управления берём из анализатора.
        TimeWindowBox.Minimum = (decimal)MinTimeWindowSeconds;
        TimeWindowBox.Maximum = (decimal)MaxTimeWindowSeconds;
        double timeWindowSeconds = Math.Clamp(
            _analyzer.TimeWindowSeconds,
            MinTimeWindowSeconds,
            MaxTimeWindowSeconds);
        TimeWindowBox.Value = (decimal)timeWindowSeconds;
        _analyzer.TimeWindowSeconds = timeWindowSeconds;

        ChannelBox.Minimum = MinChannel;
        ChannelBox.Maximum = MaxChannel;
        int channel = Math.Clamp(_analyzer.Channel, MinChannel, MaxChannel);
        ChannelBox.Value = channel;
        _analyzer.Channel = channel;

        bool demodulated = _analyzer.Source == AnalyzerSource.Demodulated;
        SourceBox.SelectedIndex = demodulated ? 1 : 0;
        _analyzer.Source = demodulated ? AnalyzerSource.Demodulated : AnalyzerSource.Raw;

        // Обратная связь: изменения в UI сразу попадают в анализатор.
        TimeWindowBox.ValueChanged += (_, _) =>
        {
            if (TimeWindowBox.Value is { } value)
            {
                _analyzer.TimeWindowSeconds = Math.Clamp(
                    (double)value,
                    MinTimeWindowSeconds,
                    MaxTimeWindowSeconds);
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
        PlotView.Plot.XLabel("Время, с");

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick += (_, _) => UpdatePlot();

        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdatePlot()
    {
        try
        {
            string unit = GetCalibrationUnit();

            double[] times = _analyzer.Times;
            double[] values = _analyzer.Values;
            int count = Math.Min(times.Length, values.Length);

            var plot = PlotView.Plot;
            plot.Clear();
            plot.YLabel($"Амплитуда, {unit}");

            VppUnit.Text = unit;
            RmsUnit.Text = unit;
            DcUnit.Text = unit;

            // До первого измерения показываем прочерки, а не нули.
            if (_analyzer.LastUpdated == default || count <= 1)
            {
                VppLabel.Text = "—";
                RmsLabel.Text = "—";
                FrequencyLabel.Text = "—";
                DcLabel.Text = "—";
                PlotView.Refresh();
                return;
            }

            var signal = times.Length == values.Length
                ? plot.Add.SignalXY(times, values, PlotTheme.Accent)
                : plot.Add.SignalXY(
                    times.AsSpan(0, count).ToArray(),
                    values.AsSpan(0, count).ToArray(),
                    PlotTheme.Accent);

            signal.LineWidth = 1.5f;
            plot.Axes.AutoScale();

            VppLabel.Text = _analyzer.Vpp.ToString("0.####");
            RmsLabel.Text = _analyzer.Rms.ToString("0.####");
            FrequencyLabel.Text = _analyzer.FrequencyHz.ToString("0.##");
            DcLabel.Text = _analyzer.DcOffset.ToString("0.####");

            PlotView.Refresh();
        }
        catch (Exception ex)
        {
            // Анализатор может обновляться параллельно — окно не должно падать.
            Debug.WriteLine($"Осциллограф: не удалось обновить график: {ex.Message}");
        }
    }

    private string GetCalibrationUnit()
    {
        string? unit = _analyzer.Calibration?.Unit;
        return string.IsNullOrWhiteSpace(unit) ? "В" : unit;
    }
}
