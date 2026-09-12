using NAudio.Wave;
using ScottPlot;
using ScottPlot.Colormaps;
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

        // Для воспроизведения - ПОЛЯ КЛАССА
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

        // Настройки (реально применяются и сохраняются)
        private int _currentSampleRate = 44100;
        private int _currentBufferSize = 16384;
        private string _currentTheme = "Светлая";
        private double _currentSeekStep = 1.0;

        // Новые свойства для интеграции с ГЭ и видимостью графиков
        public int WindowId { get; private set; } = 1;
        public string WindowTitleText { get; private set; } = "Окно анализа ПАКИК";
        public bool ShowOscillation { get; private set; } = true;
        public bool ShowSpectrum { get; private set; } = true;
        public bool ShowWaterfall { get; private set; } = true;

        private static readonly int[] AllowedSampleRates = { 44100, 48000, 96000 };
        private static readonly int[] AllowedBufferSizes = { 4096, 8192, 16384, 32768 };

        public MainWindow()
        {
            InitializeComponent();

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

            ApplyTheme(_currentTheme);
        }

        // Конструктор с поддержкой параметров из Главного экрана (ГЭ)
        public MainWindow(int windowId, string windowTitle, int sampleRate, int bufferSize, string theme, double seekStep,
                          bool showOsc = true, bool showSpec = true, bool showWaterfall = true) : this()
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

            // Пересоздаем захват под пришедшую с ГЭ частоту и буфер
            RecreateAudioCapture();
            ApplyTheme(_currentTheme);
            ApplyConfigurationToUI();
        }

        private void ApplyConfigurationToUI()
        {
            if (groupOscillation != null)
                groupOscillation.Visibility = ShowOscillation ? Visibility.Visible : Visibility.Collapsed;

            if (groupSpectrum != null)
                groupSpectrum.Visibility = ShowSpectrum ? Visibility.Visible : Visibility.Collapsed;

            if (groupWaterfall != null)
                groupWaterfall.Visibility = ShowWaterfall ? Visibility.Visible : Visibility.Collapsed;

            if (plotsGrid != null && plotsGrid.RowDefinitions.Count >= 3)
            {
                plotsGrid.RowDefinitions[0].Height = ShowOscillation ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                plotsGrid.RowDefinitions[1].Height = ShowSpectrum ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                plotsGrid.RowDefinitions[2].Height = ShowWaterfall ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            }
        }

        // ===== НАСТРОЙКИ: ХРАНЕНИЕ =====

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

        // ===== НАСТРОЙКИ: ПРИМЕНЕНИЕ =====

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

            bool? result = settingsWindow.ShowDialog();
            if (result == true)
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
            catch
            {
                // не критично
            }
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

                if (applied)
                {
                    setStyle.Invoke(plot, new[] { styleObj });
                }
                return applied;
            }
            catch
            {
                return false;
            }
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
            catch
            {
                return false;
            }
        }

        // ===== ГРАФИКИ =====

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
            wpfPlotSpectrogram.Plot.YLabel("Время");
            wpfPlotSpectrogram.Refresh();
        }

        private void UpdateSpectrogramWaterfall(double[] spectrum, int sampleRate)
        {
            if (_spectrogramData == null || spectrum == null || spectrum.Length == 0 || !ShowWaterfall)
                return;

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

        private void RebuildSpectrogramAtPosition(int centerPosition, double[] samples, int sampleRate)
        {
            if (_spectrogramData == null || samples == null || samples.Length == 0 || !ShowWaterfall)
                return;

            int cols = _spectrogramData.GetLength(1);
            int windowSize = 1000;
            int samplesPerRow = sampleRate / 10;

            for (int y = 0; y < _spectrogramHistoryLength; y++)
            {
                int center = centerPosition - y * samplesPerRow;
                if (center < 0)
                {
                    for (int x = 0; x < cols; x++) _spectrogramData[y, x] = 0;
                    continue;
                }

                int startIndex = Math.Max(0, center - windowSize / 2);
                int endIndex = Math.Min(samples.Length, startIndex + windowSize);
                if (endIndex <= startIndex)
                {
                    for (int x = 0; x < cols; x++) _spectrogramData[y, x] = 0;
                    continue;
                }

                double[] windowSamples = new double[endIndex - startIndex];
                Array.Copy(samples, startIndex, windowSamples, 0, windowSamples.Length);
                double[] spectrum = _signalProcessor.CalculateFFT(windowSamples);
                FillSpectrogramRow(y, spectrum, sampleRate);
            }

            RenderSpectrogram();
        }

        private void RenderSpectrogram()
        {
            if (!ShowWaterfall) return;
            wpfPlotSpectrogram.Plot.Clear();
            var heatmap = wpfPlotSpectrogram.Plot.Add.Heatmap(_spectrogramData);
            heatmap.Colormap = new ScottPlot.Colormaps.Turbo();
            heatmap.Extent = new ScottPlot.CoordinateRect(0, 25000, 0, _spectrogramHistoryLength);
            wpfPlotSpectrogram.Plot.Axes.SetLimits(0, 25000, 0, _spectrogramHistoryLength);
            wpfPlotSpectrogram.Refresh();
        }

        private void UpdateOscilloscope(double[] samples)
        {
            if (samples == null || samples.Length == 0 || !ShowOscillation) return;
            int displayCount = Math.Min(samples.Length, 1000);
            double[] displaySamples = new double[displayCount];
            Array.Copy(samples, samples.Length - displayCount, displaySamples, 0, displayCount);
            double[] time = new double[displayCount];
            for (int i = 0; i < displayCount; i++)
            {
                time[i] = i;
            }
            wpfPlotOscilloscope.Plot.Clear();
            wpfPlotOscilloscope.Plot.Add.Scatter(time, displaySamples);
            wpfPlotOscilloscope.Refresh();
        }

        private void UpdateSpectrum(double[] samples, int sampleRate)
        {
            if (samples == null || samples.Length == 0 || !ShowSpectrum)
                return;
            double[] spectrum = _signalProcessor.CalculateFFT(samples);
            UpdateSpectrumFromFFT(samples, spectrum, sampleRate);
        }

        private void UpdateSpectrumFromFFT(double[] samples, double[] spectrum, int sampleRate)
        {
            if (samples == null || samples.Length == 0 || spectrum == null || spectrum.Length == 0 || !ShowSpectrum)
                return;
            double[] frequencies = _signalProcessor.GetFrequencies(spectrum.Length, sampleRate);
            if (frequencies == null || frequencies.Length == 0)
                return;
            double peakFreq = _signalProcessor.FindPeakFrequency(spectrum, frequencies);
            double rms = _signalProcessor.CalculateRMS(samples);
            var currentLimits = wpfPlotSpectrum.Plot.Axes.GetLimits();
            double xMin = currentLimits.Left;
            double xMax = currentLimits.Right;
            if (xMin == xMax || double.IsInfinity(xMin) || double.IsNaN(xMin))
            {
                xMin = 0;
                xMax = 25000;
            }
            wpfPlotSpectrum.Plot.Clear();
            wpfPlotSpectrum.Plot.Add.Scatter(frequencies, spectrum);
            wpfPlotSpectrum.Plot.Axes.SetLimitsX(xMin, xMax);
            wpfPlotSpectrum.Refresh();
            txtFrequency.Text = $"Частота: {peakFreq:F1} Гц";
            txtRMS.Text = $"RMS: {rms:F4}";
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
                    if (ShowSpectrum || ShowWaterfall)
                    {
                        double[] spectrum = _signalProcessor.CalculateFFT(data.Samples);
                        if (ShowSpectrum) UpdateSpectrumFromFFT(data.Samples, spectrum, data.SampleRate);
                        if (ShowWaterfall) UpdateSpectrogramWaterfall(spectrum, data.SampleRate);
                    }
                });
            }
            catch
            {
                // окно закрывается
            }
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
                lock (_recordingLock)
                {
                    _recordingBuffer.Clear();
                }
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
            else
            {
                BtnPlay_Click(sender, e);
            }
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
                else
                {
                    BtnPlay_Click(sender, e);
                }
            }
        }

        private void BtnRecordNew_Click(object sender, RoutedEventArgs e)
        {
            _isRecordingMode = true;
            StopSeekRepeat();
            StopPlaybackInternal();
            lock (_recordingLock)
            {
                _recordingBuffer.Clear();
            }
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
            lock (_recordingLock)
            {
                copy = _recordingBuffer.ToArray();
            }
            if (copy == null || copy.Length == 0)
            {
                MessageBox.Show("Нет данных для сохранения. Сначала сделайте запись.",
                              "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var saveDialog = new Microsoft.Win32.SaveFileDialog();
            saveDialog.Filter = "WAV файлы (*.wav)|*.wav";
            saveDialog.Title = "Сохранить запись";
            saveDialog.FileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".wav";
            bool? result = saveDialog.ShowDialog();
            if (result == true)
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
                    MessageBox.Show($"Запись сохранена:\n{saveDialog.FileName}\n\nДлительность: {copy.Length / (double)_currentSampleRate:F2} сек\nЧастота: {_currentSampleRate} Гц",
                                  "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при сохранении:\n{ex.Message}",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Filter = "WAV файлы (*.wav)|*.wav";
            openFileDialog.Title = "Открыть запись для анализа";
            bool? result = openFileDialog.ShowDialog();
            if (result == true)
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
                            double normalizedSample = sampleValue / 32768.0;
                            samples.Add(normalizedSample);
                        }
                        _loadedSamples = samples.ToArray();
                        _playbackPosition = 0;
                        _isPaused = false;
                        StopSeekRepeat();
                        double[] displaySamples = _loadedSamples.Take(1000).ToArray();
                        UpdateOscilloscopeFromFile(displaySamples);
                        UpdateSpectrum(_loadedSamples, _loadedSampleRate);
                        double durationSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
                        txtStatus.Text = $"Файл: {System.IO.Path.GetFileName(openFileDialog.FileName)} | " +
                                       $"Длительность: {durationSeconds:F2} сек | " +
                                       $"Частота: {_loadedSampleRate} Гц | " +
                                       $"Каналов: {channels}";
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
                    MessageBox.Show($"Ошибка при открытии файла:\n{ex.Message}",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0)
            {
                MessageBox.Show("Нет данных для воспроизведения. Сначала откройте WAV файл.",
                              "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
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
                if (_playbackPosition > 0)
                {
                    long bytePosition = (long)_playbackPosition * 2;
                    _rawStream.Position = bytePosition;
                }
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
                txtStatus.Text = $"Воспроизведение... {FormatTime(0)} из {FormatTime(totalSeconds)}";
                _recordingTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при воспроизведении:\n{ex.Message}",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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
                if (_loadedSamples != null && _loadedSampleRate > 0)
                {
                    UpdateSeekTimeText(_loadedSamples.Length / (double)_loadedSampleRate);
                }
                txtStatus.Text = "Воспроизведение завершено";
            });
        }

        private void UpdateOscilloscopeFromFile(double[] samples)
        {
            if (!ShowOscillation) return;
            double[] time = new double[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                time[i] = i;
            }
            wpfPlotOscilloscope.Plot.Clear();
            wpfPlotOscilloscope.Plot.Add.Scatter(time, samples);
            wpfPlotOscilloscope.Refresh();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme(_currentTheme);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopSeekRepeat();
            _recordingTimer?.Stop();
            if (_waveOut != null)
            {
                _waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }
            _rawStream?.Dispose();
            _memoryStream?.Dispose();
            _audioCapture?.Dispose();
        }

        // ===== ПЕРЕМОТКА =====

        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSeeking) return;
            if (_loadedSamples == null || _loadedSamples.Length == 0)
                return;

            double totalSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
            double newPositionSeconds = (seekSlider.Value / 100.0) * totalSeconds;
            _playbackPosition = (int)(newPositionSeconds * _loadedSampleRate);

            if (_isPlaying)
            {
                StopPlaybackInternal();
                BtnPlay_Click(sender, new RoutedEventArgs());
            }
            else if (_isPaused)
            {
                if (_rawStream != null)
                {
                    try { _rawStream.Position = (long)_playbackPosition * 2; } catch { }
                }
                UpdateGraphicsAtPosition();
                txtStatus.Text = $"Пауза на {newPositionSeconds:F2} сек из {totalSeconds:F2} сек";
            }
            else
            {
                UpdateGraphicsAtPosition();
            }
        }

        private void TxtSeekTime_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplySeekTime();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void TxtSeekTime_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0) return;
            string text = txtSeekTime.Text?.Trim();
            bool ok = double.TryParse(text?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out _);
            if (ok) ApplySeekTime();
            else UpdateSeekTimeText(_playbackPosition / (double)_loadedSampleRate);
        }

        private void ApplySeekTime()
        {
            if (_loadedSamples == null || _loadedSamples.Length == 0) return;
            string text = txtSeekTime.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds))
                return;

            double totalSeconds = _loadedSamples.Length / (double)_loadedSampleRate;
            seconds = Math.Max(0, Math.Min(seconds, totalSeconds));
            _playbackPosition = (int)(seconds * _loadedSampleRate);

            if (_isPlaying)
            {
                StopPlaybackInternal();
                BtnPlay_Click(this, new RoutedEventArgs());
            }
            else if (_isPaused)
            {
                if (_rawStream != null)
                {
                    try { _rawStream.Position = (long)_playbackPosition * 2; } catch { }
                }
                UpdateGraphicsAtPosition();
            }
            else
            {
                UpdateGraphicsAtPosition();
            }

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

            if (_isPlaying)
            {
                StopPlaybackInternal();
                BtnPlay_Click(this, new RoutedEventArgs());
            }
            else if (_isPaused)
            {
                if (_rawStream != null) { try { _rawStream.Position = (long)_playbackPosition * 2; } catch { } }
                UpdateGraphicsAtPosition();
            }
            else
            {
                UpdateGraphicsAtPosition();
            }

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
                _seekRepeatTimer = new System.Windows.Threading.DispatcherTimer();
                _seekRepeatTimer.Interval = TimeSpan.FromMilliseconds(150);
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
            if (e.Key == Key.Left || e.Key == Key.Right)
            {
                if (Keyboard.FocusedElement is TextBox) { base.OnPreviewKeyDown(e); return; }
                double dir = e.Key == Key.Right ? +1 : -1;
                SeekBy(dir * _currentSeekStep);
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
                UpdateOscilloscopeFromFile(windowSamples);
                UpdateSpectrum(windowSamples, _loadedSampleRate);
            }
            RebuildSpectrogramAtPosition(_playbackPosition, _loadedSamples, _loadedSampleRate);

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
                    long currentBytePosition = _rawStream.Position;
                    double currentPositionSeconds = (double)currentBytePosition / (_loadedSampleRate * 2);
                    _playbackPosition = (int)(currentPositionSeconds * _loadedSampleRate);
                    if (_playbackPosition >= _loadedSamples.Length) _playbackPosition = _loadedSamples.Length - 1;

                    int windowSize = 1000;
                    int startIndex = Math.Max(0, _playbackPosition - windowSize / 2);
                    int endIndex = Math.Min(_loadedSamples.Length, startIndex + windowSize);
                    if (endIndex > startIndex && startIndex < _loadedSamples.Length)
                    {
                        double[] windowSamples = new double[endIndex - startIndex];
                        Array.Copy(_loadedSamples, startIndex, windowSamples, 0, windowSamples.Length);
                        UpdateOscilloscopeFromFile(windowSamples);
                        UpdateSpectrum(windowSamples, _loadedSampleRate);
                    }
                    RebuildSpectrogramAtPosition(_playbackPosition, _loadedSamples, _loadedSampleRate);

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