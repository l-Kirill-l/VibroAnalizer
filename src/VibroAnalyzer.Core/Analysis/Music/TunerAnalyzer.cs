using VibroAnalyzer.Core.Dsp;

namespace VibroAnalyzer.Core.Analysis.Music;

/// <summary>
/// Тюнер: определяет основную частоту, ближайшую ноту равномерной темперации
/// и отклонение в центах.
/// </summary>
public sealed class TunerAnalyzer : AnalyzerBase, IAnalyzerView
{
    private const int WindowSize = 8192;
    private const double ReferenceFrequencyHz = 440.0;
    private const int ReferenceMidiNote = 69;

    private static readonly string[] NoteNames =
    {
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
    };

    private volatile AnalyzerReadout[] _readouts = Array.Empty<AnalyzerReadout>();
    private volatile string _noteName = "—";
    private double _frequencyHz;
    private double _cents;
    private int _noteNumber;
    private int _octave;

    /// <summary>Отображаемое название анализатора.</summary>
    public override string Title => "Тюнер";

    /// <summary>Категория анализатора.</summary>
    public override string Category => "Музыка";

    /// <summary>Назначение анализатора.</summary>
    public override string Description =>
        "Определяет основную частоту тона по переходам через ноль и находит ближайшую ноту "
        + "12-тонной равномерной темперации с опорой A4 = 440 Гц. Показывает октаву и отклонение "
        + "в центах, что удобно для настройки музыкальных инструментов и генераторов.";

    /// <summary>Измеренная основная частота, Гц.</summary>
    public double FrequencyHz => Volatile.Read(ref _frequencyHz);

    /// <summary>Отклонение от ближайшей ноты, центы.</summary>
    public double Cents => Volatile.Read(ref _cents);

    /// <summary>Номер ближайшей ноты MIDI.</summary>
    public int NoteNumber => Volatile.Read(ref _noteNumber);

    /// <summary>Название ближайшей ноты без октавы.</summary>
    public string NoteName => _noteName;

    /// <summary>Октава ближайшей ноты.</summary>
    public int Octave => Volatile.Read(ref _octave);

    /// <inheritdoc />
    public string XLabel => "Частота, Гц";

    /// <inheritdoc />
    public string YLabel => "Уровень";

    /// <inheritdoc />
    public double[] X => Array.Empty<double>();

    /// <inheritdoc />
    public double[] Y => Array.Empty<double>();

    /// <inheritdoc />
    public IReadOnlyList<AnalyzerReadout> Readouts => _readouts;

    /// <inheritdoc />
    public override int GetWindowFrames(int sampleRate) => WindowSize;

    /// <inheritdoc />
    public override void Process(double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var count = samples.Length;
        var mean = SignalMath.Mean(samples);
        var centered = new double[count];

        for (var i = 0; i < count; i++)
        {
            centered[i] = samples[i] - mean;
        }

        var frequency = SignalMath.EstimateFrequencyHz(centered, sampleRate);

        if (frequency < 20.0 || frequency > 5000.0)
        {
            var fallback = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
            if (fallback.PeakFrequencyHz >= 20.0 && fallback.PeakFrequencyHz <= 5000.0)
            {
                frequency = fallback.PeakFrequencyHz;
            }
        }

        if (frequency <= 0.0 || !double.IsFinite(frequency))
        {
            Publish(0.0, 0.0, 0, 0, "—");
            MarkUpdated();
            return;
        }

        // Защита от октавных ошибок: если найденная частота является 2x/3x/4x
        // и субгармоника значима, выбираем субгармонику.
        var spectrum = FftProcessor.Compute(samples, sampleRate, WindowKind.Hann, removeDc: true);
        var binWidth = (double)sampleRate / spectrum.FftSize;
        var peakBin = (int)Math.Round(frequency / binWidth);
        var peakAmplitude = LocalPeak(spectrum.Magnitudes, peakBin, 3);

        for (var divisor = 2; divisor <= 4 && peakAmplitude > 1e-12; divisor++)
        {
            var candidate = frequency / divisor;
            if (candidate < 20.0)
            {
                continue;
            }

            var candidateBin = (int)Math.Round(candidate / binWidth);
            var candidateAmplitude = LocalPeak(spectrum.Magnitudes, candidateBin, 3);

            if (candidateAmplitude > peakAmplitude * 0.10)
            {
                frequency = candidate;
                break;
            }
        }

        if (frequency <= 0.0 || !double.IsFinite(frequency))
        {
            Publish(0.0, 0.0, 0, 0, "—");
            MarkUpdated();
            return;
        }

        var midi = ReferenceMidiNote + 12.0 * Math.Log2(frequency / ReferenceFrequencyHz);
        var nearest = (int)Math.Round(midi);
        var cents = 100.0 * (midi - nearest);

        var noteIndex = ((nearest % 12) + 12) % 12;
        var octave = (int)Math.Floor(nearest / 12.0) - 1;
        var noteName = NoteNames[noteIndex];

        Publish(frequency, cents, nearest, octave, noteName);
        MarkUpdated();
    }

    private static double LocalPeak(double[] magnitudes, int centerBin, int radius)
    {
        var start = Math.Clamp(centerBin - radius, 1, magnitudes.Length - 1);
        var end = Math.Clamp(centerBin + radius, start, magnitudes.Length - 1);
        var peak = magnitudes[start];

        for (var i = start + 1; i <= end; i++)
        {
            peak = Math.Max(peak, magnitudes[i]);
        }

        return peak;
    }

    private void Publish(        double frequencyHz,
        double cents,
        int noteNumber,
        int octave,
        string noteName)
    {
        Volatile.Write(ref _frequencyHz, frequencyHz);
        Volatile.Write(ref _cents, cents);
        Volatile.Write(ref _noteNumber, noteNumber);
        Volatile.Write(ref _octave, octave);
        _noteName = noteName;

        var label = noteNumber == 0 && string.Equals(noteName, "—", StringComparison.Ordinal)
            ? "—"
            : noteName + octave;

        _readouts = new[]
        {
            new AnalyzerReadout("Частота", frequencyHz, "Гц"),
            new AnalyzerReadout("Нота", noteNumber, label),
            new AnalyzerReadout("Октава", octave, string.Empty),
            new AnalyzerReadout("Отклонение", cents, "цент")
        };
    }
}
