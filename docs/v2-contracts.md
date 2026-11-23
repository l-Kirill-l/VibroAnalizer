# VibroAnalyzer v2 — контракты ядра

Этот файл — общий контракт для параллельной разработки. Все публичные типы должны совпадать с ним.
Проект: `src/VibroAnalyzer.Core`, `src/VibroAnalyzer.Audio`, `src/VibroAnalyzer.App`.
Целевой framework: `net10.0`. UI: Avalonia 12.1.6. Графики: ScottPlot.Avalonia 5.1.59.

## Правила для параллельных исполнителей

- Не менять `.csproj`, `.slnx`, `docs/v2-contracts.md`, чужие файлы.
- Не запускать `dotnet build` / `dotnet test` (сборку выполняет главный агент).
- Не трогать `VibroWaveLab`.
- Писать код в namespace, указанных ниже.
- Комментарии и пользовательские строки — по-русски.
- Не добавлять внешние зависимости, кроме уже подключённых (`MathNet.Numerics`, `NAudio`, Avalonia, ScottPlot).
- Если сигнатура кажется противоречивой — реализовать ровно как в контракте.

## Signals

```csharp
namespace VibroAnalyzer.Core.Signals;

public readonly record struct SignalFormat(int SampleRate, int Channels);

public sealed class SignalBlock
{
    public float[] Samples { get; }
    public int FrameCount { get; }
    public int Channels { get; }
    public int SampleRate { get; }
    public long Sequence { get; }
    public DateTimeOffset Timestamp { get; }
    public double DurationSeconds { get; }
    public SignalFormat Format { get; }
    public double GetSample(int frame, int channel);
    public void CopyChannelTo(int channel, Span<double> destination);
    public double[] ToChannelDouble(int channel);
    public SignalBlock(float[] samples, int channels, int sampleRate, long sequence = 0, DateTimeOffset? timestamp = null);
}

public sealed class SignalRingBuffer
{
    public SignalRingBuffer(int channels, int capacityFrames);
    public int Channels { get; }
    public int CapacityFrames { get; }
    public int Count { get; }
    public long LastSequence { get; }
    public DateTimeOffset LastTimestamp { get; }
    public void Write(SignalBlock block);
    public double[] Snapshot(int frames, int channel);
    public void Clear();
}
```

`SignalRingBuffer.Write` проверяет только число каналов. `Snapshot` возвращает ровно `frames` значений, при нехватке данных дополняет нулями слева (старые данные — в начале массива).

## Devices

```csharp
namespace VibroAnalyzer.Core.Devices;

public enum AudioJackType { Unknown, Microphone, LineIn, LineOut, Headphone, Speaker, Headset, Digital, Monitor }

public sealed record AudioChannelInfo(
    int Index,
    string Name,
    bool IsInput,
    AudioJackType JackType,
    string Notes = "");

public sealed record DeviceCapabilities(
    bool SupportsDuplex,
    bool IsAcCoupled,
    double? MaxInputVoltageRms,
    bool SupportsExclusiveMode,
    bool SupportsLoopback,
    bool SupportsChannelMapping,
    string Notes = "",
    double? MaxInputCurrentMa = null,
    double? InputImpedanceOhms = null,
    double? MinFrequencyHz = 20.0,
    double? MaxFrequencyHz = 20000.0,
    int? BitDepth = null);

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    string Backend,
    bool IsInput,
    bool IsOutput,
    int InputChannels,
    int OutputChannels,
    int DefaultSampleRate,
    IReadOnlyList<int> SupportedSampleRates,
    IReadOnlyList<AudioChannelInfo> Channels,
    DeviceCapabilities Capabilities,
    bool IsMonitor = false)
{
    public bool IsDuplex { get; }
    public override string ToString();
}
```

## Audio abstractions

```csharp
namespace VibroAnalyzer.Core.Audio;

public sealed record AudioStreamOptions(
    string? InputDeviceId,
    string? OutputDeviceId,
    int SampleRate,
    int InputChannels,
    int OutputChannels,
    int BufferFrames = 1024)
{
    public bool HasInput { get; }
    public bool HasOutput { get; }
    public bool IsDuplex { get; }
}

public interface IAudioStream : IDisposable
{
    AudioStreamOptions Options { get; }
    bool IsRunning { get; }
    event EventHandler<SignalBlock>? InputAvailable;
    event EventHandler<string>? Error;
    void Start();
    void Stop();
    bool Write(SignalBlock block);
}

public interface IAudioBackend
{
    string Name { get; }
    bool IsAvailable { get; }
    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();
    IAudioStream OpenStream(AudioStreamOptions options);
}

public sealed class AudioBackendException : Exception
{
    public AudioBackendException(string message);
    public AudioBackendException(string message, Exception innerException);
}
```

## Calibration

```csharp
namespace VibroAnalyzer.Core.Calibration;

public enum CalibrationMode { Voltage, Current, Physical }

public sealed class CalibrationProfile
{
    public string Name { get; set; } = "По умолчанию";
    public double FullScaleVolts { get; set; } = 1.0;
    public double InputGain { get; set; } = 1.0;
    public double SensorSensitivity { get; set; } = 1.0;
    public double ShuntOhms { get; set; } = 1.0;
    public CalibrationMode Mode { get; set; } = CalibrationMode.Voltage;
    public string PhysicalUnit { get; set; } = "В";
    public string Unit { get; }
    public double ToDisplay(double normalizedSample);
}
```

`ToDisplay`: `volts = normalized * FullScaleVolts / InputGain`; `Voltage => volts / SensorSensitivity`; `Current => volts / ShuntOhms / SensorSensitivity`; `Physical => volts / SensorSensitivity`.

## DSP

```csharp
namespace VibroAnalyzer.Core.Dsp;

public enum WindowKind { Rectangular, Hann, Hamming, BlackmanHarris, FlatTop }

public static class WindowFunctions
{
    public static double[] Create(WindowKind kind, int length);
    public static double Sum(double[] window);
}

public sealed class FftResult
{
    public double[] Frequencies { get; }
    public double[] Magnitudes { get; }       // амплитуда (не dB)
    public double[] MagnitudesDb { get; }     // 20*log10(max(mag, 1e-12))
    public int FftSize { get; }
    public int SampleRate { get; }
    public double DcOffset { get; }
    public double PeakFrequencyHz { get; }
    public double PeakLevelDb { get; }
}

public static class FftProcessor
{
    public static FftResult Compute(double[] samples, int sampleRate, WindowKind windowKind = WindowKind.Hann, bool removeDc = true);
    public static (double FrequencyHz, double LevelDb) FindPeak(double[] magnitudesDb, int sampleRate, int fftSize, int minBin = 1);
}

public sealed class BiquadFilter
{
    public static BiquadFilter CreateLowPass(int sampleRate, double cutoffHz, double q = 0.70710678);
    public static BiquadFilter CreateHighPass(int sampleRate, double cutoffHz, double q = 0.70710678);
    public static BiquadFilter CreateNotch(int sampleRate, double frequencyHz, double q = 30.0);
    public double Process(double sample);
    public void Reset();
}

public static class SignalMath
{
    public static double Mean(ReadOnlySpan<double> samples);
    public static double Rms(ReadOnlySpan<double> samples);
    public static double Peak(ReadOnlySpan<double> samples);
    public static double PeakToPeak(ReadOnlySpan<double> samples);
    public static double EstimateFrequencyHz(ReadOnlySpan<double> samples, int sampleRate);
    public static double DbFs(double amplitude);
    public static double FromDbFs(double db);
    public static void RemoveDcInPlace(Span<double> samples);
}
```

## Modulation / demodulation

```csharp
namespace VibroAnalyzer.Core.Modulation;

public enum CarrierWaveform { Sine, Square, Triangle }

public sealed class CarrierGenerator
{
    public double FrequencyHz { get; set; }
    public double Amplitude { get; set; }
    public CarrierWaveform Waveform { get; set; }
    public double Phase { get; set; } // 0..1
    public void Reset();
    public double NextSample(int sampleRate);
    public void Fill(Span<double> destination, int sampleRate);
}

public sealed class ModulationChain
{
    public bool OutputEnabled { get; set; }
    public bool Modulate { get; set; }
    public double ModulationIndex { get; set; }
    public double BasebandFrequencyHz { get; set; }
    public double BasebandAmplitude { get; set; }
    public CarrierGenerator Carrier { get; }
    public void Reset();
    public SignalBlock GenerateBlock(int sampleRate, int frames, int channels, DateTimeOffset? timestamp = null);
}

public enum DemodulationKind { None, Envelope, Synchronous }

public sealed class DemodulationChain
{
    public bool Enabled { get; set; }
    public DemodulationKind Kind { get; set; }
    public double CarrierFrequencyHz { get; set; }
    public double LowPassCutoffHz { get; set; }
    public int SourceChannel { get; set; }
    public double LastAmplitude { get; }
    public double LastPhaseDegrees { get; }
    public void Configure(int sampleRate);
    public double[] Process(SignalBlock block);
    public void Reset();
}
```

## Analyzers

```csharp
namespace VibroAnalyzer.Core.Analysis;

public enum AnalyzerSource { Raw, Demodulated }

public abstract class AnalyzerBase
{
    public Guid Id { get; }
    public abstract string Title { get; }
    public bool IsEnabled { get; set; }
    public AnalyzerSource Source { get; set; }
    public int Channel { get; set; }
    public CalibrationProfile Calibration { get; set; }
    public DateTimeOffset LastUpdated { get; protected set; }
    public abstract int GetWindowFrames(int sampleRate);
    public abstract void Process(double[] samples, int sampleRate);
    protected void MarkUpdated();
}

public sealed class OscilloscopeAnalyzer : AnalyzerBase
{
    public double TimeWindowSeconds { get; set; }
    public double[] Times { get; }
    public double[] Values { get; }
    public double Vpp { get; }
    public double Rms { get; }
    public double DcOffset { get; }
    public double FrequencyHz { get; }
}

public sealed class SpectrumAnalyzer : AnalyzerBase
{
    public int FftSize { get; set; }
    public WindowKind Window { get; set; }
    public double AveragingFactor { get; set; }
    public double[] Frequencies { get; }
    public double[] MagnitudesDb { get; }
    public double PeakFrequencyHz { get; }
    public double PeakLevelDb { get; }
    public double NoiseFloorDb { get; }
}

public sealed class AnalyzerHost : IDisposable
{
    public AnalyzerHost(SignalRingBuffer raw, SignalRingBuffer demod, CalibrationProfile calibration);
    public IReadOnlyList<AnalyzerBase> Analyzers { get; }
    public int SampleRate { get; }
    public void Add(AnalyzerBase analyzer);
    public void Remove(AnalyzerBase analyzer);
    public void Clear();
    public void Start(int sampleRate, int intervalMs = 50);
    public void Stop();
    public void Dispose();
}
```

## Session

```csharp
namespace VibroAnalyzer.Core.Session;

public sealed record Measurement(string Parameter, double Value, string Units);
public sealed record SpectrumPeak(double FrequencyHz, double LevelDb);

public sealed class SessionContext
{
    public string SessionDirectory { get; }
    public string ModuleId { get; }
    public bool IsStandalone { get; }
    public string ModuleDirectory { get; }
    public static SessionContext FromArgs(string[] args);
    public void EnsureDirectories();
}

public sealed class SessionWriter
{
    public SessionWriter(SessionContext context);
    public string WriteReport(ReportData report);
    public void AppendMeasurements(IEnumerable<Measurement> measurements);
    public void WriteStatus(string state, string details = "");
}

public sealed class ReportData
{
    public string Schema { get; set; } = "vibroanalyzer.report.v1";
    public string ModuleId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Backend { get; set; } = "";
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";
    public int SampleRate { get; set; }
    public int InputChannels { get; set; }
    public int OutputChannels { get; set; }
    public List<Measurement> Measurements { get; set; } = new();
    public List<SpectrumPeak> SpectrumPeaks { get; set; } = new();
}
```

## Routing (логические шины)

```csharp
namespace VibroAnalyzer.Core.Routing;

public enum FusionBand { Full, LowPass, HighPass }
public enum FusionMode { Average, Weighted, Sum, Difference, Minimum, Maximum, BandMerge }

public sealed record FusionSource(
    ISignalBus Source,
    double Gain = 1.0,
    FusionBand Band = FusionBand.Full,
    double CutoffHz = 1000.0);

public interface ISignalBus
{
    Guid Id { get; }
    string Name { get; }
    int SampleRate { get; }
    int Channels { get; }          // логические входы всегда моно
    bool IsVirtual { get; }
    CalibrationProfile Calibration { get; }
    SignalRingBuffer RawBuffer { get; }
    SignalRingBuffer DemodBuffer { get; }
    AnalyzerHost Analyzers { get; }
    DemodulationChain Demodulation { get; }
    void AddAnalyzer(AnalyzerBase analyzer);
    void RemoveAnalyzer(AnalyzerBase analyzer);
}

public sealed class InputBus : ISignalBus, IDisposable
{
    public InputBus(string name, string deviceId, int physicalChannel, int sampleRate,
                    CalibrationProfile? calibration = null);
    public string DeviceId { get; }
    public int PhysicalChannel { get; }
    public string Description { get; }
    public void Configure(int sampleRate);
    public void Start();
    public void Stop();
    public void Process(SignalBlock block); // выделяет физический канал
}

public sealed class VirtualInputBus : ISignalBus, IDisposable
{
    public VirtualInputBus(string name, IEnumerable<FusionSource> sources, FusionMode mode,
                           int sampleRate, CalibrationProfile? calibration = null);
    public FusionMode Mode { get; set; }
    public IReadOnlyList<FusionSource> Sources { get; }
    public string Description { get; }
    public bool DependsOn(Guid busId);
    public void Configure(int sampleRate);
    public void Start();
    public void Stop();
    public void Update(); // только новые отсчёты источников
}

public sealed class OutputBus
{
    public OutputBus(string name, string deviceId, int physicalChannel);
    public string DeviceId { get; }
    public int PhysicalChannel { get; }
    public bool Enabled { get; set; }
    public double Gain { get; set; }
    public ModulationChain Modulation { get; }
    public float[] GenerateMonoBlock(int sampleRate, int frames);
}

public static class OutputMixer
{
    public static float[] Mix(IReadOnlyList<OutputBus> buses, int frames,
                              int sampleRate, int deviceChannels);
}
```

## Engine

```csharp
namespace VibroAnalyzer.Core.Engine;

public sealed class AudioEngine : IDisposable
{
    public AudioEngine(IAudioBackend backend, SessionContext session);
    public IAudioBackend Backend { get; }
    public IReadOnlyList<AudioDeviceInfo> Devices { get; }
    public IReadOnlyList<InputBus> Inputs { get; }
    public IReadOnlyList<OutputBus> Outputs { get; }
    public IReadOnlyList<VirtualInputBus> VirtualInputs { get; }
    public int SampleRate { get; set; }
    public int BufferFrames { get; set; }
    public bool IsRunning { get; }
    public CalibrationProfile DefaultCalibration { get; }
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler? RoutesChanged;
    public void RefreshDevices();
    public InputBus AddInput(AudioDeviceInfo device, int physicalChannel, string? name = null);
    public void RemoveInput(InputBus bus);
    public OutputBus AddOutput(AudioDeviceInfo device, int physicalChannel, string? name = null);
    public void RemoveOutput(OutputBus bus);
    public VirtualInputBus AddVirtualInput(string? name, IEnumerable<FusionSource> sources, FusionMode mode);
    public void RemoveVirtualInput(VirtualInputBus bus);
    public void Start();
    public void Stop();
    public bool IsRecording { get; }
    public string? RecordingDirectory { get; }
    public void StartRecording(string? name = null, IEnumerable<ISignalBus>? buses = null, bool includeOutputs = false);
    public string? StopRecording();
    public string SaveMeasurement();
    public void SetBackend(IAudioBackend backend);
    public void Dispose();
}
```

## Recording / playback

```csharp
namespace VibroAnalyzer.Core.Recording;

public sealed class RecordingTarget
{
    public static RecordingTarget ForBus(ISignalBus bus);
    public static RecordingTarget ForOutput(OutputBus output);
    public RecordingBusInfo Info { get; }
}

public sealed class SessionRecorder : IDisposable
{
    public SessionRecorder(string directory, int sampleRate, IEnumerable<RecordingTarget> targets);
    public string Directory { get; }
    public bool IsRecording { get; }
    public void Start();
    public string Stop();
}

namespace VibroAnalyzer.Core.Playback;

public sealed class PlaybackSession : IDisposable
{
    public static PlaybackSession Load(string directory);
    public string Directory { get; }
    public string Name { get; }
    public int SampleRate { get; }
    public TimeSpan Duration { get; }
    public TimeSpan Position { get; }
    public double Speed { get; set; }
    public bool IsPlaying { get; }
    public IReadOnlyList<PlaybackBus> Buses { get; }
    public void Play();
    public void Pause();
    public void Stop();
    public void Seek(TimeSpan position);
}

public sealed class PlaybackBus : ISignalBus, IDisposable
{
    public string Kind { get; }
}
```


Правила:
- Изменять маршруты можно только при остановленном измерении.
- На один физический канал можно создать несколько логических входов и выходов.
- Несколько выходов на одном физическом канале складываются.
- Виртуальный вход читает только новые отсчёты источников (без повторной склейки),
  поэтому его спектр не загрязняется разрывами.
- Анализаторы и окна привязываются к конкретной шине; на одну шину можно
  открыть несколько окон и несколько экземпляров анализаторов.


## Audio backends (проект `VibroAnalyzer.Audio`)

```csharp
namespace VibroAnalyzer.Audio;

public static class AudioBackendFactory
{
    public static IReadOnlyList<string> AvailableBackendNames { get; }
    public static IAudioBackend Create(string? name = null);
}
```

- `SimulatedAudioBackend`: всегда доступен, имя `"Демо"`.
- `PipeWireAudioBackend`: Linux, имя `"PipeWire"`, использует `parec`/`pacat`/`pactl`.
- `WasapiAudioBackend`: Windows, имя `"WASAPI"`, использует NAudio.
- `AudioBackendFactory.Create` выбирает по имени; по умолчанию — первый доступный, иначе демо.

## UI (проект `VibroAnalyzer.App`)

- `MainWindow(AudioEngine engine, SessionContext session)`
  - управляет списком входов, выходов и виртуальных входов;
  - `OpenAnalyzerWindows(string specification)`, `StartMeasurement()`;
  - `OpenSettingsAsync()`, `ConfigureDemoRouting()` для демонстрационного набора.
- `SettingsWindow(bool demoEnabled, DemoSignalConfig config)` — демо-режим и
  конструктор частот/колебаний.
- `HelpWindow(AnalyzerHelp help, string? title = null)` — справка по клику.
- `ScopeWindow(OscilloscopeAnalyzer analyzer, string? busName = null)`
- `SpectrumWindow(SpectrumAnalyzer analyzer, string? busName = null)`
- `ModulationWindow(ModulationChain modulation, string? busName = null)`
- `DemodulationWindow(DemodulationChain demodulation, string? busName = null)`
- `AnalyzerWindow(AnalyzerBase analyzer, string? busName = null)` — универсальное окно
  с описанием, параметрами, показаниями и кнопкой «?».
- `AnalyzerCatalogWindow(ISignalBus bus)` — категории и мини-описания.
- `MixerWindow(IReadOnlyList<ISignalBus> sources)` — выбор конкретных каналов,
  усиления и полосы.
- `OverlayWindow(IReadOnlyList<ISignalBus> buses)` — наложение временных форм и спектров.
- `ReplayWindow(PlaybackSession session)` — транспорт, перемотка и скорость.

`OutputBus` также реализует `ISignalBus`, поэтому осциллограф, спектр и каталог
анализаторов можно открывать для выходных (модулированных) сигналов.

Окна анализаторов — отдельные `Window`. Для одной шины можно открыть несколько
окон; каждое окно осциллографа/спектра создаёт собственный экземпляр анализатора
и подключает его к выбранной шине. Закрытие окна отключает анализатор.

Аргументы запуска `VibroAnalyzer.App`:

```
--session-dir <путь>       каталог сессии
--module-id <id>           идентификатор модуля
--demo                     демо-бэкенд, 2 входа + 2 выхода + микшер
--start                    сразу запустить измерение
--open=scope,spectrum,...  открыть окна для первой шины
```

