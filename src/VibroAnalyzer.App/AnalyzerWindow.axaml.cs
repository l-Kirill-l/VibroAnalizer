using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using VibroAnalyzer.Core.Analysis;

namespace VibroAnalyzer.App;

/// <summary>
/// Универсальное окно анализатора: описание, параметры, показания и кривая X/Y.
/// </summary>
public partial class AnalyzerWindow : Window
{
    private readonly AnalyzerBase _analyzer;
    private readonly DispatcherTimer _timer;
    private string _readoutKey = "";

    public AnalyzerWindow(AnalyzerBase analyzer, string? busName = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _analyzer = analyzer;

        InitializeComponent();

        Title = analyzer.Title;
        TitleText.Text = analyzer.Title;
        TagText.Text = $"АНАЛИЗАТОР · {analyzer.Category.ToUpperInvariant()}";
        DescriptionText.Text = analyzer.Description;
        BoundText.Text = string.IsNullOrWhiteSpace(busName)
            ? "Шина не задана"
            : $"Шина: {busName}";
        HelpUi.Attach(HelpButton, analyzer.Help);

        PlotTheme.Apply(PlotView.Plot);
        BuildParameters();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdateView();

        Opened += (_, _) =>
        {
            _timer.Start();
            UpdateView();
        };

        Closed += (_, _) => _timer.Stop();
    }

    private void BuildParameters()
    {
        if (_analyzer is not IAnalyzerParameterized parameterized
            || parameterized.Parameters.Count == 0)
        {
            ParametersCard.IsVisible = false;
            return;
        }

        ParametersCard.IsVisible = true;

        foreach (var parameter in parameterized.Parameters)
        {
            var label = new TextBlock { Text = parameter.Name, VerticalAlignment = VerticalAlignment.Center };
            label.Classes.Add("label");

            var box = new NumericUpDown
            {
                Width = 130,
                Minimum = (decimal)parameter.Minimum,
                Maximum = (decimal)parameter.Maximum,
                Increment = Math.Max(0.01m, (decimal)((parameter.Maximum - parameter.Minimum) / 100.0)),
                FormatString = "0.###",
                ClipValueToMinMax = true,
                Value = (decimal)Math.Clamp(parameter.Getter(), parameter.Minimum, parameter.Maximum),
            };

            box.ValueChanged += (_, _) =>
            {
                if (box.Value is { } value)
                {
                    parameter.Setter(Math.Clamp((double)value, parameter.Minimum, parameter.Maximum));
                }
            };

            var unit = new TextBlock { Text = parameter.Units, VerticalAlignment = VerticalAlignment.Center };
            unit.Classes.Add("hint");

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(label);
            row.Children.Add(box);
            row.Children.Add(unit);
            ParametersPanel.Children.Add(row);
        }
    }

    private void UpdateView()
    {
        try
        {
            if (_analyzer is not IAnalyzerView view)
            {
                StatusText.Text = _analyzer.LastUpdated == default
                    ? "Ожидание данных..."
                    : $"Обновлено: {_analyzer.LastUpdated.ToLocalTime():HH:mm:ss}";
                PlotBorder.IsVisible = false;
                return;
            }

            UpdateReadouts(view.Readouts);

            var x = view.X;
            var y = view.Y;

            if (x.Length > 1 && y.Length > 1)
            {
                var count = Math.Min(x.Length, y.Length);
                var plot = PlotView.Plot;
                plot.Clear();

                var signal = x.Length == y.Length
                    ? plot.Add.SignalXY(x, y, PlotTheme.Accent)
                    : plot.Add.SignalXY(
                        x.AsSpan(0, count).ToArray(),
                        y.AsSpan(0, count).ToArray(),
                        PlotTheme.Accent);

                signal.LineWidth = 1.4f;
                plot.Axes.AutoScale();
                plot.XLabel(view.XLabel);
                plot.YLabel(view.YLabel);
                PlotBorder.IsVisible = true;
                PlotView.Refresh();
            }
            else
            {
                PlotBorder.IsVisible = false;
            }

            StatusText.Text = _analyzer.LastUpdated == default
                ? "Ожидание данных..."
                : $"Обновлено: {_analyzer.LastUpdated.ToLocalTime():HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Анализатор {_analyzer.Title}: {ex.Message}");
        }
    }

    private void UpdateReadouts(IReadOnlyList<AnalyzerReadout> readouts)
    {
        var key = string.Join("|", readouts.Select(readout => readout.Name));

        if (key != _readoutKey)
        {
            _readoutKey = key;
            ReadoutsPanel.Children.Clear();

            foreach (var readout in readouts)
            {
                var panel = new StackPanel { Spacing = 2 };
                var label = new TextBlock { Text = readout.Name.ToUpperInvariant() };
                label.Classes.Add("readout-label");
                var value = new TextBlock { Text = "—" };
                value.Classes.Add("readout-value");
                value.FontSize = 20;
                var unit = new TextBlock { Text = readout.Units };
                unit.Classes.Add("readout-unit");

                panel.Children.Add(label);
                panel.Children.Add(value);
                panel.Children.Add(unit);

                var border = new Border { Child = panel, MinWidth = 130 };
                border.Classes.Add("readout");
                ReadoutsPanel.Children.Add(border);
            }
        }

        for (var i = 0; i < readouts.Count && i < ReadoutsPanel.Children.Count; i++)
        {
            if (ReadoutsPanel.Children[i] is Border { Child: StackPanel panel }
                && panel.Children.Count > 1
                && panel.Children[1] is TextBlock value)
            {
                var numeric = readouts[i].Value;
                value.Text = double.IsFinite(numeric)
                    ? numeric.ToString("0.###", CultureInfo.CurrentCulture)
                    : "—";
            }
        }
    }
}
