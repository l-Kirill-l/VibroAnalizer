using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Engine;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.App;

/// <summary>
/// Сканирует входные каналы всех выбранных карт и показывает массив
/// отреагировавших каналов. Пользователь выбирает нужный вручную.
/// </summary>
internal partial class FindSignalWindow : Window
{
    private const int ScanDurationMilliseconds = 1400;
    private const int ScanSampleRate = 48000;

    private readonly AudioEngine _engine;
    private readonly IReadOnlyList<CardOption> _cards;
    private readonly List<SignalHit> _hits = new();

    public FindSignalWindow(AudioEngine engine, IReadOnlyList<CardOption> cards)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _cards = cards ?? throw new ArgumentNullException(nameof(cards));

        InitializeComponent();

        var cardItems = new List<object> { "Все карты" };
        cardItems.AddRange(_cards.Where(card => card.Inputs.Count > 0));
        CardCombo.ItemsSource = cardItems;
        CardCombo.SelectedIndex = 0;
        CardCombo.SelectionChanged += OnCardChanged;
        PortCombo.SelectionChanged += OnPortChanged;

        ScanButton.Click += OnScan;
        SelectButton.Click += OnSelect;
        CloseButton.Click += (_, _) => Close();
        ResultsList.SelectionChanged += (_, _) =>
            SelectButton.IsEnabled = ResultsList.SelectedItem is not null;

        PopulatePorts();
    }

    private void OnCardChanged(object? sender, SelectionChangedEventArgs e)
    {
        PopulatePorts();
    }

    private void OnPortChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Фильтр порта применяется при следующем сканировании.
    }

    private void PopulatePorts()
    {
        var card = CardCombo.SelectedItem as CardOption;
        var items = new List<object> { "Все входы" };

        if (card is not null)
        {
            items.AddRange(card.Inputs);
        }
        else
        {
            items.AddRange(_cards.SelectMany(item => item.Inputs));
        }

        PortCombo.ItemsSource = items;
        PortCombo.SelectedIndex = 0;
    }

    private async void OnScan(object? sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            StatusText.Text = "Остановите измерение перед поиском сигнала.";
            return;
        }

        var ports = ResolvePorts();
        if (ports.Count == 0)
        {
            StatusText.Text = "Нет входных портов для сканирования.";
            return;
        }

        var threshold = ThresholdBox.Value is decimal value ? (double)value : -60;
        ScanButton.IsEnabled = false;
        ResultsList.ItemsSource = null;
        SelectButton.IsEnabled = false;
        _hits.Clear();

        try
        {
            var levels = new Dictionary<(string Stream, int Channel), double>();

            foreach (var group in ports.GroupBy(port => port.Device.EffectiveStreamId))
            {
                var channels = Math.Max(1, group.Max(port => port.ChannelCount));
                StatusText.Text = $"Сканирование {group.Key} · {channels} кан...";

                try
                {
                    var measured = await MeasureStreamAsync(group.Key, channels);
                    foreach (var pair in measured)
                    {
                        levels[pair.Key] = pair.Value;
                    }
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Ошибка сканирования {group.Key}: {ex.Message}";
                }
            }

            foreach (var streamGroup in ports.GroupBy(port => port.Device.EffectiveStreamId))
            {
                var streamPorts = streamGroup.ToList();
                var maxChannels = streamPorts.Max(port => port.ChannelCount);

                for (var channelIndex = 0; channelIndex < maxChannels; channelIndex++)
                {
                    if (!levels.TryGetValue((streamGroup.Key, channelIndex), out var rms))
                    {
                        continue;
                    }

                    var matchingPorts = streamPorts
                        .Where(port => port.Channels.Any(channel => channel.Index == channelIndex))
                        .ToList();
                    if (matchingPorts.Count == 0)
                    {
                        continue;
                    }

                    // Один поток может предлагать несколько портов, но активен
                    // только один физический порт. Остальные — альтернативные.
                    var port = matchingPorts.FirstOrDefault(IsActivePort)
                               ?? matchingPorts[0];
                    var channel = port.Channels.First(item => item.Index == channelIndex);
                    var card = _cards.FirstOrDefault(item =>
                        item.Inputs.Contains(port) || item.Outputs.Contains(port));
                    if (card is null)
                    {
                        continue;
                    }

                    var db = rms > 1e-12 ? 20 * Math.Log10(rms) : -120;
                    if (db < threshold)
                    {
                        continue;
                    }

                    var usedBy = string.Join(
                        ", ",
                        _engine.Inputs
                            .Where(bus => bus.IsConnected
                                          && bus.DeviceId == channel.Device.Id
                                          && bus.PhysicalChannel == channel.Index)
                            .Select(bus => bus.Name));

                    _hits.Add(new SignalHit(
                        card,
                        port,
                        channel,
                        db,
                        string.IsNullOrWhiteSpace(usedBy) ? "—" : usedBy));
                }
            }

            _hits.Sort((left, right) => right.LevelDb.CompareTo(left.LevelDb));

            ResultsList.ItemsSource = _hits
                .Select(hit =>
                    $"{hit.Card.Name} · {hit.Port.Name} · канал {hit.Channel.Index + 1} · "
                    + $"{hit.LevelDb:0.0} dB · {hit.UsedBy}")
                .ToList();

            StatusText.Text = _hits.Count == 0
                ? $"Сигналов выше {threshold:0} dBFS не найдено."
                : $"Найдено каналов: {_hits.Count}. Выберите нужный.";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private static bool IsActivePort(PortOption port)
    {
        var active = port.Device.ActivePortId;
        if (string.IsNullOrWhiteSpace(active))
        {
            return false;
        }

        var activeIds = active.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var portIds = port.Port.Id.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return activeIds.Intersect(portIds, StringComparer.Ordinal).Any();
    }

    private List<PortOption> ResolvePorts()
    {
        var selectedPort = PortCombo.SelectedItem as PortOption;
        if (selectedPort is not null)
        {
            return new List<PortOption> { selectedPort };
        }

        var card = CardCombo.SelectedItem as CardOption;
        return card is not null
            ? card.Inputs.ToList()
            : _cards.SelectMany(item => item.Inputs).ToList();
    }

    private async Task<Dictionary<(string Stream, int Channel), double>> MeasureStreamAsync(
        string streamId,
        int channels)
    {
        var result = new Dictionary<(string Stream, int Channel), double>();
        var sums = new double[channels];
        var frames = 0L;
        var sync = new object();

        using var stream = _engine.Backend.OpenStream(new AudioStreamOptions(
            streamId,
            null,
            ScanSampleRate,
            channels,
            0,
            1024));

        stream.InputAvailable += (_, block) =>
        {
            lock (sync)
            {
                frames += block.FrameCount;
                for (var channel = 0; channel < Math.Min(channels, block.Channels); channel++)
                {
                    var sum = 0.0;
                    for (var frame = 0; frame < block.FrameCount; frame++)
                    {
                        var sample = block.Samples[frame * block.Channels + channel];
                        sum += sample * sample;
                    }

                    sums[channel] += sum;
                }
            }
        };

        stream.Start();
        await Task.Delay(ScanDurationMilliseconds);
        stream.Stop();

        lock (sync)
        {
            if (frames <= 0)
            {
                return result;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                result[(streamId, channel)] = Math.Sqrt(sums[channel] / frames);
            }
        }

        return result;
    }

    private void OnSelect(object? sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedIndex < 0
            || ResultsList.SelectedIndex >= _hits.Count)
        {
            return;
        }

        var hit = _hits[ResultsList.SelectedIndex];
        Close(new SignalSelection(hit.Card, hit.Port, hit.Channel));
    }

    private sealed record SignalHit(
        CardOption Card,
        PortOption Port,
        ChannelOption Channel,
        double LevelDb,
        string UsedBy);
}
