using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VibroAnalyzer.Audio;
using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Devices;
using VibroAnalyzer.Core.Engine;
using VibroAnalyzer.Core.Modulation;
using VibroAnalyzer.Core.Playback;
using VibroAnalyzer.Core.Routing;
using VibroAnalyzer.Core.Session;

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

    private OscilloscopeAnalyzer? _summaryScope;
    private SpectrumAnalyzer? _summarySpectrum;
    private ISignalBus? _summaryBus;
    private ISignalBus? _recordingBus;

    private bool _ready;
    private bool _loading;
    private bool _closed;

    public MainWindow(AudioEngine engine, SessionContext session)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        InitializeComponent();

        _readoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _readoutTimer.Tick += (_, _) => UpdateReadout();

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
        RateCombo.ItemsSource = SupportedRates;
        BufferCombo.ItemsSource = SupportedBuffers;
        MixModeCombo.ItemsSource = MixModes.Select(mode => mode.Title).ToArray();

        BackendCombo.SelectionChanged += OnBackendChanged;
        RateCombo.SelectionChanged += OnRateChanged;
        BufferCombo.SelectionChanged += OnBufferChanged;
        InputDeviceCombo.SelectionChanged += OnInputDeviceChanged;
        OutputDeviceCombo.SelectionChanged += OnOutputDeviceChanged;
        RefreshButton.Click += OnRefreshDevices;
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
        RefreshDeviceCombos();
        EnsureDefaultInput();
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

    private void BindStaticValues()
    {
        _loading = true;
        try
        {
            BackendCombo.SelectedItem = _engine.Backend.Name;
            RateCombo.SelectedItem = SupportedRates.Contains(_engine.SampleRate)
                ? _engine.SampleRate
                : 48000;
            BufferCombo.SelectedItem = SupportedBuffers.Contains(_engine.BufferFrames)
                ? _engine.BufferFrames
                : 1024;
            MixModeCombo.SelectedIndex = 0;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshDeviceCombos()
    {
        _loading = true;
        try
        {
            var inputs = _engine.Devices.Where(device => device.IsInput).ToList();
            var outputs = _engine.Devices.Where(device => device.IsOutput).ToList();

            InputDeviceCombo.ItemsSource = inputs;
            OutputDeviceCombo.ItemsSource = outputs;

            InputDeviceCombo.SelectedItem = inputs.FirstOrDefault(device => !device.IsMonitor)
                                             ?? inputs.FirstOrDefault();
            OutputDeviceCombo.SelectedItem = outputs.FirstOrDefault(device => !device.IsMonitor)
                                              ?? outputs.FirstOrDefault();

            UpdateChannelLimits();
        }
        finally
        {
            _loading = false;
        }

        UpdateDeviceInfo();
    }

    private void UpdateChannelLimits()
    {
        if (InputDeviceCombo.SelectedItem is AudioDeviceInfo input)
        {
            var maximum = Math.Max(0, input.InputChannels - 1);
            InputChannelBox.Maximum = maximum;
            if (ReadChannel(InputChannelBox) > maximum)
            {
                InputChannelBox.Value = maximum;
            }
        }

        if (OutputDeviceCombo.SelectedItem is AudioDeviceInfo output)
        {
            var maximum = Math.Max(0, output.OutputChannels - 1);
            OutputChannelBox.Maximum = maximum;
            if (ReadChannel(OutputChannelBox) > maximum)
            {
                OutputChannelBox.Value = maximum;
            }
        }
    }

    private static int ReadChannel(NumericUpDown box)
        => box.Value is decimal value ? (int)value : 0;

    private void EnsureDefaultInput()
    {
        if (_engine.Inputs.Count > 0 || _engine.VirtualInputs.Count > 0)
        {
            return;
        }

        if (InputDeviceCombo.SelectedItem is not AudioDeviceInfo device
            || !device.IsInput
            || device.InputChannels <= 0)
        {
            return;
        }

        try
        {
            _engine.AddInput(device, 0);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось создать вход по умолчанию: {ex.Message}");
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

        UpdateDeviceInfo();
        UpdateButtons();
        UpdateRecordingUi();
        UpdateReadout();
    }

    private static TextBlock BuildEmptyHint(string text)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add("hint");
        return block;
    }

    private Border BuildInputRow(InputBus bus)
    {
        var device = _engine.Devices.FirstOrDefault(item => item.Id == bus.DeviceId);
        var subtitle = $"{device?.Name ?? bus.DeviceId} · {bus.Description} · "
                       + $"демодуляция: {DescribeDemodulation(bus.Demodulation.Kind)}";

        var scope = SmallButton("Осциллограф", (_, _) => OpenScope(bus));
        var spectrum = SmallButton("Спектр", (_, _) => OpenSpectrum(bus));
        var analyzers = SmallButton("Анализаторы", (_, _) => OpenAnalyzerCatalog(bus));
        var demodulation = SmallButton("Демодуляция", (_, _) => OpenDemodulation(bus));
        var record = CreateRecordButton(bus);
        var remove = SmallButton("Удалить", (_, _) => RemoveRoute(bus));
        remove.Classes.Add("danger");
        remove.IsEnabled = !_engine.IsRunning;

        return CreateRouteRow(bus.Name, subtitle, scope, spectrum, analyzers, demodulation, record, remove);
    }

    private Border BuildOutputRow(OutputBus bus)
    {
        var device = _engine.Devices.FirstOrDefault(item => item.Id == bus.DeviceId);
        var subtitle = $"{device?.Name ?? bus.DeviceId} · {bus.Description} · "
                       + $"несущая {bus.Modulation.Carrier.FrequencyHz:0} Гц";

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

        return CreateRouteRow(bus.Name, subtitle, enabled, modulation, scope, spectrum, analyzers, remove);
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
        if (_loading || BackendCombo.SelectedItem is not string backendName)
        {
            return;
        }

        try
        {
            CloseAllBusWindows();
            _engine.SetBackend(AudioBackendFactory.Create(backendName));
            _recordingBus = null;
            RefreshDeviceCombos();
            EnsureDefaultInput();
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
        RefreshDeviceCombos();
        RefreshRoutes();
        ShowStatus("Список устройств обновлён.");
    }

    private void OnInputDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateChannelLimits();
        UpdateDeviceInfo();
    }

    private void OnOutputDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateChannelLimits();
        UpdateDeviceInfo();
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
        if (InputDeviceCombo.SelectedItem is not AudioDeviceInfo device)
        {
            ShowError("Выберите устройство входа.");
            return;
        }

        try
        {
            _engine.AddInput(device, ReadChannel(InputChannelBox));
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось добавить вход: {ex.Message}");
        }
    }

    private void OnAddOutput(object? sender, RoutedEventArgs e)
    {
        if (OutputDeviceCombo.SelectedItem is not AudioDeviceInfo device)
        {
            ShowError("Выберите устройство выхода.");
            return;
        }

        try
        {
            _engine.AddOutput(device, ReadChannel(OutputChannelBox));
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

        BackendCombo.IsEnabled = idle;
        RateCombo.IsEnabled = idle;
        BufferCombo.IsEnabled = idle;
        InputDeviceCombo.IsEnabled = idle;
        OutputDeviceCombo.IsEnabled = idle;
        InputChannelBox.IsEnabled = idle;
        OutputChannelBox.IsEnabled = idle;
    }

    private void UpdateDeviceInfo()
    {
        var input = InputDeviceCombo.SelectedItem as AudioDeviceInfo;
        var output = OutputDeviceCombo.SelectedItem as AudioDeviceInfo;

        if (input is null && output is null)
        {
            InfoText.Text = "Устройства не найдены. Проверьте подключение и выберите бэкенд.";
            return;
        }

        var lines = new List<string>();

        if (input is not null)
        {
            lines.Add($"Вход: {input.Name}");
            lines.Add($"  каналов: {input.InputChannels}, частота по умолчанию: {input.DefaultSampleRate} Гц");
            lines.Add($"  возможности: {DescribeCapabilities(input.Capabilities)}");

            if (input.IsMonitor)
            {
                lines.Add("  ! это монитор выхода: сигнал взят из петли, а не с внешнего входа.");
            }
        }

        if (output is not null)
        {
            if (lines.Count > 0)
            {
                lines.Add("");
            }

            lines.Add($"Выход: {output.Name}");
            lines.Add($"  каналов: {output.OutputChannels}, частота по умолчанию: {output.DefaultSampleRate} Гц");
            lines.Add($"  возможности: {DescribeCapabilities(output.Capabilities)}");
        }

        InfoText.Text = string.Join(Environment.NewLine, lines);
    }

    private static string DescribeCapabilities(DeviceCapabilities capabilities)
    {
        var parts = new List<string>
        {
            capabilities.SupportsDuplex ? "дуплекс" : "не дуплекс",
            capabilities.IsAcCoupled ? "AC-связь" : "DC-связь",
            capabilities.SupportsExclusiveMode ? "эксклюзивный режим" : "общий режим",
        };

        if (capabilities.MaxInputVoltageRms is { } volts)
        {
            parts.Add($"до {volts:0.###} В СКЗ");
        }

        if (!string.IsNullOrWhiteSpace(capabilities.Notes))
        {
            parts.Add(capabilities.Notes);
        }

        return string.Join(", ", parts);
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
