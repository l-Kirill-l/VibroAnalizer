using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Modulation;

namespace VibroAnalyzer.App;

/// <summary>Результат применения настроек.</summary>
public sealed record SettingsResult(bool DemoEnabled, DemoSignalConfig DemoConfig);

/// <summary>
/// Настройки приложения: демо-режим и конструктор частот/колебаний.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly (CarrierWaveform Waveform, string Title)[] Waveforms =
    {
        (CarrierWaveform.Sine, "Синус"),
        (CarrierWaveform.Square, "Прямоугольник"),
        (CarrierWaveform.Triangle, "Треугольник"),
    };

    private readonly List<ToneRow> _rows = new();

    public SettingsWindow(bool demoEnabled, DemoSignalConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        InitializeComponent();

        DemoModeCheck.IsChecked = demoEnabled;
        NoiseBox.Value = (decimal)Math.Clamp(config.NoiseLevel, 0.0, 0.2);

        foreach (var tone in config.Tones)
        {
            AddRow(tone);
        }

        if (_rows.Count == 0)
        {
            AddRow(new DemoTone());
        }

        AddToneButton.Click += (_, _) => AddRow(new DemoTone());
        ApplyButton.Click += OnApply;
        CancelButton.Click += (_, _) => Close(null);
    }

    private void AddRow(DemoTone tone)
    {
        var frequency = new NumericUpDown
        {
            Width = 120,
            Minimum = 1m,
            Maximum = 24000m,
            Increment = 1m,
            FormatString = "0",
            ClipValueToMinMax = true,
            Value = (decimal)Math.Clamp(tone.FrequencyHz, 1.0, 24000.0),
        };

        var amplitude = new NumericUpDown
        {
            Width = 100,
            Minimum = 0m,
            Maximum = 1m,
            Increment = 0.01m,
            FormatString = "0.00",
            ClipValueToMinMax = true,
            Value = (decimal)Math.Clamp(tone.Amplitude, 0.0, 1.0),
        };

        var waveform = new ComboBox
        {
            Width = 150,
            ItemsSource = Waveforms.Select(item => item.Title).ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(Waveforms, item => item.Waveform == tone.Waveform)),
        };

        var channel = new NumericUpDown
        {
            Width = 90,
            Minimum = -1m,
            Maximum = 32m,
            Increment = 1m,
            FormatString = "0",
            ClipValueToMinMax = true,
            Value = Math.Clamp(tone.Channel, -1, 32),
        };

        var remove = new Button { Content = "Удалить", MinWidth = 90 };
        remove.Classes.Add("small");
        remove.Classes.Add("danger");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,100,150,90,Auto") };
        Grid.SetColumn(frequency, 0);
        Grid.SetColumn(amplitude, 1);
        Grid.SetColumn(waveform, 2);
        Grid.SetColumn(channel, 3);
        Grid.SetColumn(remove, 4);
        frequency.Margin = new Thickness(0, 0, 8, 0);
        amplitude.Margin = new Thickness(0, 0, 8, 0);
        waveform.Margin = new Thickness(0, 0, 8, 0);
        channel.Margin = new Thickness(0, 0, 8, 0);

        grid.Children.Add(frequency);
        grid.Children.Add(amplitude);
        grid.Children.Add(waveform);
        grid.Children.Add(channel);
        grid.Children.Add(remove);

        var border = new Border { Child = grid };
        border.Classes.Add("route");

        var row = new ToneRow(border, frequency, amplitude, waveform, channel);
        remove.Click += (_, _) =>
        {
            if (_rows.Count <= 1)
            {
                return;
            }

            _rows.Remove(row);
            TonesPanel.Children.Remove(border);
        };

        _rows.Add(row);
        TonesPanel.Children.Add(border);
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var tones = new List<DemoTone>();

        foreach (var row in _rows)
        {
            var frequency = row.Frequency.Value is decimal frequencyValue ? (double)frequencyValue : 1000.0;
            var amplitude = row.Amplitude.Value is decimal amplitudeValue ? (double)amplitudeValue : 0.0;
            var channel = row.Channel.Value is decimal channelValue ? (int)channelValue : -1;
            var waveformIndex = row.Waveform.SelectedIndex;
            var waveform = waveformIndex >= 0 && waveformIndex < Waveforms.Length
                ? Waveforms[waveformIndex].Waveform
                : CarrierWaveform.Sine;

            if (!double.IsFinite(frequency) || frequency <= 0.0)
            {
                ErrorText.Text = "Частота должна быть больше нуля.";
                return;
            }

            tones.Add(new DemoTone
            {
                FrequencyHz = frequency,
                Amplitude = Math.Clamp(amplitude, 0.0, 1.0),
                Waveform = waveform,
                Channel = Math.Clamp(channel, -1, 32),
            });
        }

        var noise = NoiseBox.Value is decimal noiseValue ? (double)noiseValue : 0.0;
        var config = new DemoSignalConfig
        {
            Tones = tones,
            NoiseLevel = Math.Clamp(noise, 0.0, 0.2),
            LoopbackLevel = 0.0,
        };

        Close(new SettingsResult(DemoModeCheck.IsChecked == true, config));
    }

    private sealed record ToneRow(
        Border Container,
        NumericUpDown Frequency,
        NumericUpDown Amplitude,
        ComboBox Waveform,
        NumericUpDown Channel);
}
