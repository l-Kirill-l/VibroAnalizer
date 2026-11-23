using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using VibroAnalyzer.Core.Routing;

namespace VibroAnalyzer.App;

/// <summary>Результат диалога микшера.</summary>
public sealed record MixerSelection(FusionMode Mode, IReadOnlyList<FusionSource> Sources);

/// <summary>
/// Диалог создания виртуального входа: выбор конкретных шин,
/// усиления и полосы каждого источника.
/// </summary>
public partial class MixerWindow : Window
{
    private static readonly (FusionMode Mode, string Title)[] Modes =
    {
        (FusionMode.Average, "Среднее"),
        (FusionMode.Weighted, "Взвешенное среднее"),
        (FusionMode.Sum, "Сумма"),
        (FusionMode.Difference, "Разность"),
        (FusionMode.Minimum, "Минимум"),
        (FusionMode.Maximum, "Максимум"),
        (FusionMode.BandMerge, "Сумма полос"),
    };

    private static readonly (FusionBand Band, string Title)[] Bands =
    {
        (FusionBand.Full, "Полный спектр"),
        (FusionBand.LowPass, "НЧ (ФНЧ)"),
        (FusionBand.HighPass, "ВЧ (ФВЧ)"),
    };

    private readonly List<SourceRow> _rows = new();

    public MixerWindow(IReadOnlyList<ISignalBus> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        InitializeComponent();

        ModeCombo.ItemsSource = Modes.Select(mode => mode.Title).ToArray();
        ModeCombo.SelectedIndex = 0;

        foreach (var source in sources)
        {
            var row = CreateRow(source);
            _rows.Add(row);
            SourcesPanel.Children.Add(row.Container);
        }

        if (_rows.Count == 0)
        {
            ErrorText.Text = "Нет доступных входов.";
            CreateButton.IsEnabled = false;
        }

        CreateButton.Click += OnCreate;
        CancelButton.Click += (_, _) => Close(null);
    }

    private SourceRow CreateRow(ISignalBus bus)
    {
        var enabled = new CheckBox
        {
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock { Text = bus.Name };
        name.Classes.Add("route-title");

        var kind = bus.IsVirtual ? "виртуальный вход" : "физический вход";
        var subtitle = new TextBlock { Text = $"{kind} · {bus.SampleRate} Гц" };
        subtitle.Classes.Add("route-subtitle");

        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(name);
        textPanel.Children.Add(subtitle);

        var gainBox = new NumericUpDown
        {
            Width = 90,
            Minimum = 0.01m,
            Maximum = 10m,
            Increment = 0.1m,
            FormatString = "0.00",
            Value = 1m,
            ClipValueToMinMax = true,
        };

        var bandCombo = new ComboBox
        {
            Width = 150,
            ItemsSource = Bands.Select(band => band.Title).ToArray(),
            SelectedIndex = 0,
        };

        var cutoffBox = new NumericUpDown
        {
            Width = 100,
            Minimum = 10m,
            Maximum = 20000m,
            Increment = 10m,
            FormatString = "0",
            Value = 1000m,
            ClipValueToMinMax = true,
            IsEnabled = false,
        };

        bandCombo.SelectionChanged += (_, _) =>
        {
            cutoffBox.IsEnabled = bandCombo.SelectedIndex > 0;
        };

        var controls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            ItemSpacing = 6,
        };

        var gainLabel = new TextBlock { Text = "×", Classes = { "label" } };
        var cutoffLabel = new TextBlock { Text = "срез", Classes = { "label" } };
        var hzLabel = new TextBlock { Text = "Гц", Classes = { "label" } };

        controls.Children.Add(gainLabel);
        controls.Children.Add(gainBox);
        controls.Children.Add(bandCombo);
        controls.Children.Add(cutoffBox);
        controls.Children.Add(hzLabel);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(enabled, 0);
        Grid.SetColumn(textPanel, 1);
        Grid.SetColumn(controls, 2);
        textPanel.Margin = new Thickness(8, 0, 12, 0);
        grid.Children.Add(enabled);
        grid.Children.Add(textPanel);
        grid.Children.Add(controls);

        var border = new Border { Child = grid };
        border.Classes.Add("route");

        return new SourceRow(bus, border, enabled, gainBox, bandCombo, cutoffBox);
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(row => row.Enabled.IsChecked == true).ToList();

        if (selected.Count == 0)
        {
            ErrorText.Text = "Отметьте хотя бы один источник.";
            return;
        }

        var modeIndex = ModeCombo.SelectedIndex;
        var mode = modeIndex >= 0 && modeIndex < Modes.Length
            ? Modes[modeIndex].Mode
            : FusionMode.Average;

        if (mode == FusionMode.Difference && selected.Count < 2)
        {
            ErrorText.Text = "Для разности нужно выбрать не меньше двух источников.";
            return;
        }

        var sources = selected.Select(row =>
        {
            var bandIndex = row.BandCombo.SelectedIndex;
            var band = bandIndex >= 0 && bandIndex < Bands.Length
                ? Bands[bandIndex].Band
                : FusionBand.Full;
            var cutoff = row.CutoffBox.Value is decimal value ? (double)value : 1000.0;
            var gain = row.GainBox.Value is decimal gainValue ? (double)gainValue : 1.0;

            return new FusionSource(row.Bus, gain, band, cutoff);
        }).ToList();

        Close(new MixerSelection(mode, sources));
    }

    private sealed record SourceRow(
        ISignalBus Bus,
        Border Container,
        CheckBox Enabled,
        NumericUpDown GainBox,
        ComboBox BandCombo,
        NumericUpDown CutoffBox);
}
