using NAudio.Wave;
using ScottPlot;
using ScottPlot.Colormaps;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibroAnalyzer.Core.Models;
using VibroAnalyzer.Core.Services;

namespace VibroAnalyzer.WPF
{
    public partial class MainWindow : Window
    {
        private AudioCapture _audioCapture;
        private SignalProcessor _signalProcessor;
        private double[] _latestSamples;
        private List<short> _recordingBuffer;
        private readonly object _recordingLock = new object();
        private bool _isRecording;
        private System.Windows.Threading.DispatcherTimer _recordingTimer;
        private TimeSpan _recordingDuration;
        private double[,] _spectrogramData;
        private int _spectrogramHistoryLength = 100;
        private bool _isSeeking = false;

        // Буфер для трека огибающей / RMS
        private readonly List<double> _rmsHistory = new List<double>();
        private const int MaxRmsHistoryLength = 300;

        // Воспроизведение
        private NAudio.Wave.WaveOutEvent _waveOut;
        private NAudio.Wave.RawSourceWaveStream _rawStream;
        private System.IO.MemoryStream _memoryStream;
        private double[] _loadedSamples;
        private int _loadedSampleRate;
        private bool _isPlaying;
        private bool _isPaused;
        private int _playbackPosition;
        private DateTime _playbackStartTime;
        private bool _isRecordingMode = true;

        // Автоповтор кнопок перемотки
        private System.Windows.Threading.DispatcherTimer _seekRepeatTimer;
        private double _seekRepeatDirection = 0;

        // Настройки
        private int _currentSampleRate = 44100;
        private int _currentBufferSize = 16384;
        private string _currentTheme = "Светлая";
        private double _currentSeekStep = 1.0;

        // Выбор и масштабирование высоты активного графика
        private GroupBox _selectedChartGroup = null;
        private const double DefaultChartHeight = 260.0;
        private const double MinChartHeight = 160.0;
        private const double MaxChartHeight = 1200.0;

        // Параметры активации инструментов анализа
        public int WindowId { get; private set; } = 1;
        public string WindowTitleText { get; private set; } = "Окно анализа ПАКИК";
        public bool ShowOscillation { get; private set; } = true;
        public bool ShowSpectrum { get; private set; } = true;
        public bool ShowWaterfall { get; private set; } = true;
        public bool ShowEnvelope { get; private set; } = true;
        public bool ShowPsd { get; private set; } = true;
        public bool ShowAcf { get; private set; } = false;
        public bool ShowOctave { get; private set; } = false;
        public bool ShowCepstrum { get; private set; } = false;
        public bool ShowCwt { get; private set; } = false;
        public bool ShowPhase { get; private set; } = false;

        private static readonly int[] AllowedSampleRates = { 44100, 48000, 96000 };
        private static readonly int[] AllowedBufferSizes = { 4096, 8192, 16384, 32768 };

        public MainWindow()
        {
            InitializeComponent();

            MaxHeight = SystemParameters.WorkArea.Height;

            LoadSettings();

            _audioCapture = new AudioCapture(_currentSampleRate, _currentBufferSize);
            _signalProcessor = new SignalProcessor();
            _audioCapture.DataAvailable += OnDataAvailable;
            _recordingBuffer = new List<short>();
            _isRecording = false;
            _isRecordingMode = true;
            _recordingTimer = new System.Windows.Threading.DispatcherTimer();
            _recordingTimer.Interval = TimeSpan.FromMilliseconds(100);
            _recordingTimer.Tick += RecordingTimer_Tick;

            InitializeOscilloscope();
            InitializeSpectrum();
            InitializeSpectrogramWaterfall();
            InitializeEnvelopePlot();
            InitializePsdPlot();
            InitializeAcfPlot();
            InitializeOctavePlot();
            InitializeCepstrumPlot();
            InitializeCwtPlot();
            InitializePhasePlot();

            DisablePlotInternalInteractions();

            // Перехват колесика на ScrollViewer для предотвращения сдвига экрана
            if (plotsScrollViewer != null)
            {
                plotsScrollViewer.PreviewMouseWheel += PlotsScrollViewer_PreviewMouseWheel;
            }

            ApplyTheme(_currentTheme);
        }

        public MainWindow(int windowId, string windowTitle, int sampleRate, int bufferSize, string theme, double seekStep,
                          bool showOsc = true, bool showSpec = true, bool showWaterfall = true,
                          bool showEnv = true, bool showPsd = true, bool showAcf = false,
                          bool showOctave = false, bool showCepstrum = false, bool showCwt = false, bool showPhase = false) : this()
        {
            WindowId = windowId;
            WindowTitleText = windowTitle;
            Title = windowTitle;
            _currentSampleRate = sampleRate;
            _currentBufferSize = bufferSize;
            _currentTheme = theme;
            _currentSeekStep = seekStep;

            ShowOscillation = showOsc;
            ShowSpectrum = showSpec;
            ShowWaterfall = showWaterfall;
            ShowEnvelope = showEnv;
            ShowPsd = showPsd;
            ShowAcf = showAcf;
            ShowOctave = showOctave;
            ShowCepstrum = showCepstrum;
            ShowCwt = showCwt;
            ShowPhase = showPhase;

            RecreateAudioCapture();
            ApplyTheme(_currentTheme);
            ApplyConfigurationToUI();
        }

        private IEnumerable<ScottPlot.WPF.WpfPlot> GetAllPlots()
        {
            yield return wpfPlotOscilloscope;
            yield return wpfPlotSpectrum;
            yield return wpfPlotSpectrogram;
            yield return wpfPlotEnvelope;
            yield return wpfPlotPsd;
            yield return wpfPlotAcf;
            yield return wpfPlotOctave;
            yield return wpfPlotCepstrum;
            yield return wpfPlotCwt;
            yield return wpfPlotPhase;
        }

        private ScottPlot.WPF.WpfPlot GetPlotFromGroupBox(GroupBox group)
        {
            if (group == null) return null;
            return group.Content as ScottPlot.WPF.WpfPlot;
        }

        private void DisablePlotInternalInteractions()
        {
            foreach (var plot in GetAllPlots())
            {
                if (plot != null)
                {
                    plot.UserInputProcessor.Disable();
                }
            }
        }

        private void ApplyConfigurationToUI()
        {
            if (groupOscillation != null) groupOscillation.Visibility = ShowOscillation ? Visibility.Visible : Visibility.Collapsed;
            if (groupSpectrum != null) groupSpectrum.Visibility = ShowSpectrum ? Visibility.Visible : Visibility.Collapsed;
            if (groupWaterfall != null) groupWaterfall.Visibility = ShowWaterfall ? Visibility.Visible : Visibility.Collapsed;
            if (groupEnvelope != null) groupEnvelope.Visibility = ShowEnvelope ? Visibility.Visible : Visibility.Collapsed;
            if (groupPsd != null) groupPsd.Visibility = ShowPsd ? Visibility.Visible : Visibility.Collapsed;
            if (groupAcf != null) groupAcf.Visibility = ShowAcf ? Visibility.Visible : Visibility.Collapsed;
            if (groupOctave != null) groupOctave.Visibility = ShowOctave ? Visibility.Visible : Visibility.Collapsed;
            if (groupCepstrum != null) groupCepstrum.Visibility = ShowCepstrum ? Visibility.Visible : Visibility.Collapsed;
            if (groupCwt != null) groupCwt.Visibility = ShowCwt ? Visibility.Visible : Visibility.Collapsed;
            if (groupPhase != null) groupPhase.Visibility = ShowPhase ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===== РАЗДЕЛЕНИЕ СКРОЛЛА И ВЫБОР ИНСТРУМЕНТА =====

        private void ChartHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Parent is GroupBox groupBox)
            {
                if (e.ClickCount == 2)
                {
                    groupBox.Height = DefaultChartHeight;
                    txtStatus.Text = $"Высота графика сброшена до {DefaultChartHeight}px";
                    e.Handled = true;
                    return;
                }

                if (_selectedChartGroup == groupBox)
                {
                    DeselectChart();
                }
                else
                {
                    SelectChart(groupBox);
                }
                e.Handled = true;
            }
        }

        private void SelectChart(GroupBox group)
        {
            DeselectChart();

            _selectedChartGroup = group;

            if (_selectedChartGroup != null)
            {
                if (_selectedChartGroup.Header is Border newBorder)
                {
                    newBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 204));
                    newBorder.BorderThickness = new Thickness(2);
                    newBorder.CornerRadius = new CornerRadius(3);
                }

                // Включаем взаимодействие для выбранного графика, чтобы работало перетаскивание (Pan) правой кнопкой мыши
                var activePlot = GetPlotFromGroupBox(_selectedChartGroup);
                if (activePlot != null)
                {
                    activePlot.UserInputProcessor.Enable();
                }

                txtStatus.Text = "Выбран инструмент. ПКМ (зажать): перемещение | Колесико: зум X | Shift + колесико: зум Y | Ctrl + колесико: высота.";
            }
        }

        private void DeselectChart()
        {
            if (_selectedChartGroup != null)
            {
                if (_selectedChartGroup.Header is Border oldBorder)
                {
                    oldBorder.BorderBrush = null;
                    oldBorder.BorderThickness = new Thickness(0);
                }

                // Отключаем взаимодействие, чтобы деактивированный график не сдвигался
                var activePlot = GetPlotFromGroupBox(_selectedChartGroup);
                if (activePlot != null)
                {
                    activePlot.UserInputProcessor.Disable();
                }

                _selectedChartGroup = null;
                txtStatus.Text = "Инструмент снят с выбора. Прокрутка окна активна.";
            }
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_selectedChartGroup == null) return;

            DependencyObject dep = e.OriginalSource as DependencyObject;
            while (dep != null)
            {
                if (dep == _selectedChartGroup) return;
                dep = VisualTreeHelper.GetParent(dep);
            }

            DeselectChart();
        }

        // Управление зумом по осям X и Y, а также высотой карточки
        private void PlotsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_selectedChartGroup != null)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    // Изменение высоты карточки графика
                    double delta = e.Delta > 0 ? 40.0 : -40.0;
                    ChangeSelectedChartHeight(delta);
                }
                else
                {
                    var activePlot = GetPlotFromGroupBox(_selectedChartGroup);
                    if (activePlot != null)
                    {
                        double zoomRatio = e.Delta > 0 ? 1.15 : 0.85;

                        // Если зажат Shift — зум только по оси Y
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        {
                            activePlot.Plot.Axes.Zoom(1.0, zoomRatio);
                        }
                        // По умолчанию (без Shift) — зум только по оси X
                        else
                        {
                            activePlot.Plot.Axes.Zoom(zoomRatio, 1.0);
                        }

                        activePlot.Refresh();
                    }
                }

                // Гарантированно глушим событие, чтобы страница не сдвигалась
                e.Handled = true;
            }
        }

        private void ChangeSelectedChartHeight(double delta)
        {
            if (_selectedChartGroup == null) return;
            double currentHeight = double.IsNaN(_selectedChartGroup.Height) ? DefaultChartHeight : _selectedChartGroup.Height;
            double newHeight = Math.Max(MinChartHeight, Math.Min(MaxChartHeight, currentHeight + delta));
            _selectedChartGroup.Height = newHeight;
            txtStatus.Text = $"Высота графика: {newHeight:F0}px";
        }

        // ===== НАСТРОЙКИ =====

        private static string SettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "VibroAnalyzer", "settings.txt");

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                foreach (string line in File.ReadAllLines(SettingsFilePath))
                {
                    string[] parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    switch (parts[0])
                    {
                        case "SampleRate":
                            if (int.TryParse(parts[1], out int sr)) _currentSampleRate = sr;
                            break;
                        case "BufferSize":
                            if (int.TryParse(parts[1], out int bs)) _currentBufferSize = bs;
                            break;
                        case "Theme":
                            _currentTheme = parts[1];
                            break;
                        case "SeekStep":
                            if (double.TryParse(parts[1], NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out double ss)) _currentSeekStep = ss;
                            break;
                    }
                }
            }
            catch { }

            if (!AllowedSampleRates.Contains(_currentSampleRate)) _currentSampleRate = 44100;
            if (!AllowedBufferSizes.Contains(_currentBufferSize)) _currentBufferSize = 16384;
            if (_currentTheme != "Тёмная") _currentTheme = "Светлая";
            if (double.IsNaN(_currentSeekStep) || _currentSeekStep <= 0 || _currentSeekStep > 3600)
                _currentSeekStep = 1.0;
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));
                File.WriteAllLines(SettingsFilePath, new[]
                {
                    $"SampleRate={_currentSampleRate}",
                    $"BufferSize={_currentBufferSize}",
                    $"Theme={_currentTheme}",
                    $"SeekStep={_currentSeekStep.ToString(CultureInfo.InvariantCulture)}"
                });
            }
            catch { }
        }

        private void RecreateAudioCapture()
        {
            bool wasRecording = _isRecording;
            if (wasRecording)
            {
                _audioCapture.Stop();
                _isRecording = false;
            }
            _audioCapture.DataAvailable -= OnDataAvailable;
            _audioCapture.Dispose();
            _audioCapture = new AudioCapture(_currentSampleRate, _currentBufferSize);
            _audioCapture.DataAvailable += OnDataAvailable;
            if (wasRecording)
            {
                _audioCapture.Start();
                _isRecording = true;
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(_currentSampleRate, _currentBufferSize,
                                                    _currentTheme, _currentSeekStep);
            settingsWindow.Owner = this;
            ApplyThemeRecursive(settingsWindow, _currentTheme == "Тёмная");

            if (settingsWindow.ShowDialog() == true)
            {
                _currentSampleRate = settingsWindow.SelectedSampleRate;
                _currentBufferSize = settingsWindow.SelectedBufferSize;
                _currentTheme = settingsWindow.SelectedTheme;
                _currentSeekStep = settingsWindow.SelectedSeekStep;

                ApplyTheme(_currentTheme);
                RecreateAudioCapture();
                SaveSettings();

                txtStatus.Text = $"Настройки применены: {_currentSampleRate} Гц, буфер {_currentBufferSize}, " +
                                 $"шаг {_currentSeekStep:F3} с, тема: {_currentTheme}";
            }
        }

        // ===== ТЕМА =====

        private void ApplyTheme(string theme)
        {
            bool dark = theme == "Тёмная";
            ApplyThemeRecursive(this, dark);
            StylePlot(wpfPlotOscilloscope, dark);
            StylePlot(wpfPlotSpectrum, dark);
            StylePlot(wpfPlotSpectrogram, dark);
            StylePlot(wpfPlotEnvelope, dark);
            StylePlot(wpfPlotPsd, dark);
            StylePlot(wpfPlotAcf, dark);
            StylePlot(wpfPlotOctave, dark);
            StylePlot(wpfPlotCepstrum, dark);
            StylePlot(wpfPlotCwt, dark);
            StylePlot(wpfPlotPhase, dark);
        }

        private void ApplyThemeRecursive(DependencyObject element, bool dark)
        {
            var windowBg = dark
                ? System.Windows.Media.Color.FromRgb(30, 30, 30)
                : System.Windows.Media.Color.FromRgb(245, 245, 245);
            var controlBg = dark
                ? System.Windows.Media.Color.FromRgb(62, 62, 66)
                : System.Windows.Media.Color.FromRgb(255, 255, 255);
            var fg = dark
                ? System.Windows.Media.Color.FromRgb(240, 240, 240)
                : System.Windows.Media.Color.FromRgb(25, 25, 25);

            if (element is Window w)
            {
                w.Background = new SolidColorBrush(windowBg);
                w.Foreground = new SolidColorBrush(fg);
            }
            else if (element is Panel p)
            {
                p.Background = new SolidColorBrush(windowBg);
            }
            else if (element is TextBlock tb)
            {
                tb.Foreground = new SolidColorBrush(fg);
            }
            else if (element is Control ctrl && !(element is ScottPlot.WPF.WpfPlot))
            {
                ctrl.Background = new SolidColorBrush(controlBg);
                ctrl.Foreground = new SolidColorBrush(fg);
            }

            if (element is ScottPlot.WPF.WpfPlot) return;

            int children = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < children; i++)
            {
                ApplyThemeRecursive(VisualTreeHelper.GetChild(element, i), dark);
            }
        }

        private void StylePlot(ScottPlot.WPF.WpfPlot wpfPlot, bool dark)
        {
            if (wpfPlot == null) return;
            try
            {
                var plot = wpfPlot.Plot;
                wpfPlot.Background = new SolidColorBrush(dark
                    ? System.Windows.Media.Color.FromRgb(30, 30, 30)
                    : System.Windows.Media.Color.FromRgb(255, 255, 255));

                var figureBg = ScottPlot.Color.FromHex(dark ? "#252526" : "#ffffff");
                var dataBg = ScottPlot.Color.FromHex(dark ? "#1e1e1e" : "#f5f5f5");
                var fg = ScottPlot.Color.FromHex(dark ? "#e0e0e0" : "#16161a");

                bool applied = ApplyPlotStyleViaGetSetStyle(plot, figureBg, dataBg);
                if (applied)
                {
                    plot.Axes.Color(fg);
                }
                wpfPlot.Refresh();
            }
            catch { }
        }

        private static bool ApplyPlotStyleViaGetSetStyle(ScottPlot.Plot plot, ScottPlot.Color figure, ScottPlot.Color data)
        {
            try
            {
                var getStyle = plot.GetType().GetMethod("GetStyle",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var setStyle = plot.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SetStyle" && m.GetParameters().Length == 1);
                if (getStyle == null || setStyle == null) return false;

                object styleObj = getStyle.Invoke(plot, null);
                if (styleObj == null) return false;

                bool applied = false;
                applied |= TrySetColorProp(styleObj, "FigureBackground", figure);
                applied |= TrySetColorProp(styleObj, "DataBackground", data);

                foreach (var p in styleObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name.IndexOf("background", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool isData = p.Name.IndexOf("data", StringComparison.OrdinalIgnoreCase) >= 0;
                    applied |= TrySetColorProp(styleObj, p.Name, isData ? data : figure);
                }

                if (applied) setStyle.Invoke(plot, new[] { styleObj });
                return applied;
            }
            catch { return false; }
        }

        private static bool TrySetColorProp(object target, string propName, ScottPlot.Color color)
        {
            try
            {
                var p = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return false;
                Type t = p.PropertyType;
                if (t == typeof(ScottPlot.Color) || Nullable.GetUnderlyingType(t) == typeof(ScottPlot.Color))
                {
                    p.SetValue(target, color);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        // ===== ИНИЦИАЛИЗАЦИЯ ГРАФИКОВ =====

        private void InitializeOscilloscope()
        {
            wpfPlotOscilloscope.Plot.Clear();
            wpfPlotOscilloscope.Plot.Add.Scatter(new double[0], new double[0]);
            wpfPlotOscilloscope.Plot.Title("Осциллограмма");
            wpfPlotOscilloscope.Plot.XLabel("Время (отсчеты)");
            wpfPlotOscilloscope.Plot.YLabel("Амплитуда");
            wpfPlotOscilloscope.Plot.Axes.SetLimits(0, 1000, -1.1, 1.1);
            wpfPlotOscilloscope.Refresh();
        }

        private void InitializeSpectrum()
        {
            wpfPlotSpectrum.Plot.Clear();
            wpfPlotSpectrum.Plot.Add.Scatter(new double[0], new double[0]);
            wpfPlotSpectrum.Plot.Title("Спектр частот");
            wpfPlotSpectrum.Plot.XLabel("Частота (Гц)");
            wpfPlotSpectrum.Plot.YLabel("Амплитуда");
            wpfPlotSpectrum.Plot.Axes.SetLimits(0, 25000, 0, 1);
            wpfPlotSpectrum.Refresh();
        }

        private void InitializeSpectrogramWaterfall()
        {
            _spectrogramData = new double[_spectrogramHistoryLength, 2048];
            RenderSpectrogram();
            wpfPlotSpectrogram.Plot.Title("Спектрограмма");
            wpfPlotSpectrogram.Plot.XLabel("Частота (Гц)");
            wpfPlotSpectrogram.Plot.YLabel("История времени");
            wpfPlotSpectrogram.Refresh();
        }

        private void InitializeEnvelopePlot()
        {
            wpfPlotEnvelope.Plot.Clear();
            wpfPlotEnvelope.Plot.Add.Scatter(new double[0], new double[0]);
            wpfPlotEnvelope.Plot.Title("Огибающая / RMS во времени");
            wpfPlotEnvelope.Plot.XLabel("Шаг буфера");
            wpfPlotEnvelope.Plot.YLabel("RMS");
            wpfPlotEnvelope.Plot.Axes.SetLimits(0, MaxRmsHistoryLength, 0, 1.0);
            wpfPlotEnvelope.Refresh();
        }

        private void InitializePsdPlot()
        {
            wpfPlotPsd.Plot.Clear();
            wpfPlotPsd.Plot.Add.Scatter(new double[0], new double[0]);
            wpfPlotPsd.Plot.Title("Спектральная плотность мощности (PSD)");
            wpfPlotPsd.Plot.XLabel("Частота (Гц)");
            wpfPlotPsd.Plot.YLabel("Мощность (дБ)");
            wpfPlotPsd.Plot.Axes.SetLimits(0, 25000, -100, 10);
            wpfPlotPsd.Refresh();
        }

        private void InitializeAcfPlot()
        {
            wpfPlotAcf.Plot.Clear();
            wpfPlotAcf.Plot.Add.Scatter(new double[0], new double[0]);
            wpfPlotAcf.Plot.Title("Автокорреляционная функция (АКФ)");
            wpfPlotAcf.Plot.XLabel("Лаг (отсчеты)");
            wpfPlotAcf.Plot.YLabel("Корреляция");
            wpfPlotAcf.Plot.Axes.SetLimits(0, 500, -1.0, 1.0);
            wpfPlotAcf.Refresh();
        }

        private void InitializeOctavePlot()
        {
            wpfPlotOctave.Plot.Clear();
            wpfPlotOctave.Plot.Add.Scatter(new double[0], new double[0]);
            wpfPlotOctave.Plot.Title("Октавный спектр (1/3 октавы)");
            wpfPlotOctave.Plot.XLabel("Полоса частот");
            wpfPlotOctave.Plot.YLabel("Энергия полосы (дБ)");
            wpfPlotOctave.Refresh();
        }

        private void InitializeCepstrumPlot()
        {
            wpfPlotCepstrum.Plot.Clear();
            wpfPlotCepstrum.Plot.Add.Scatter(new double[0], new double[0]);
            wpfPlotCepstrum.Plot.Title("Вещественный кепстр (Real Cepstrum)");
            wpfPlotCepstrum.Plot.XLabel("Квефренция (мс)");
            wpfPlotCepstrum.Plot.YLabel("Амплитуда кепстра");
            wpfPlotCepstrum.Refresh();
        }

        private void InitializeCwtPlot()
        {
            wpfPlotCwt.Plot.Clear();
            wpfPlotCwt.Plot.Title("Вейвлет-анализ (CWT Скалограмма)");
            wpfPlotCwt.Plot.XLabel("Время");
            wpfPlotCwt.Plot.YLabel("Масштаб");
            wpfPlotCwt.Refresh();
        }

        private void InitializePhasePlot()
        {
            wpfPlotPhase.Plot.Clear();
            wpfPlotPhase.Plot.Add.Scatter(new double[0], new double[0]);
            wpfPlotPhase.Plot.Title("Фазовый портрет x(t) vs x(t - τ)");
            wpfPlotPhase.Plot.XLabel("x(t)");
            wpfPlotPhase.Plot.YLabel("x(t - τ)");
            wpfPlotPhase.Plot.Axes.SetLimits(-1.1, 1.1, -1.1, 1.1);
            wpfPlotPhase.Refresh();
        }

        // ===== ОБНОВЛЕНИЕ ГРАФИКОВ =====

        private void UpdateSpectrogramWaterfall(double[] spectrum, int sampleRate)
        {
            if (_spectrogramData == null || spectrum == null || spectrum.Length == 0 || !ShowWaterfall) return;

            for (int y = _spectrogramHistoryLength - 1; y > 0; y--)
            {
                for (int x = 0; x < _spectrogramData.GetLength(1); x++)
                {
                    _spectrogramData[y, x] = _spectrogramData[y - 1, x];
                }
            }

            FillSpectrogramRow(0, spectrum, sampleRate);
            RenderSpectrogram();
        }

        private void ClearSpectrogramHistory()
        {
            if (_spectrogramData == null) return;
            Array.Clear(_spectrogramData, 0, _spectrogramData.Length);
            RenderSpectrogram();
        }

        private void FillSpectrogramRow(int y, double[] spectrum, int sampleRate)
        {
            int cols = _spectrogramData.GetLength(1);
            for (int x = 0; x < cols; x++)
            {
                double frequency = (double)x / (cols - 1) * 25000;
                int sourceIndex = (int)(frequency / (sampleRate / 2.0) * spectrum.Length);
                sourceIndex = Math.Min(sourceIndex, spectrum.Length - 1);
                _spectrogramData[y, x] = sourceIndex < spectrum.Length ? spectrum[sourceIndex] : 0;
            }
        }

        private void RenderSpectrogram()
        {
            if (!ShowWaterfall) return;
            wpfPlotSpectrogram.Plot.Clear();
            var heatmap = wpfPlotSpectrogram.Plot.Add.Heatmap(_spectrogramData);
            heatmap.Colormap = new ScottPlot.Colormaps.Turbo();
            heatmap.Extent = new ScottPlot.CoordinateRect(0, 25000, 0, _spectrogramHistoryLength);
            wpfPlotSpectrogram.Refresh();
        }

        private void UpdateOscilloscope(double[] samples)
        {
            if (samples == null || samples.Length == 0 || !ShowOscillation) return;
            int displayCount = Math.Min(samples.Length, 1000);
            double[] displaySamples = new double[displayCount];
            Array.Copy(samples, samples.Length - displayCount, displaySamples, 0, displayCount);
            double[] time = new double[displayCount];
            for (int i = 0; i < displayCount; i++) time[i] = i;

            wpfPlotOscilloscope.Plot.Clear();
            wpfPlotOscilloscope.Plot.Add.Scatter(time, displaySamples);
            wpfPlotOscilloscope.Refresh();
        }

        private void UpdateSpectrumFromFFT(double[] samples, double[] spectrum, int sampleRate)
        {
            if (samples == null || samples.Length == 0 || spectrum == null || spectrum.Length == 0 || !ShowSpectrum) return;
            double[] frequencies = _signalProcessor.GetFrequencies(spectrum.Length, sampleRate);
            if (frequencies == null || frequencies.Length == 0) return;

            double peakFreq = _signalProcessor.FindPeakFrequency(spectrum, frequencies);
            double rms = _signalProcessor.CalculateRMS(samples);

            wpfPlotSpectrum.Plot.Clear();
            wpfPlotSpectrum.Plot.Add.Scatter(frequencies, spectrum);
            wpfPlotSpectrum.Refresh();
            txtFrequency.Text = $"Частота: {peakFreq:F1} Гц";
            txtRMS.Text = $"RMS: {rms:F4}";
        }

        private void UpdateEnvelope(double rms)
        {
            if (!ShowEnvelope) return;
            _rmsHistory.Add(rms);
            if (_rmsHistory.Count > MaxRmsHistoryLength) _rmsHistory.RemoveAt(0);

            double[] xs = new double[_rmsHistory.Count];
            double[] ys = _rmsHistory.ToArray();
            for (int i = 0; i < xs.Length; i++) xs[i] = i;

            wpfPlotEnvelope.Plot.Clear();
            wpfPlotEnvelope.Plot.Add.Scatter(xs, ys);
            wpfPlotEnvelope.Refresh();
        }

        private void UpdatePsd(double[] spectrum, int sampleRate)
        {
            if (!ShowPsd || spectrum == null || spectrum.Length == 0) return;
            double[] frequencies = _signalProcessor.GetFrequencies(spectrum.Length, sampleRate);
            double[] psdDb = new double[spectrum.Length];

            for (int i = 0; i < spectrum.Length; i++)
            {
                double p = Math.Max(1e-12, spectrum[i] * spectrum[i]);
                psdDb[i] = 10.0 * Math.Log10(p);
            }

            wpfPlotPsd.Plot.Clear();
            wpfPlotPsd.Plot.Add.Scatter(frequencies, psdDb);
            wpfPlotPsd.Refresh();
        }

        private void UpdateAcf(double[] samples)
        {
            if (!ShowAcf || samples == null || samples.Length == 0) return;
            int maxLag = Math.Min(500, samples.Length / 2);
            double[] lags = new double[maxLag];
            double[] r = new double[maxLag];

            double mean = samples.Average();
            double c0 = 0;
            for (int i = 0; i < samples.Length; i++) c0 += (samples[i] - mean) * (samples[i] - mean);

            for (int k = 0; k < maxLag; k++)
            {
                lags[k] = k;
                if (c0 <= 1e-15) { r[k] = 0; continue; }
                double ck = 0;
                for (int i = 0; i < samples.Length - k; i++)
                {
                    ck += (samples[i] - mean) * (samples[i + k] - mean);
                }
                r[k] = ck / c0;
            }

            wpfPlotAcf.Plot.Clear();
            wpfPlotAcf.Plot.Add.Scatter(lags, r);
            wpfPlotAcf.Refresh();
        }

        private void UpdateOctave(double[] spectrum, int sampleRate)
        {
            if (!ShowOctave || spectrum == null || spectrum.Length == 0) return;
            double[] centerFreqs = { 31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
            double[] bandEnergies = new double[centerFreqs.Length];
            double freqStep = (sampleRate / 2.0) / spectrum.Length;

            for (int b = 0; b < centerFreqs.Length; b++)
            {
                double fLow = centerFreqs[b] / Math.Sqrt(2);
                double fHigh = centerFreqs[b] * Math.Sqrt(2);
                int idxLow = (int)(fLow / freqStep);
                int idxHigh = Math.Min(spectrum.Length - 1, (int)(fHigh / freqStep));

                double sum = 0;
                for (int i = idxLow; i <= idxHigh; i++) sum += spectrum[i] * spectrum[i];
                bandEnergies[b] = 10.0 * Math.Log10(Math.Max(1e-12, sum));
            }

            double[] xs = new double[centerFreqs.Length];
            for (int i = 0; i < xs.Length; i++) xs[i] = i;

            wpfPlotOctave.Plot.Clear();
            wpfPlotOctave.Plot.Add.Scatter(xs, bandEnergies);
            wpfPlotOctave.Refresh();
        }

        private void UpdateCepstrum(double[] spectrum, int sampleRate)
        {
            if (!ShowCepstrum || spectrum == null || spectrum.Length == 0) return;
            double[] logMag = new double[spectrum.Length];
            for (int i = 0; i < spectrum.Length; i++) logMag[i] = Math.Log(Math.Max(1e-9, spectrum[i]));

            double[] cepstrum = _signalProcessor.CalculateFFT(logMag);
            int displayCount = Math.Min(300, cepstrum.Length);
            double[] quefrencyMs = new double[displayCount];
            double[] vals = new double[displayCount];

            for (int i = 0; i < displayCount; i++)
            {
                quefrencyMs[i] = (i / (double)sampleRate) * 1000.0;
                vals[i] = cepstrum[i];
            }

            wpfPlotCepstrum.Plot.Clear();
            wpfPlotCepstrum.Plot.Add.Scatter(quefrencyMs, vals);
            wpfPlotCepstrum.Refresh();
        }

        private void UpdateCwt(double[] samples)
        {
            if (!ShowCwt || samples == null || samples.Length == 0) return;
            int n = Math.Min(128, samples.Length);
            int scales = 20;
            double[,] cwtData = new double[scales, n];

            for (int s = 1; s <= scales; s++)
            {
                double width = s * 2.0;
                for (int i = 0; i < n; i++)
                {
                    double sum = 0;
                    for (int k = -10; k <= 10; k++)
                    {
                        int idx = i + k;
                        if (idx >= 0 && idx < n)
                        {
                            double t = k / width;
                            double ricker = (1.0 - t * t) * Math.Exp(-0.5 * t * t);
                            sum += samples[idx] * ricker;
                        }
                    }
                    cwtData[s - 1, i] = Math.Abs(sum);
                }
            }

            wpfPlotCwt.Plot.Clear();
            var hm = wpfPlotCwt.Plot.Add.Heatmap(cwtData);
            hm.Colormap = new ScottPlot.Colormaps.Turbo();
            wpfPlotCwt.Refresh();
        }

        private void UpdatePhase(double[] samples)
        {
            if (!ShowPhase || samples == null || samples.Length < 100) return;
            int tau = 10;
            int count = Math.Min(500, samples.Length - tau);
            double[] x = new double[count];
            double[] y = new double[count];

            for (int i = 0; i < count; i++)
            {
                x[i] = samples[i];
                y[i] = samples[i + tau];
            }

            wpfPlotPhase.Plot.Clear();
            wpfPlotPhase.Plot.Add.Scatter(x, y);
            wpfPlotPhase.Refresh();
        }

        private void CalculateAndDisplayStats(double[] samples)
        {
            if (samples == null || samples.Length == 0) return;
            double mean = samples.Average();
            double variance = 0;
            double fourthMoment = 0;
            double maxAbs = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                double diff = samples[i] - mean;
                variance += diff * diff;
                fourthMoment += diff * diff * diff * diff;
                double abs = Math.Abs(samples[i]);
                if (abs > maxAbs) maxAbs = abs;
            }

            variance /= samples.Length;
            double stdDev = Math.Sqrt(variance);
            fourthMoment /= samples.Length;

            double crestFactor = stdDev > 1e-9 ? (maxAbs / stdDev) : 0;
            double kurtosis = (variance > 1e-9) ? (fourthMoment / (variance * variance)) - 3.0 : 0;

            txtStats.Text = $"Пик-фактор: {crestFactor:F2} | Эксцесс (Kurtosis): {kurtosis:F2}";
        }

        private void OnDataAvailable(VibroData data)
        {
            _latestSamples = data.Samples;
            if (_isRecording && data.Samples != null)
            {
                lock (_recordingLock)
                {
                    foreach (double sample in data.Samples)
                    {
                        short sampleValue = (short)(sample * 32767);
                        _recordingBuffer.Add(sampleValue);
                    }
                }
            }
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (ShowOscillation) UpdateOscilloscope(data.Samples);
                    double rms = _signalProcessor.CalculateRMS(data.Samples);
                    if (ShowEnvelope) UpdateEnvelope(rms);
                    CalculateAndDisplayStats(data.Samples);

                    if (ShowSpectrum || ShowWaterfall || ShowPsd || ShowOctave || ShowCepstrum)
                    {
                        double[] spectrum = _signalProcessor.CalculateFFT(data.Samples);
                        if (ShowSpectrum) UpdateSpectrumFromFFT(data.Samples, spectrum, data.SampleRate);
                        if (ShowWaterfall) UpdateSpectrogramWaterfall(spectrum, data.SampleRate);
                        if (ShowPsd) UpdatePsd(spectrum, data.SampleRate);
                        if (ShowOctave) UpdateOctave(spectrum, data.SampleRate);
                        if (ShowCepstrum) UpdateCepstrum(spectrum, data.SampleRate);
                    }

                    if (ShowAcf) UpdateAcf(data.Samples);
                    if (ShowCwt) UpdateCwt(data.Samples);
                    if (ShowPhase) UpdatePhase(data.Samples);
                });
            }
            catch { }
        }

        // ===== ВОСПРОИЗВЕДЕНИЕ =====

        private void StopPlaybackInternal()
        {
            if (_waveOut != null)
            {
                _waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
            _rawStream?.Dispose();
            _rawStream = null;
            _memoryStream?.Dispose();
            _memoryStream = null;
            _isPlaying = false;
            _isPaused = false;
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingMode)
            {
                _audioCapture.Start();
                lock (_recordingLock) { _recordingBuffer.Clear(); }
                ClearSpectrogramHistory();
                _isRecording = true;
                _recordingDuration = TimeSpan.Zero;
                _recordingTimer.Start();
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
                btnResume.IsEnabled = false;
                btnSave.IsEnabled = false;
                txtStatus.Text = "Запись... 00:00:00";
            }
            else { BtnPlay_Click(sender, e); }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingMode)
            {
                _audioCapture.Stop();
                _isRecording = false;
                _recordingTimer.Stop();
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = false;
                btnResume.IsEnabled = true;
                btnSave.IsEnabled = true;
                txtStatus.Text = $"Пауза. Длительность: {_recordingDuration.Hours:D2}:{_recordingDuration.Minutes:D2}:{_recordingDuration.Seconds:D2}";
            }
            else
            {
                if (_waveOut != null && _isPlaying)
                {
                    _waveOut.Pause();
                    _isPlaying = false;
                    _isPaused = true;
                    _recordingTimer.Stop();
                    btnStart.IsEnabled = false;
                    btnStop.IsEnabled = false;
                    btnResume.IsEnabled = true;
                    double totalDuration = _loadedSamples.Length / (double)_loadedSampleRate;
                    txtStatus.Text = $"Пауза на {_playbackPosition / (double)_loadedSampleRate:F2} сек из {totalDuration:F2} сек";
                }
            }
        }

        private void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingMode)
            {
                _audioCapture.Start();
                _isRecording = true;
                _recordingTimer.Start();
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
                btnResume.IsEnabled = false;
                btnSave.IsEnabled = false;
                txtStatus.Text = $"Запись... {_recordingDuration.Hours:D2}:{_recordingDuration.Minutes:D2}:{_recordingDuration.Seconds:D2}";
            }
            else
            {
                if (_isPaused && _waveOut != null)
                {
                    _waveOut.Play();
                    _isPlaying = true;
                    _isPaused = false;
                    _recordingTimer.Start();
                    btnStart.IsEnabled = false;
                    btnStop.IsEnabled = true;
                    btnResume.IsEnabled = false;
                    double totalSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
                    txtStatus.Text = $"Воспроизведение... {FormatTime(totalSeconds)}";
                }
                else { BtnPlay_Click(sender, e); }
            }
        }

        private void BtnRecordNew_Click(object sender, RoutedEventArgs e)
        {
            _isRecordingMode = true;
            StopSeekRepeat();
            StopPlaybackInternal();
            lock (_recordingLock) { _recordingBuffer.Clear(); }
            ClearSpectrogramHistory();
            _recordingDuration = TimeSpan.Zero;
            _playbackPosition = 0;
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            btnResume.IsEnabled = false;
            btnSave.IsEnabled = false;
            _isSeeking = true;
            seekSlider.Value = 0;
            _isSeeking = false;
            txtSeekTime.Text = "0.000";
            txtStatus.Text = "Готов к записи нового файла";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            short[] copy;
            lock (_recordingLock) { copy = _recordingBuffer.ToArray(); }
            if (copy == null || copy.Length == 0)
            {
                MessageBox.Show("Нет данных для сохранения.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "WAV файлы (*.wav)|*.wav",
                Title = "Сохранить запись",
                FileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".wav"
            };
            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    var waveFormat = new NAudio.Wave.WaveFormat(_currentSampleRate, 16, 1);
                    using (var writer = new NAudio.Wave.WaveFileWriter(saveDialog.FileName, waveFormat))
                    {
                        byte[] buffer = new byte[copy.Length * 2];
                        Buffer.BlockCopy(copy, 0, buffer, 0, buffer.Length);
                        writer.Write(buffer, 0, buffer.Length);
                    }
                    MessageBox.Show($"Запись сохранена:\n{saveDialog.FileName}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при сохранении:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "WAV файлы (*.wav)|*.wav",
                Title = "Открыть запись для анализа"
            };
            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    using (var reader = new NAudio.Wave.WaveFileReader(openFileDialog.FileName))
                    {
                        _loadedSampleRate = reader.WaveFormat.SampleRate;
                        int channels = reader.WaveFormat.Channels;
                        byte[] buffer = new byte[reader.Length];
                        int bytesRead = reader.Read(buffer, 0, buffer.Length);
                        List<double> samples = new List<double>();
                        for (int i = 0; i < bytesRead; i += 2)
                        {
                            short sampleValue = BitConverter.ToInt16(buffer, i);
                            samples.Add(sampleValue / 32768.0);
                        }
                        _loadedSamples = samples.ToArray();
                        _playbackPosition = 0;
                        _isPaused = false;
                        StopSeekRepeat();

                        double[] displaySamples = _loadedSamples.Take(1000).ToArray();
                        UpdateOscilloscope(displaySamples);
                        double durationSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
                        txtStatus.Text = $"Файл: {System.IO.Path.GetFileName(openFileDialog.FileName)} | Длит.: {durationSeconds:F2} с";
                        _isRecordingMode = false;
                        if (_isRecording)
                        {
                            _audioCapture.Stop();
                            _isRecording = false;
                            _recordingTimer.Stop();
                        }
                        btnStart.IsEnabled = true;
                        btnStop.IsEnabled = false;
                        btnResume.IsEnabled = false;
                        btnSave.IsEnabled = false;
                        _isSeeking = true;
                        seekSlider.Value = 0;
                        _isSeeking = false;
                        txtTotalTime.Text = FormatTime(durationSeconds);
                        txtSeekTime.Text = "0.000";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при открытии файла:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0) return;
            try
            {
                StopPlaybackInternal();
                byte[] buffer = new byte[_loadedSamples.Length * 2];
                for (int i = 0; i < _loadedSamples.Length; i++)
                {
                    short sampleValue = (short)(_loadedSamples[i] * 32767);
                    BitConverter.GetBytes(sampleValue).CopyTo(buffer, i * 2);
                }
                var waveFormat = new NAudio.Wave.WaveFormat(_loadedSampleRate, 16, 1);
                _memoryStream = new System.IO.MemoryStream(buffer);
                _rawStream = new NAudio.Wave.RawSourceWaveStream(_memoryStream, waveFormat);
                if (_playbackPosition > 0) _rawStream.Position = (long)_playbackPosition * 2;

                _waveOut = new NAudio.Wave.WaveOutEvent();
                _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                _waveOut.Init(_rawStream);
                _waveOut.Play();
                _isPlaying = true;
                _isPaused = false;
                _playbackStartTime = DateTime.Now;
                double totalSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
                txtTotalTime.Text = FormatTime(totalSeconds);
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
                btnResume.IsEnabled = false;
                _recordingTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при воспроизведении:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                StopPlaybackInternal();
            }
        }

        private void WaveOut_PlaybackStopped(object sender, NAudio.Wave.StoppedEventArgs e)
        {
            _isPlaying = false;
            _isPaused = false;
            _recordingTimer.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _rawStream?.Dispose();
            _rawStream = null;
            _memoryStream?.Dispose();
            _memoryStream = null;

            Dispatcher.Invoke(() =>
            {
                btnStart.IsEnabled = true;
                btnStop.IsEnabled = false;
                btnResume.IsEnabled = false;
                _playbackPosition = 0;
                _isSeeking = true;
                seekSlider.Value = 100;
                _isSeeking = false;
                txtStatus.Text = "Воспроизведение завершено";
            });
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) => ApplyTheme(_currentTheme);

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopSeekRepeat();
            _recordingTimer?.Stop();
            StopPlaybackInternal();
            _audioCapture?.Dispose();
        }

        // ===== ПЕРЕМОТКА И ГОРЯЧИЕ КЛАВИШИ =====

        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSeeking || _loadedSamples == null || _loadedSamples.Length == 0) return;
            double totalSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
            double newPositionSeconds = (seekSlider.Value / 100.0) * totalSeconds;
            _playbackPosition = (int)(newPositionSeconds * _loadedSampleRate);

            if (_isPlaying) { StopPlaybackInternal(); BtnPlay_Click(sender, new RoutedEventArgs()); }
            else if (_isPaused)
            {
                if (_rawStream != null) { try { _rawStream.Position = (long)_playbackPosition * 2; } catch { } }
                UpdateGraphicsAtPosition();
            }
            else { UpdateGraphicsAtPosition(); }
        }

        private void TxtSeekTime_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ApplySeekTime(); Keyboard.ClearFocus(); e.Handled = true; }
        }

        private void TxtSeekTime_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0) return;
            string text = txtSeekTime.Text?.Trim();
            if (double.TryParse(text?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                ApplySeekTime();
            else UpdateSeekTimeText(_playbackPosition / (double)_loadedSampleRate);
        }

        private void ApplySeekTime()
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0) return;
            string text = txtSeekTime.Text?.Trim();
            if (string.IsNullOrEmpty(text) || !double.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds))
                return;

            double totalSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
            seconds = Math.Max(0, Math.Min(seconds, totalSeconds));
            _playbackPosition = (int)(seconds * _loadedSampleRate);

            if (_isPlaying) { StopPlaybackInternal(); BtnPlay_Click(this, new RoutedEventArgs()); }
            else if (_isPaused)
            {
                if (_rawStream != null) { try { _rawStream.Position = (long)_playbackPosition * 2; } catch { } }
                UpdateGraphicsAtPosition();
            }
            else { UpdateGraphicsAtPosition(); }

            _isSeeking = true;
            seekSlider.Value = totalSeconds > 0 ? (seconds / totalSeconds) * 100 : 0;
            _isSeeking = false;
            txtSeekTime.Text = seconds.ToString("F3", CultureInfo.InvariantCulture);
        }

        private void UpdateSeekTimeText(double seconds)
        {
            if (txtSeekTime.IsFocused) return;
            txtSeekTime.Text = seconds.ToString("F3", CultureInfo.InvariantCulture);
        }

        private void SeekBy(double deltaSeconds)
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0) return;
            double totalSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
            double currentSeconds = _playbackPosition / (double)_loadedSampleRate;
            double newSeconds = Math.Max(0, Math.Min(totalSeconds, currentSeconds + deltaSeconds));
            _playbackPosition = (int)Math.Round(newSeconds * _loadedSampleRate);

            if (_isPlaying) { StopPlaybackInternal(); BtnPlay_Click(this, new RoutedEventArgs()); }
            else if (_isPaused)
            {
                if (_rawStream != null) { try { _rawStream.Position = (long)_playbackPosition * 2; } catch { } }
                UpdateGraphicsAtPosition();
            }
            else { UpdateGraphicsAtPosition(); }

            _isSeeking = true;
            seekSlider.Value = totalSeconds > 0 ? (newSeconds / totalSeconds) * 100 : 0;
            _isSeeking = false;
        }

        private void StartSeekRepeat(double direction)
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0) return;
            SeekBy(direction * _currentSeekStep);
            _seekRepeatDirection = direction;
            if (_seekRepeatTimer == null)
            {
                _seekRepeatTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _seekRepeatTimer.Tick += (s, e) => SeekBy(_seekRepeatDirection * _currentSeekStep);
            }
            _seekRepeatTimer.Start();
        }

        private void StopSeekRepeat() => _seekRepeatTimer?.Stop();
        private void BtnSeekBack_MouseDown(object sender, MouseButtonEventArgs e) => StartSeekRepeat(-1);
        private void BtnSeekForward_MouseDown(object sender, MouseButtonEventArgs e) => StartSeekRepeat(+1);
        private void BtnSeek_MouseUp(object sender, MouseButtonEventArgs e) => StopSeekRepeat();
        private void BtnSeek_LostMouseCapture(object sender, MouseEventArgs e) => StopSeekRepeat();

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _selectedChartGroup != null)
            {
                DeselectChart();
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && _selectedChartGroup != null)
            {
                if (e.Key == Key.OemPlus || e.Key == Key.Add)
                {
                    ChangeSelectedChartHeight(+40.0);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
                {
                    ChangeSelectedChartHeight(-40.0);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Left || e.Key == Key.Right)
            {
                if (Keyboard.FocusedElement is TextBox) { base.OnPreviewKeyDown(e); return; }
                SeekBy((e.Key == Key.Right ? +1 : -1) * _currentSeekStep);
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        private void UpdateGraphicsAtPosition()
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0) return;
            int windowSize = 1000;
            int startIndex = Math.Max(0, _playbackPosition - windowSize / 2);
            int endIndex = Math.Min(_loadedSamples.Length, startIndex + windowSize);
            if (endIndex > startIndex && startIndex < _loadedSamples.Length)
            {
                double[] windowSamples = new double[endIndex - startIndex];
                Array.Copy(_loadedSamples, startIndex, windowSamples, 0, windowSamples.Length);
                UpdateOscilloscope(windowSamples);
            }
            double currentTime = _playbackPosition / (double)_loadedSampleRate;
            double totalDuration = _loadedSamples.Length / (double)_loadedSampleRate;
            UpdateSeekTimeText(currentTime);
            txtStatus.Text = $"Позиция: {currentTime:F3} сек из {totalDuration:F2} сек";
        }

        private string FormatTime(double seconds)
        {
            int mins = (int)(seconds / 60);
            int secs = (int)(seconds % 60);
            return $"{mins}:{secs:D2}";
        }

        private void RecordingTimer_Tick(object sender, EventArgs e)
        {
            if (_isRecordingMode)
            {
                _recordingDuration = _recordingDuration.Add(TimeSpan.FromMilliseconds(100));
                txtStatus.Text = $"Запись... {_recordingDuration.Hours:D2}:{_recordingDuration.Minutes:D2}:{_recordingDuration.Seconds:D2}";
            }
            else if (_isPlaying && _rawStream != null)
            {
                try
                {
                    double currentPositionSeconds = (double)_rawStream.Position / (_loadedSampleRate * 2);
                    _playbackPosition = (int)(currentPositionSeconds * _loadedSampleRate);
                    if (_playbackPosition >= _loadedSamples.Length) _playbackPosition = _loadedSamples.Length - 1;

                    UpdateGraphicsAtPosition();
                    double totalSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
                    _isSeeking = true;
                    seekSlider.Value = (currentPositionSeconds / totalSeconds) * 100;
                    _isSeeking = false;
                    UpdateSeekTimeText(currentPositionSeconds);
                    txtStatus.Text = $"Воспроизведение: {currentPositionSeconds:F3} сек из {totalSeconds:F2} сек";
                }
                catch (ObjectDisposedException) { }
            }
        }
    }
}