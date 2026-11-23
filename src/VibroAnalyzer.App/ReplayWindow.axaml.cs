using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Playback;
using VibroAnalyzer.Core.Routing;

namespace VibroAnalyzer.App;

/// <summary>
/// Окно просмотра сохранённой записи: транспорт, перемотка,
/// список шин и окна анализаторов.
/// </summary>
public partial class ReplayWindow : Window
{
    private static readonly (double Speed, string Title)[] Speeds =
    {
        (0.25, "0.25×"),
        (0.5, "0.5×"),
        (1.0, "1×"),
        (2.0, "2×"),
        (4.0, "4×"),
    };

    private readonly PlaybackSession _session;
    private readonly DispatcherTimer _timer;
    private bool _updatingPosition;

    public ReplayWindow(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        InitializeComponent();

        BoundText.Text = $"Запись: {session.Name} · {session.Directory}";
        SpeedCombo.ItemsSource = Speeds.Select(speed => speed.Title).ToArray();
        SpeedCombo.SelectedIndex = 2;

        SpeedCombo.SelectionChanged += (_, _) =>
        {
            if (SpeedCombo.SelectedIndex is >= 0 and < 5)
            {
                _session.Speed = Speeds[SpeedCombo.SelectedIndex].Speed;
            }
        };

        PlayPauseButton.Click += (_, _) => TogglePlayback();
        StopButton.Click += (_, _) =>
        {
            _session.Stop();
            UpdateTransport();
        };

        PositionSlider.ValueChanged += (_, _) =>
        {
            if (_updatingPosition)
            {
                return;
            }

            _session.Seek(TimeSpan.FromSeconds(PositionSlider.Value));
            UpdateTransport();
        };

        BuildBusRows();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdateTransport();

        Opened += (_, _) =>
        {
            _timer.Start();
            UpdateTransport();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _session.Dispose();
        };
    }

    private void TogglePlayback()
    {
        if (_session.IsPlaying)
        {
            _session.Pause();
        }
        else
        {
            if (_session.Duration > TimeSpan.Zero && _session.Position >= _session.Duration)
            {
                _session.Seek(TimeSpan.Zero);
            }

            _session.Play();
        }

        UpdateTransport();
    }

    private void UpdateTransport()
    {
        _updatingPosition = true;
        try
        {
            var duration = Math.Max(0.001, _session.Duration.TotalSeconds);
            PositionSlider.Maximum = duration;
            PositionSlider.Value = Math.Clamp(_session.Position.TotalSeconds, 0, duration);
            TimeText.Text = $"{FormatTime(_session.Position)} / {FormatTime(_session.Duration)}";
            PlayPauseButton.Content = _session.IsPlaying ? "Пауза" : "Играть";
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }

    private void BuildBusRows()
    {
        BusesPanel.Children.Clear();

        foreach (var bus in _session.Buses)
        {
            var scope = SmallButton("Осциллограф", (_, _) => OpenScope(bus));
            var spectrum = SmallButton("Спектр", (_, _) => OpenSpectrum(bus));
            var analyzers = SmallButton("Анализаторы", (_, _) => OpenCatalog(bus));

            var row = CreateRow(bus.Name, $"{bus.Kind} · {bus.SampleRate} Гц", scope, spectrum, analyzers);
            BusesPanel.Children.Add(row);
        }

        if (_session.Buses.Count > 0)
        {
            var overlay = new Button { Content = "Наложение всех шин", Margin = new Thickness(0, 8, 0, 0) };
            overlay.Classes.Add("small");
            overlay.Click += (_, _) =>
            {
                var buses = _session.Buses.Cast<ISignalBus>().ToList();
                new OverlayWindow(buses).Show();
            };
            BusesPanel.Children.Add(overlay);
        }
    }

    private void OpenScope(PlaybackBus bus)
    {
        var analyzer = new OscilloscopeAnalyzer();
        bus.AddAnalyzer(analyzer);
        var window = new ScopeWindow(analyzer, bus.Name);
        window.Closed += (_, _) => bus.RemoveAnalyzer(analyzer);
        window.Show();
    }

    private void OpenSpectrum(PlaybackBus bus)
    {
        var analyzer = new SpectrumAnalyzer();
        bus.AddAnalyzer(analyzer);
        var window = new SpectrumWindow(analyzer, bus.Name);
        window.Closed += (_, _) => bus.RemoveAnalyzer(analyzer);
        window.Show();
    }

    private void OpenCatalog(PlaybackBus bus)
    {
        new AnalyzerCatalogWindow(bus).Show();
    }

    private static Button SmallButton(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> handler)
    {
        var button = new Button { Content = text };
        button.Classes.Add("small");
        button.Click += handler;
        return button;
    }

    private static Border CreateRow(string title, string subtitle, params Control[] actions)
    {
        var textPanel = new StackPanel { Spacing = 2 };

        var titleBlock = new TextBlock { Text = title };
        titleBlock.Classes.Add("route-title");
        var subtitleBlock = new TextBlock { Text = subtitle };
        subtitleBlock.Classes.Add("route-subtitle");

        textPanel.Children.Add(titleBlock);
        textPanel.Children.Add(subtitleBlock);

        var actionPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            ItemSpacing = 6,
            Margin = new Thickness(10, 0, 0, 0),
        };

        foreach (var action in actions)
        {
            actionPanel.Children.Add(action);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(textPanel, 0);
        Grid.SetColumn(actionPanel, 1);
        grid.Children.Add(textPanel);
        grid.Children.Add(actionPanel);

        var border = new Border { Child = grid };
        border.Classes.Add("route");
        return border;
    }
}
