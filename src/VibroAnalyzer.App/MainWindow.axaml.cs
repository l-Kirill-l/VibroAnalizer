using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VibroAnalyzer.Audio;
using VibroAnalyzer.Audio.Simulated;
using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Audio;
using VibroAnalyzer.Core.Devices;
using VibroAnalyzer.Core.Engine;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Playback;
using VibroAnalyzer.Core.Routing;
using VibroAnalyzer.Core.Session;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.App;

/// <summary>
/// Главное окно: маршрутизация входов и выходов, запуск измерения
/// и открытие окон, привязанных к конкретным шинам.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly int[] SupportedRates = { 44100, 48000, 96000, 192000 };
    private static readonly int[] SupportedBuffers = { 128, 256, 512, 1024, 2048, 4096 };

    private static readonly (FusionMode Mode, string Title)[] MixModes =
    {
        (FusionMode.Average, "Среднее"),
        (FusionMode.Weighted, "Взвешенное среднее"),
        (FusionMode.Sum, "Сумма"),
        (FusionMode.Difference, "Разность"),
        (FusionMode.Minimum, "Минимум"),
        (FusionMode.Maximum, "Максимум"),
        (FusionMode.BandMerge, "Сумма полос"),
    };

    private readonly AudioEngine _engine;
    private readonly SessionContext _session;
    private readonly DispatcherTimer _readoutTimer;
    private readonly List<(Window Window, object Bus)> _busWindows = new();
    private readonly List<Window> _overlayWindows = new();
    private readonly List<(ChannelOption Channel, TextBlock Level)> _levelCells = new();
    private IReadOnlyList<CardOption> _cards = Array.Empty<CardOption>();
    private Guid? _connectionEditorBusId;
    private bool _connectionEditorIsInput;

    private OscilloscopeAnalyzer? _summaryScope;
    private SpectrumAnalyzer? _summarySpectrum;
    private ISignalBus? _summaryBus;
    private ISignalBus? _recordingBus;
    private DemoSignalConfig _demoConfig = DemoSignalConfig.CreateDefault();

    private bool _ready;
    private bool _loading;
    private bool _closed;

    public MainWindow(AudioEngine engine, SessionContext session)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        InitializeComponent();

        _readoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _readoutTimer.Tick += (_, _) =>
        {
            UpdateReadout();
            UpdateInspectorLevels();
        };

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// Открывает указанные измерительные окна для первой шины. Список через запятую:
    /// scope, spectrum, modulation, demodulation.
    /// </summary>
    public void OpenAnalyzerWindows(string specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
        {
            return;
        }

        ISignalBus? firstInput = _engine.Inputs.FirstOrDefault();
        firstInput ??= _engine.VirtualInputs.FirstOrDefault();
        var firstOutput = _engine.Outputs.FirstOrDefault();

        foreach (var name in specification.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (name.ToLowerInvariant())
            {
                case "scope":
                case "oscilloscope":
                case "осциллограф":
                    if (firstInput is not null)
                    {
                        OpenScope(firstInput);
                    }

                    break;

                case "spectrum":
                case "спектр":
                    if (firstInput is not null)
                    {
                        OpenSpectrum(firstInput);
                    }

                    break;

                case "modulation":
                case "модуляция":
                    if (firstOutput is not null)
                    {
                        OpenModulation(firstOutput);
                    }

                    break;

                case "demodulation":
                case "демодуляция":
                    if (firstInput is not null)
                    {
                        OpenDemodulation(firstInput);
                    }

                    break;

                case "overlay":
                case "наложение":
                    OpenOverlay();
                    break;

                case "analyzers":
                case "catalog":
                case "каталог":
                    if (firstInput is not null)
                    {
                        OpenAnalyzerCatalog(firstInput);
                    }

                    break;
            }
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            return;
        }

        _ready = true;

        BackendCombo.ItemsSource = AudioBackendFactory.AvailableBackendNames;
        BackendCombo.PlaceholderText = "Бэкенд";
        RateCombo.ItemsSource = SupportedRates;
        BufferCombo.ItemsSource = SupportedBuffers;
        MixModeCombo.ItemsSource = MixModes.Select(mode => mode.Title).ToArray();

        BackendCombo.SelectionChanged += OnBackendChanged;
        RateCombo.SelectionChanged += OnRateChanged;
        BufferCombo.SelectionChanged += OnBufferChanged;
        InspectorCardCombo.SelectionChanged += OnInspectorCardChanged;
        InspectorPortCombo.SelectionChanged += OnInspectorPortChanged;
        RefreshButton.Click += OnRefreshDevices;
        SettingsButton.Click += OnOpenSettings;
        StartButton.Click += OnStart;
        StopButton.Click += OnStop;
        SaveButton.Click += OnSave;
        AddInputButton.Click += OnAddInput;
        AddOutputButton.Click += OnAddOutput;
        AddMixerButton.Click += OnAddMixer;
        AddBandMergeButton.Click += OnAddBandMerge;
        ConfiguredMixerButton.Click += OnAddConfiguredMixer;
        OverlayButton.Click += OnOpenOverlay;
        RecordSessionButton.Click += OnToggleSessionRecording;
        OpenRecordingButton.Click += OnOpenRecording;

        _engine.StatusChanged += OnEngineStatus;
        _engine.ErrorOccurred += OnEngineError;
        _engine.RoutesChanged += OnRoutesChanged;

        BindStaticValues();
        RefreshTopology();
        EnsureDefaultSensor();
        RefreshRoutes();
        UpdateSessionInfo();
        UpdateButtons();
        UpdateReadout();

        _readoutTimer.Start();
    }

    private void OnClosing(object? sender, EventArgs e)
    {
        _closed = true;
        _readoutTimer.Stop();

        _engine.StatusChanged -= OnEngineStatus;
        _engine.ErrorOccurred -= OnEngineError;
        _engine.RoutesChanged -= OnRoutesChanged;

        CloseAllBusWindows();
        _engine.Stop();
    }

    /// <summary>
    /// Создаёт демонстрационную маршрутизацию на демо-бэкенде:
    /// два входа, два выхода и виртуальный вход «среднее».
    /// </summary>
    public void ConfigureDemoRouting()
    {
        try
        {
            CloseAllBusWindows();
            _connectionEditorBusId = null;

            if (_engine.IsRunning)
            {
                _engine.Stop();
            }

            _recordingBus = null;

            foreach (var virtualInput in _engine.VirtualInputs.ToList())
            {
                _engine.RemoveVirtualInput(virtualInput);
            }

            foreach (var input in _engine.Inputs.ToList())
            {
                _engine.RemoveInput(input);
            }

            foreach (var output in _engine.Outputs.ToList())
            {
                _engine.RemoveOutput(output);
            }

            var inputDevice = _engine.Devices.FirstOrDefault(d => d.Id == "sim-input")
                              ?? _engine.Devices.FirstOrDefault(d => d.IsInput && d.InputChannels >= 2)
                              ?? _engine.Devices.FirstOrDefault(d => d.IsInput);

            if (inputDevice is not null)
            {
                _engine.AddInput(inputDevice, 0, "Датчик 1 · 1 кГц");

                if (inputDevice.InputChannels >= 2)
                {
                    _engine.AddInput(inputDevice, 1, "Датчик 2 · 100 Гц");
                }
            }

            var outputDevice = _engine.Devices.FirstOrDefault(d => d.Id == "sim-output")
                               ?? _engine.Devices.FirstOrDefault(d => d.IsOutput && d.OutputChannels >= 2)
                               ?? _engine.Devices.FirstOrDefault(d => d.IsOutput);

            if (outputDevice is not null)
            {
                var left = _engine.AddOutput(outputDevice, 0, "Левый · 1 кГц");
                left.Modulation.Carrier.FrequencyHz = 1000;
                left.Modulation.Carrier.Amplitude = 0.2;

                if (outputDevice.OutputChannels >= 2)
                {
                    var right = _engine.AddOutput(outputDevice, 1, "Правый · 3 кГц");
                    right.Modulation.Carrier.FrequencyHz = 3000;
                    right.Modulation.Carrier.Amplitude = 0.2;
                }
            }

            if (_engine.Inputs.Count >= 2)
            {
                var sources = _engine.Inputs
                    .Select(input => new FusionSource(input))
                    .ToList();

                _engine.AddVirtualInput("Среднее датчиков", sources, FusionMode.Average);
            }

            RefreshRoutes();
            ShowStatus("Демо-маршрутизация: два входа, два выхода, среднее датчиков.");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось создать демо-маршрутизацию: {ex.Message}");
        }
    }

    private bool IsDemoBackend => _engine.Backend is SimulatedAudioBackend;

    private void BindStaticValues()
    {
        _loading = true;
        try
        {
            BackendCombo.SelectedItem = IsDemoBackend ? null : _engine.Backend.Name;
            RateCombo.SelectedItem = SupportedRates.Contains(_engine.SampleRate)
                ? _engine.SampleRate
                : 48000;
            BufferCombo.SelectedItem = SupportedBuffers.Contains(_engine.BufferFrames)
                ? _engine.BufferFrames
                : 1024;
            MixModeCombo.SelectedIndex = 0;
            DemoBadge.IsVisible = IsDemoBackend;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshTopology()
    {
        var previousCardId = (InspectorCardCombo.SelectedItem as CardOption)?.Id;
        var previousPortKey = (InspectorPortCombo.SelectedItem as PortOption)?.Key;

        _cards = AudioTopologyView.Build(_engine.Devices);

        _loading = true;
        try
        {
            InspectorCardCombo.ItemsSource = _cards;
            InspectorCardCombo.SelectedItem = _cards.FirstOrDefault(card => card.Id == previousCardId)
                                               ?? _cards.FirstOrDefault();
            PopulateInspectorPorts(previousPortKey);
        }
        finally
        {
            _loading = false;
        }

        RefreshInspector();
    }

    private void PopulateInspectorPorts(string? preferredKey = null)
    {
        var card = InspectorCardCombo.SelectedItem as CardOption;
        var ports = card is null
            ? Array.Empty<PortOption>()
            : card.Inputs.Concat(card.Outputs).ToArray();

        InspectorPortCombo.ItemsSource = ports;
        InspectorPortCombo.SelectedItem = ports.FirstOrDefault(port => port.Key == preferredKey)
                                          ?? ports.FirstOrDefault();
    }

    private void EnsureDefaultSensor()
    {
        if (_engine.Inputs.Count > 0 || _engine.VirtualInputs.Count > 0)
        {
            return;
        }

        try
        {
            _engine.AddInput("Датчик 1");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось создать датчик по умолчанию: {ex.Message}");
        }
    }

    private void OnRoutesChanged(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        Dispatcher.UIThread.Post(RefreshRoutes);
    }

    private void RefreshRoutes()
    {
        if (!_ready || _closed)
        {
            return;
        }

        EnsureSummaryAnalyzers();

        InputsPanel.Children.Clear();
        OutputsPanel.Children.Clear();
        VirtualPanel.Children.Clear();

        foreach (var input in _engine.Inputs)
        {
            InputsPanel.Children.Add(BuildInputRow(input));
        }

        if (_engine.Inputs.Count == 0)
        {
            InputsPanel.Children.Add(BuildEmptyHint("Входы ещё не добавлены."));
        }

        foreach (var output in _engine.Outputs)
        {
            OutputsPanel.Children.Add(BuildOutputRow(output));
        }

        if (_engine.Outputs.Count == 0)
        {
            OutputsPanel.Children.Add(BuildEmptyHint("Выходы ещё не добавлены."));
        }

        foreach (var virtualInput in _engine.VirtualInputs)
        {
            VirtualPanel.Children.Add(BuildVirtualRow(virtualInput));
        }

        if (_engine.VirtualInputs.Count == 0)
        {
            VirtualPanel.Children.Add(BuildEmptyHint("Микшеры ещё не созданы."));
        }

        InputsPanel.InvalidateMeasure();
        OutputsPanel.InvalidateMeasure();
        InputsPanel.InvalidateArrange();
        OutputsPanel.InvalidateArrange();

        RefreshInspector();
        UpdateButtons();
        UpdateRecordingUi();
        UpdateDemoBadge();
        UpdateReadout();
    }

    private static TextBlock BuildEmptyHint(string text)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add("hint");
        return block;
    }

    private Control BuildInputRow(InputBus bus)
    {
        var subtitle = $"{DescribeConnection(bus.DeviceId, bus.PhysicalChannel, isInput: true)} · "
                       + $"демодуляция: {DescribeDemodulation(bus.Demodulation.Kind)}";

        var connect = SmallButton("Подключение", (_, _) => OpenConnectionEditor(bus.Id, isInput: true));
        var scope = SmallButton("Осциллограф", (_, _) => OpenScope(bus));
        var spectrum = SmallButton("Спектр", (_, _) => OpenSpectrum(bus));
        var analyzers = SmallButton("Анализаторы", (_, _) => OpenAnalyzerCatalog(bus));
        var demodulation = SmallButton("Демодуляция", (_, _) => OpenDemodulation(bus));
        var record = CreateRecordButton(bus);
        var remove = SmallButton("Удалить", (_, _) => RemoveRoute(bus));
        remove.Classes.Add("danger");
        remove.IsEnabled = !_engine.IsRunning;

        var row = CreateRouteRow(bus.Name, subtitle, connect, scope, spectrum, analyzers, demodulation, record, remove);
        if (_connectionEditorBusId != bus.Id || !_connectionEditorIsInput)
        {
            return row;
        }

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(row);
        panel.Children.Add(BuildConnectionEditor(inputBus: bus, outputBus: null));
        return panel;
    }

    private Control BuildOutputRow(OutputBus bus)
    {
        var subtitle = $"{DescribeConnection(bus.DeviceId, bus.PhysicalChannel, isInput: false)} · "
                       + $"несущая {bus.Modulation.Carrier.FrequencyHz:0} Гц";

        var connect = SmallButton("Подключение", (_, _) => OpenConnectionEditor(bus.Id, isInput: false));
        var enabled = new CheckBox
        {
            Content = "Вкл",
            IsChecked = bus.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        enabled.IsCheckedChanged += (_, _) => bus.Enabled = enabled.IsChecked == true;

        var modulation = SmallButton("Модуляция", (_, _) => OpenModulation(bus));
        var scope = SmallButton("Осциллограф", (_, _) => OpenScope(bus));
        var spectrum = SmallButton("Спектр", (_, _) => OpenSpectrum(bus));
        var analyzers = SmallButton("Анализаторы", (_, _) => OpenAnalyzerCatalog(bus));
        var remove = SmallButton("Удалить", (_, _) => RemoveRoute(bus));
        remove.Classes.Add("danger");
        remove.IsEnabled = !_engine.IsRunning;

        var row = CreateRouteRow(bus.Name, subtitle, connect, enabled, modulation, scope, spectrum, analyzers, remove);
        if (_connectionEditorBusId != bus.Id || _connectionEditorIsInput)
        {
            return row;
        }

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(row);
        panel.Children.Add(BuildConnectionEditor(inputBus: null, outputBus: bus));
        return panel;
    }

    private Border BuildVirtualRow(VirtualInputBus bus)
    {
        var scope = SmallButton("Осциллограф", (_, _) => OpenScope(bus));
        var spectrum = SmallButton("Спектр", (_, _) => OpenSpectrum(bus));
        var analyzers = SmallButton("Анализаторы", (_, _) => OpenAnalyzerCatalog(bus));
        var demodulation = SmallButton("Демодуляция", (_, _) => OpenDemodulation(bus));
        var record = CreateRecordButton(bus);
        var remove = SmallButton("Удалить", (_, _) => RemoveRoute(bus));
        remove.Classes.Add("danger");
        remove.IsEnabled = !_engine.IsRunning;

        return CreateRouteRow(bus.Name, bus.Description, scope, spectrum, analyzers, demodulation, record, remove);
    }

    private static Border CreateRouteRow(string title, string subtitle, params Control[] actions)
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
            Margin = new Thickness(10, 0, 0, 0),
        };

        foreach (var action in actions)
        {
            action.Margin = new Thickness(0, 0, 6, 0);
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

    private static Button SmallButton(string text, EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button { Content = text };
        button.Classes.Add("small");
        button.Click += handler;
        return button;
    }

    private Button CreateRecordButton(ISignalBus bus)
    {
        var active = _engine.IsRecording && ReferenceEquals(_recordingBus, bus);
        var button = SmallButton(active ? "Стоп запись" : "Запись", (_, _) => ToggleBusRecording(bus));
        button.IsEnabled = _engine.IsRunning && (!_engine.IsRecording || active);

        if (active)
        {
            button.Classes.Add("danger");
        }

        return button;
    }

    private static string DescribeDemodulation(DemodulationKind kind) => kind switch
    {
        DemodulationKind.Envelope => "огибающая",
        DemodulationKind.Synchronous => "синхронная",
        _ => "выключена",
    };

    private void OnBackendChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || IsDemoBackend || BackendCombo.SelectedItem is not string backendName)
        {
            return;
        }

        try
        {
            CloseAllBusWindows();
            _engine.SetBackend(AudioBackendFactory.Create(backendName));
            _recordingBus = null;
            RefreshTopology();
            EnsureDefaultSensor();
            RefreshRoutes();
            ShowStatus($"Бэкенд переключён: {backendName}. Маршруты сброшены.");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось переключить бэкенд: {ex.Message}");
        }
    }

    private void OnRefreshDevices(object? sender, RoutedEventArgs e)
    {
        _engine.RefreshDevices();
        RefreshTopology();
        RefreshRoutes();
        ShowStatus("Список устройств обновлён.");
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        await OpenSettingsAsync();
    }

    /// <summary>Открывает окно настроек и применяет выбранный режим.</summary>
    public async Task OpenSettingsAsync()
    {
        var dialog = new SettingsWindow(IsDemoBackend, _demoConfig.Clone());
        var result = await dialog.ShowDialog<SettingsResult?>(this);

        if (result is null)
        {
            return;
        }

        _demoConfig = result.DemoConfig;
        ApplyDemoMode(result.DemoEnabled);
    }

    private void ApplyDemoMode(bool enabled)
    {
        try
        {
            CloseAllBusWindows();

            if (enabled)
            {
                _engine.SetBackend(new SimulatedAudioBackend(_demoConfig));
                RefreshTopology();
                ConfigureDemoRouting();
                ShowStatus("Демо-режим включён: используется сигнал конструктора.");
            }
            else
            {
                if (IsDemoBackend)
                {
                    _engine.SetBackend(AudioBackendFactory.Create());
                    RefreshTopology();
                    EnsureDefaultSensor();
                    ShowStatus("Демо-режим выключен: используются реальные устройства.");
                }
                else
                {
                    ShowStatus("Настройки сохранены.");
                }
            }

            _recordingBus = null;
            RefreshRoutes();
            UpdateButtons();
            UpdateDemoBadge();
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось применить настройки: {ex.Message}");
        }
    }

    private void UpdateDemoBadge()
    {
        DemoBadge.IsVisible = IsDemoBackend;
        BackendCombo.PlaceholderText = IsDemoBackend ? "Демо-режим" : "Бэкенд";

        _loading = true;
        try
        {
            var names = AudioBackendFactory.AvailableBackendNames;
            BackendCombo.SelectedItem = IsDemoBackend
                ? null
                : names.Contains(_engine.Backend.Name) ? _engine.Backend.Name : null;
        }
        finally
        {
            _loading = false;
        }

        BackendCombo.IsEnabled = !IsDemoBackend && !_engine.IsRunning;
    }

    private void OnRateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || RateCombo.SelectedItem is not int rate)
        {
            return;
        }

        _engine.SampleRate = rate;
        WarnIfRunning("частота дискретизации");
    }

    private void OnBufferChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || BufferCombo.SelectedItem is not int frames)
        {
            return;
        }

        _engine.BufferFrames = frames;
        WarnIfRunning("размер буфера");
    }

    private void OnAddInput(object? sender, RoutedEventArgs e)
    {
        try
        {
            var bus = _engine.AddInput();
            _connectionEditorBusId = bus.Id;
            _connectionEditorIsInput = true;
            RefreshRoutes();
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось добавить датчик: {ex.Message}");
        }
    }

    private void OnAddOutput(object? sender, RoutedEventArgs e)
    {
        try
        {
            var bus = _engine.AddOutput();
            _connectionEditorBusId = bus.Id;
            _connectionEditorIsInput = false;
            RefreshRoutes();
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось добавить выход: {ex.Message}");
        }
    }

    private void OnAddMixer(object? sender, RoutedEventArgs e)
    {
        var sources = _engine.Inputs
            .Select(input => new FusionSource(input))
            .ToList();

        if (sources.Count == 0)
        {
            ShowError("Сначала добавьте хотя бы один вход.");
            return;
        }

        var index = MixModeCombo.SelectedIndex;
        var mode = index >= 0 && index < MixModes.Length
            ? MixModes[index].Mode
            : FusionMode.Average;

        try
        {
            _engine.AddVirtualInput(null, sources, mode);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось создать микшер: {ex.Message}");
        }
    }

    private void OnAddBandMerge(object? sender, RoutedEventArgs e)
    {
        var inputs = _engine.Inputs;
        if (inputs.Count < 2)
        {
            ShowError("Для объединения полос нужно не меньше двух входов.");
            return;
        }

        var cutoff = MixCutoffBox.Value is decimal value ? (double)value : 1000.0;
        var sources = new[]
        {
            new FusionSource(inputs[0], Band: FusionBand.LowPass, CutoffHz: cutoff),
            new FusionSource(inputs[1], Band: FusionBand.HighPass, CutoffHz: cutoff),
        };

        try
        {
            _engine.AddVirtualInput(null, sources, FusionMode.BandMerge);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось объединить полосы: {ex.Message}");
        }
    }

    private async void OnAddConfiguredMixer(object? sender, RoutedEventArgs e)
    {
        var buses = _engine.Inputs
            .Cast<ISignalBus>()
            .Concat(_engine.VirtualInputs)
            .ToList();

        if (buses.Count == 0)
        {
            ShowError("Сначала добавьте хотя бы один вход.");
            return;
        }

        var dialog = new MixerWindow(buses);
        var selection = await dialog.ShowDialog<MixerSelection?>(this);

        if (selection is null)
        {
            return;
        }

        try
        {
            _engine.AddVirtualInput(null, selection.Sources, selection.Mode);
            ShowStatus("Виртуальный вход создан.");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось создать микшер: {ex.Message}");
        }
    }

    private void RemoveRoute(object bus)
    {
        try
        {
            CloseBusWindows(bus);

            switch (bus)
            {
                case InputBus input:
                    _engine.RemoveInput(input);
                    break;

                case VirtualInputBus virtualInput:
                    _engine.RemoveVirtualInput(virtualInput);
                    break;

                case OutputBus output:
                    _engine.RemoveOutput(output);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось удалить шину: {ex.Message}");
        }
    }

    private void OpenScope(ISignalBus bus)
    {
        var analyzer = new OscilloscopeAnalyzer();
        bus.AddAnalyzer(analyzer);

        var window = new ScopeWindow(analyzer, bus.Name);
        window.Closed += (_, _) => bus.RemoveAnalyzer(analyzer);
        RegisterWindow(window, bus);
    }

    private void OpenSpectrum(ISignalBus bus)
    {
        var analyzer = new SpectrumAnalyzer();
        bus.AddAnalyzer(analyzer);

        var window = new SpectrumWindow(analyzer, bus.Name);
        window.Closed += (_, _) => bus.RemoveAnalyzer(analyzer);
        RegisterWindow(window, bus);
    }

    private void OpenAnalyzerCatalog(ISignalBus bus)
    {
        var window = new AnalyzerCatalogWindow(bus);
        RegisterWindow(window, bus);
    }

    private void OpenDemodulation(ISignalBus bus)
    {
        var window = new DemodulationWindow(bus.Demodulation, bus.Name);
        RegisterWindow(window, bus);
    }

    private void OpenModulation(OutputBus bus)
    {
        var window = new ModulationWindow(bus.Modulation, bus.Name);
        RegisterWindow(window, bus);
    }

    private void OnOpenOverlay(object? sender, RoutedEventArgs e)
    {
        OpenOverlay();
    }

    private void OpenOverlay()
    {
        var buses = _engine.Inputs
            .Cast<ISignalBus>()
            .Concat(_engine.VirtualInputs)
            .ToList();

        if (buses.Count == 0)
        {
            ShowError("Сначала добавьте хотя бы один вход.");
            return;
        }

        var window = new OverlayWindow(buses);
        _overlayWindows.Add(window);
        window.Closed += (_, _) => _overlayWindows.Remove(window);
        window.Show(this);
    }

    private void RegisterWindow(Window window, object bus)
    {
        _busWindows.Add((window, bus));
        window.Closed += (_, _) => _busWindows.RemoveAll(entry => ReferenceEquals(entry.Window, window));
        window.Show(this);
    }

    private void CloseBusWindows(object bus)
    {
        var windows = _busWindows
            .Where(entry => ReferenceEquals(entry.Bus, bus))
            .Select(entry => entry.Window)
            .ToList();

        foreach (var window in windows)
        {
            window.Close();
        }

        _busWindows.RemoveAll(entry => ReferenceEquals(entry.Bus, bus));
    }

    private void CloseAllBusWindows()
    {
        foreach (var entry in _busWindows.ToList())
        {
            entry.Window.Close();
        }

        _busWindows.Clear();

        foreach (var window in _overlayWindows.ToList())
        {
            window.Close();
        }

        _overlayWindows.Clear();
    }

    private void EnsureSummaryAnalyzers()
    {
        ISignalBus? bus = _engine.Inputs.FirstOrDefault();
        bus ??= _engine.VirtualInputs.FirstOrDefault();

        if (ReferenceEquals(bus, _summaryBus)
            && _summaryScope is not null
            && _summarySpectrum is not null)
        {
            return;
        }

        if (_summaryBus is not null)
        {
            if (_summaryScope is not null)
            {
                _summaryBus.RemoveAnalyzer(_summaryScope);
            }

            if (_summarySpectrum is not null)
            {
                _summaryBus.RemoveAnalyzer(_summarySpectrum);
            }
        }

        _summaryBus = bus;
        _summaryScope = null;
        _summarySpectrum = null;

        if (bus is not null)
        {
            _summaryScope = new OscilloscopeAnalyzer { TimeWindowSeconds = 0.05 };
            _summarySpectrum = new SpectrumAnalyzer { FftSize = 8192 };
            bus.AddAnalyzer(_summaryScope);
            bus.AddAnalyzer(_summarySpectrum);
            SummaryBusText.Text = $"Шина: {bus.Name}";
        }
        else
        {
            SummaryBusText.Text = "Нет входов.";
        }
    }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        StartMeasurement();
    }

    /// <summary>Запускает измерение с текущей маршрутизацией.</summary>
    public void StartMeasurement()
    {
        try
        {
            EnsureSummaryAnalyzers();
            _engine.Start();
            ShowStatus(_engine.IsRunning ? "Измерение запущено." : "Поток не запустился.");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось запустить измерение: {ex.Message}");
        }

        RefreshRoutes();
        UpdateButtons();
        UpdateReadout();
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        _engine.Stop();
        _recordingBus = null;
        ShowStatus("Измерение остановлено.");
        RefreshRoutes();
        UpdateButtons();
        UpdateRecordingUi();
        UpdateReadout();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = _engine.SaveMeasurement();
            ShowStatus($"Отчёт сохранён: {path}");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось сохранить отчёт: {ex.Message}");
        }
    }

    private void OnToggleSessionRecording(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_engine.IsRecording)
            {
                var path = _engine.StopRecording();
                _recordingBus = null;
                ShowStatus(path is null ? "Запись остановлена." : $"Запись сохранена: {path}");
            }
            else
            {
                _engine.StartRecording(
                    $"session-{DateTime.Now:HH-mm-ss}",
                    includeOutputs: true);
                _recordingBus = null;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Запись: {ex.Message}");
        }

        RefreshRoutes();
        UpdateButtons();
        UpdateRecordingUi();
    }

    private void ToggleBusRecording(ISignalBus bus)
    {
        try
        {
            if (_engine.IsRecording)
            {
                var path = _engine.StopRecording();
                _recordingBus = null;
                ShowStatus(path is null ? "Запись остановлена." : $"Запись сохранена: {path}");
            }
            else
            {
                _engine.StartRecording(
                    $"bus-{bus.Name}",
                    new[] { bus },
                    includeOutputs: false);
                _recordingBus = bus;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Запись: {ex.Message}");
        }

        RefreshRoutes();
        UpdateButtons();
        UpdateRecordingUi();
    }

    private async void OnOpenRecording(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Выберите папку записи",
                AllowMultiple = false,
            });

            if (folders.Count == 0)
            {
                return;
            }

            var path = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowError("Не удалось получить путь к выбранной папке.");
                return;
            }

            OpenReplay(path);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось открыть запись: {ex.Message}");
        }
    }

    /// <summary>Открывает сохранённую запись в окне просмотра.</summary>
    public void OpenReplay(string directory)
    {
        try
        {
            var session = PlaybackSession.Load(directory);
            new ReplayWindow(session).Show(this);
            ShowStatus($"Открыта запись: {session.Name}");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось открыть запись: {ex.Message}");
        }
    }

    private void UpdateRecordingUi()
    {
        if (RecordSessionButton is null)
        {
            return;
        }

        var recording = _engine.IsRecording;
        RecordSessionButton.Content = recording ? "Остановить запись" : "Записать сессию";
        RecordSessionButton.Classes.Set("primary", !recording);
        RecordSessionButton.Classes.Set("danger", recording);

        RecordingStatusText.Text = recording
            ? $"Идёт запись: {_engine.RecordingDirectory}"
            : "Запись не ведётся.";
    }

    private void OnEngineStatus(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ShowStatus(message);
            UpdateButtons();
        });
    }

    private void OnEngineError(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() => ShowError(message));
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
        StatusText.Classes.Set("danger", false);
        StatusText.Classes.Set("ok", true);
    }

    private void ShowError(string message)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] Ошибка: {message}";
        StatusText.Classes.Set("ok", false);
        StatusText.Classes.Set("danger", true);
    }

    private void WarnIfRunning(string what)
    {
        if (_engine.IsRunning)
        {
            ShowStatus($"Новое значение ({what}) вступит в силу после перезапуска измерения.");
        }
    }

    private void UpdateButtons()
    {
        var idle = !_engine.IsRunning;

        StartButton.IsEnabled = idle;
        StopButton.IsEnabled = !idle;
        SaveButton.IsEnabled = true;
        RecordSessionButton.IsEnabled = _engine.IsRunning || _engine.IsRecording;
        OpenRecordingButton.IsEnabled = true;

        AddInputButton.IsEnabled = idle;
        AddOutputButton.IsEnabled = idle;
        AddMixerButton.IsEnabled = idle;
        AddBandMergeButton.IsEnabled = idle;
        ConfiguredMixerButton.IsEnabled = idle;
        OverlayButton.IsEnabled = _engine.Inputs.Count + _engine.VirtualInputs.Count > 0;

        BackendCombo.IsEnabled = idle && !IsDemoBackend;
        RateCombo.IsEnabled = idle;
        BufferCombo.IsEnabled = idle;
    }

    private void OpenConnectionEditor(Guid busId, bool isInput)
    {
        if (_engine.IsRunning)
        {
            ShowStatus("Остановите измерение перед изменением подключения.");
            return;
        }

        _connectionEditorBusId = busId;
        _connectionEditorIsInput = isInput;
        RefreshRoutes();
    }

    private void CloseConnectionEditor()
    {
        _connectionEditorBusId = null;
        RefreshRoutes();
    }

    private AudioDeviceInfo? FindDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return _engine.Devices.FirstOrDefault(device =>
            string.Equals(device.Id, deviceId, StringComparison.Ordinal));
    }

    private string DescribeConnection(string deviceId, int physicalChannel, bool isInput)
    {
        var device = FindDevice(deviceId);
        if (device is null)
        {
            return "не подключён";
        }

        var controller = string.IsNullOrWhiteSpace(device.ControllerName)
            ? device.Name
            : device.ControllerName;
        var port = string.IsNullOrWhiteSpace(device.PortName)
            ? "устройство"
            : device.PortName;
        var channel = DescribeBusChannel(device, physicalChannel, isInput);
        return $"{controller} · {port} · {channel}";
    }

    private static string DescribeBusChannel(
        AudioDeviceInfo device,
        int physicalChannel,
        bool isInput)
    {
        var count = isInput ? device.InputChannels : device.OutputChannels;
        if (count <= 1)
        {
            return "моно";
        }

        if (count == 2)
        {
            return physicalChannel == 0 ? "левый" : "правый";
        }

        return $"канал {physicalChannel + 1}";
    }

    private Control BuildConnectionEditor(InputBus? inputBus, OutputBus? outputBus)
    {
        var isInput = inputBus is not null;
        var currentDevice = FindDevice(isInput ? inputBus!.DeviceId : outputBus!.DeviceId);
        var currentChannel = isInput ? inputBus!.PhysicalChannel : outputBus!.PhysicalChannel;

        var cards = _cards
            .Where(card => (isInput ? card.Inputs.Count : card.Outputs.Count) > 0)
            .ToList();

        var cardCombo = new ComboBox
        {
            ItemsSource = cards,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Карта",
        };
        var portCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Порт",
        };
        var channelCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Канал",
        };

        var loading = false;

        void SetChannels(PortOption? port, int preferredChannel = -1)
        {
            var channels = port?.Channels ?? Array.Empty<ChannelOption>();
            channelCombo.ItemsSource = channels;
            channelCombo.SelectedItem = channels.FirstOrDefault(channel => channel.Index == preferredChannel)
                                        ?? channels.FirstOrDefault();
            channelCombo.IsEnabled = channels.Count > 1;
        }

        void SetPorts(CardOption? card, AudioDeviceInfo? preferredDevice, int preferredChannel)
        {
            var ports = card is null
                ? Array.Empty<PortOption>()
                : (isInput ? card.Inputs : card.Outputs).ToArray();
            portCombo.ItemsSource = ports;

            var preferredPort = preferredDevice is null
                ? null
                : ports.FirstOrDefault(port => port.Device.Id == preferredDevice.Id);
            var selectedPort = preferredPort ?? ports.FirstOrDefault();
            portCombo.SelectedItem = selectedPort;
            SetChannels(selectedPort, preferredPort is null ? -1 : preferredChannel);
        }

        cardCombo.SelectionChanged += (_, _) =>
        {
            if (loading)
            {
                return;
            }

            loading = true;
            SetPorts(cardCombo.SelectedItem as CardOption, null, -1);
            loading = false;
        };

        portCombo.SelectionChanged += (_, _) =>
        {
            if (loading)
            {
                return;
            }

            loading = true;
            SetChannels(portCombo.SelectedItem as PortOption);
            loading = false;
        };

        var cardKey = currentDevice is null
            ? null
            : string.IsNullOrWhiteSpace(currentDevice.CardId)
                ? currentDevice.Id
                : currentDevice.CardId;
        var initialCard = cards.FirstOrDefault(card => card.Id == cardKey) ?? cards.FirstOrDefault();

        loading = true;
        cardCombo.SelectedItem = initialCard;
        SetPorts(initialCard, currentDevice, currentChannel);
        loading = false;

        var findButton = SmallButton("Найти сигнал", (_, _) =>
        {
            if (!isInput)
            {
                return;
            }

            OpenFindSignalWindow(selection =>
            {
                loading = true;
                cardCombo.SelectedItem = selection.Card;
                SetPorts(selection.Card, selection.Port.Device, selection.Channel.Index);
                portCombo.SelectedItem = selection.Port;
                SetChannels(selection.Port, selection.Channel.Index);
                channelCombo.SelectedItem = selection.Channel;
                loading = false;
            });
        });

        var showButton = SmallButton("Показать каналы", (_, _) =>
        {
            if (portCombo.SelectedItem is PortOption port)
            {
                SelectInspector(port);
            }
        });

        var applyButton = SmallButton("Применить", (_, _) =>
        {
            if (channelCombo.SelectedItem is not ChannelOption channel)
            {
                ShowError("Выберите канал.");
                return;
            }

            ApplyConnection(inputBus, outputBus, channel, isInput);
        });
        applyButton.Classes.Add("primary");

        var cancelButton = SmallButton("Отмена", (_, _) => CloseConnectionEditor());

        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,110"),
            ColumnSpacing = 8,
        };

        AddEditorColumn(columns, 0, "КАРТА", cardCombo);
        AddEditorColumn(columns, 1, "ПОРТ", portCombo);
        AddEditorColumn(columns, 2, "КАНАЛ", channelCombo);

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var actions = new List<Button> { findButton, showButton, applyButton, cancelButton };
        foreach (var button in actions)
        {
            button.Margin = new Thickness(0, 0, 6, 0);
            buttons.Children.Add(button);
        }

        if (!isInput)
        {
            findButton.IsVisible = false;
        }

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = "Выберите карту, затем порт и канал. Список каналов зависит от порта.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#798793")),
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(columns);
        stack.Children.Add(buttons);

        var panel = new Border { Child = stack, Margin = new Thickness(14, 2, 14, 4) };
        panel.Classes.Add("route");
        return panel;
    }

    private static void AddEditorColumn(Grid grid, int column, string label, Control control)
    {
        var stack = new StackPanel { Spacing = 2 };
        var text = new TextBlock { Text = label };
        text.Classes.Add("label");
        stack.Children.Add(text);
        stack.Children.Add(control);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private void ApplyConnection(
        InputBus? inputBus,
        OutputBus? outputBus,
        ChannelOption channel,
        bool isInput)
    {
        if (_engine.IsRunning)
        {
            ShowStatus("Остановите измерение перед изменением подключения.");
            return;
        }

        try
        {
            var device = channel.Device;
            if (!string.IsNullOrWhiteSpace(device.PortId)
                && _engine.Backend is IAudioPortControl control
                && !control.TrySelectPort(
                    device.EffectiveStreamId,
                    device.PortId,
                    isInput,
                    out var error))
            {
                ShowError($"Не удалось активировать порт: {error}");
                return;
            }

            if (isInput)
            {
                _engine.ReconnectInput(inputBus!, device, channel.PhysicalChannel);
            }
            else
            {
                _engine.ReconnectOutput(outputBus!, device, channel.PhysicalChannel);
            }

            _connectionEditorBusId = null;
            _engine.RefreshDevices();
            RefreshTopology();
            RefreshRoutes();
            ShowStatus($"Подключено: {DescribeConnection(device.Id, channel.PhysicalChannel, isInput)}");
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось подключить канал: {ex.Message}");
        }
    }

    private void OnInspectorCardChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            PopulateInspectorPorts();
        }
        finally
        {
            _loading = false;
        }

        RefreshInspector();
    }

    private void OnInspectorPortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        RefreshInspector();
    }

    private void SelectInspector(PortOption port)
    {
        var card = _cards.FirstOrDefault(item =>
            item.Inputs.Contains(port) || item.Outputs.Contains(port));
        if (card is null)
        {
            return;
        }

        _loading = true;
        try
        {
            InspectorCardCombo.SelectedItem = card;
            PopulateInspectorPorts(port.Key);
        }
        finally
        {
            _loading = false;
        }

        RefreshInspector();
    }

    private void RefreshInspector()
    {
        var card = InspectorCardCombo.SelectedItem as CardOption;
        var port = InspectorPortCombo.SelectedItem as PortOption;

        if (card is null || port is null)
        {
            InspectorInfoText.Text = "Нет доступных карт и портов.";
            ChannelTable.Children.Clear();
            ChannelTable.RowDefinitions.Clear();
            _levelCells.Clear();
            PortHintText.Text = "";
            return;
        }

        var availability = port.Availability == PortAvailability.Unavailable
            ? " · не подключён"
            : string.Empty;
        InspectorInfoText.Text = $"{card.Name} · {port.Name} · {port.Direction} · "
                                 + $"{port.ChannelCount} кан.{availability}";
        var specs = AudioInputSpecsCatalog.For(
            port.JackType,
            port.IsInput,
            port.IsOutput,
            port.Device.Capabilities.BitDepth);
        PortHintText.Text = DescribePortHint(port)
                            + Environment.NewLine
                            + AudioInputSpecsCatalog.Describe(specs);
        BuildChannelTable(port);
    }

    private void BuildChannelTable(PortOption port)
    {
        ChannelTable.Children.Clear();
        ChannelTable.RowDefinitions.Clear();
        _levelCells.Clear();

        var rowCount = port.Channels.Count + 1;
        for (var index = 0; index < rowCount; index++)
        {
            ChannelTable.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        AddTableHeaderCell(0, 0, "КАНАЛ");
        AddTableHeaderCell(0, 1, "НАЗНАЧЕНИЕ");
        AddTableHeaderCell(0, 2, "ИСПОЛЬЗУЕТСЯ");
        AddTableHeaderCell(0, 3, "УРОВЕНЬ");

        for (var index = 0; index < port.Channels.Count; index++)
        {
            var channel = port.Channels[index];
            var usedBy = GetUsedBy(channel);
            var row = index + 1;

            AddTableCell(row, 0, (channel.Index + 1).ToString());
            AddTableCell(row, 1, channel.Purpose);
            AddTableCell(row, 2, usedBy.Count == 0 ? "—" : string.Join(", ", usedBy));

            var level = new TextBlock
            {
                Text = FormatLevel(GetChannelRms(channel)),
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, Liberation Mono, DejaVu Sans Mono"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.Parse("#B7C3CF")),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var levelBorder = new Border { Child = level };
            levelBorder.Classes.Add("pin");
            Grid.SetRow(levelBorder, row);
            Grid.SetColumn(levelBorder, 3);
            ChannelTable.Children.Add(levelBorder);
            _levelCells.Add((channel, level));
        }
    }

    private void AddTableHeaderCell(int row, int column, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, Liberation Mono, DejaVu Sans Mono"),
            FontSize = 9.5,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#798793")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var border = new Border { Child = block };
        border.Classes.Add("pin");
        border.Background = null;
        border.BorderThickness = new Thickness(0);
        border.Padding = new Thickness(0);
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        ChannelTable.Children.Add(border);
    }

    private void AddTableCell(int row, int column, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.Parse("#F2F6FA")),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var border = new Border { Child = block };
        border.Classes.Add("pin");
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        ChannelTable.Children.Add(border);
    }

    private static string DescribePortHint(PortOption port)
    {
        if (port.Availability == PortAvailability.Unavailable)
        {
            return "Порт сейчас недоступен (устройство не подключено).";
        }

        return $"Тип порта: {DescribeJack(port.JackType)} · каналов: {port.ChannelCount}";
    }

    private static string DescribeJack(AudioJackType jackType) => jackType switch
    {
        AudioJackType.Microphone => "микрофонный вход",
        AudioJackType.LineIn => "линейный вход",
        AudioJackType.LineOut => "линейный выход",
        AudioJackType.Headphone => "наушники",
        AudioJackType.Speaker => "динамики",
        AudioJackType.Headset => "комбинированный разъём гарнитуры",
        AudioJackType.Digital => "цифровой интерфейс",
        AudioJackType.Monitor => "монитор выхода",
        _ => "не определён",
    };

    private List<string> GetUsedBy(ChannelOption channel)
    {
        if (channel.IsInput)
        {
            return _engine.Inputs
                .Where(bus => bus.IsConnected
                              && bus.DeviceId == channel.Device.Id
                              && bus.PhysicalChannel == channel.Index)
                .Select(bus => bus.Name)
                .ToList();
        }

        return _engine.Outputs
            .Where(bus => bus.IsConnected
                          && bus.DeviceId == channel.Device.Id
                          && bus.PhysicalChannel == channel.Index)
            .Select(bus => bus.Name)
            .ToList();
    }

    private double? GetChannelRms(ChannelOption channel)
    {
        double? maximum = null;

        IEnumerable<SignalRingBuffer> buffers;
        if (channel.IsInput)
        {
            buffers = _engine.Inputs
                .Where(bus => bus.IsConnected
                              && bus.DeviceId == channel.Device.Id
                              && bus.PhysicalChannel == channel.Index)
                .Select(bus => bus.RawBuffer);
        }
        else
        {
            buffers = _engine.Outputs
                .Where(bus => bus.IsConnected
                              && bus.DeviceId == channel.Device.Id
                              && bus.PhysicalChannel == channel.Index)
                .Select(bus => bus.RawBuffer);
        }

        foreach (var buffer in buffers)
        {
            var count = Math.Min(1024, buffer.Count);
            if (count < 64)
            {
                continue;
            }

            var samples = buffer.Snapshot(count, 0);
            double sum = 0;
            foreach (var sample in samples)
            {
                sum += sample * sample;
            }

            var rms = Math.Sqrt(sum / samples.Length);
            if (maximum is null || rms > maximum)
            {
                maximum = rms;
            }
        }

        return maximum;
    }

    private static string FormatLevel(double? rms)
    {
        if (rms is not { } value || value <= 1e-9)
        {
            return "—";
        }

        return $"{20 * Math.Log10(value):0.0} dB";
    }

    private void UpdateInspectorLevels()
    {
        foreach (var (channel, level) in _levelCells)
        {
            level.Text = FormatLevel(GetChannelRms(channel));
        }
    }

    private async void OpenFindSignalWindow(Action<SignalSelection> onSelected)
    {
        if (_engine.IsRunning)
        {
            ShowStatus("Остановите измерение перед поиском сигнала.");
            return;
        }

        var window = new FindSignalWindow(_engine, _cards);
        var result = await window.ShowDialog<SignalSelection?>(this);
        if (result is { } selection)
        {
            onSelected(selection);
        }
    }

    private void UpdateSessionInfo()
    {
        SessionText.Text = string.Join(Environment.NewLine,
            $"Каталог сессии: {_session.SessionDirectory}",
            $"Каталог модуля: {_session.ModuleDirectory}",
            $"Идентификатор модуля: {_session.ModuleId}",
            _session.IsStandalone ? "Режим: автономный запуск" : "Режим: запуск из внешнего приложения");
    }

    private void UpdateReadout()
    {
        var unit = _summaryBus?.Calibration.Unit ?? "В";
        RmsUnit.Text = unit;
        VppUnit.Text = unit;

        if (!_engine.IsRunning
            || _summaryScope is null
            || _summarySpectrum is null
            || _summaryScope.LastUpdated == default)
        {
            FrequencyValue.Text = "—";
            RmsValue.Text = "—";
            VppValue.Text = "—";
            PeakValue.Text = "—";
            ReadoutDetail.Text = _engine.IsRunning ? "Ожидание данных..." : "Измерение не запущено.";
            return;
        }

        FrequencyValue.Text = _summaryScope.FrequencyHz.ToString("0.###");
        RmsValue.Text = _summaryScope.Rms.ToString("0.####");
        VppValue.Text = _summaryScope.Vpp.ToString("0.####");
        PeakValue.Text = _summarySpectrum.PeakFrequencyHz.ToString("0.###");

        ReadoutDetail.Text =
            $"Пик: {_summarySpectrum.PeakLevelDb:0.#} дБFS · шум: {_summarySpectrum.NoiseFloorDb:0.#} дБFS · "
            + $"постоянная: {_summaryScope.DcOffset:0.####} {unit}";
    }
}
