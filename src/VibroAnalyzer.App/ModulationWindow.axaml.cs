using System;
using Avalonia.Controls;
using VibroAnalyzer.Core.Modulation;

namespace VibroAnalyzer.App;

/// <summary>
/// Отдельное окно настройки генератора несущей и амплитудной модуляции.
/// Все изменения сразу записываются в <see cref="ModulationChain"/>.
/// </summary>
public partial class ModulationWindow : Window
{
    private const int PreviewPointCount = 900;
    private const double PreviewPeriods = 3.0;
    private const double MinCarrierFrequencyHz = 10.0;
    private const double MaxCarrierFrequencyHz = 20000.0;
    private const double MinBasebandFrequencyHz = 1.0;
    private const double MaxBasebandFrequencyHz = 20000.0;

    private readonly ModulationChain _modulation;
    private readonly double[] _previewTimes = new double[PreviewPointCount];
    private readonly double[] _previewValues = new double[PreviewPointCount];

    public ModulationWindow(ModulationChain modulation)
        : this(modulation, null)
    {
    }

    public ModulationWindow(ModulationChain modulation, string? busName)
    {
        ArgumentNullException.ThrowIfNull(modulation);
        _modulation = modulation;

        InitializeComponent();

        BoundText.Text = string.IsNullOrWhiteSpace(busName)
            ? "Выход не задан"
            : $"Выход: {busName}";

        PlotTheme.Apply(PreviewPlot.Plot);
        LoadFromChain();
        SubscribeToChanges();

        Opened += (_, _) => RenderPreview();
        RenderPreview();
    }

    private void LoadFromChain()
    {
        var carrier = _modulation.Carrier;

        OutputEnabledCheckBox.IsChecked = _modulation.OutputEnabled;
        CarrierFrequencyBox.Value = ToNumeric(carrier.FrequencyHz, MinCarrierFrequencyHz, MaxCarrierFrequencyHz);
        CarrierAmplitudeBox.Value = ToNumeric(carrier.Amplitude, 0.0, 1.0);
        WaveformComboBox.SelectedIndex = ToWaveformIndex(carrier.Waveform);

        ModulateCheckBox.IsChecked = _modulation.Modulate;
        ModulationIndexBox.Value = ToNumeric(_modulation.ModulationIndex, 0.0, 1.0);
        BasebandFrequencyBox.Value = ToNumeric(_modulation.BasebandFrequencyHz, MinBasebandFrequencyHz, MaxBasebandFrequencyHz);
        BasebandAmplitudeBox.Value = ToNumeric(_modulation.BasebandAmplitude, 0.0, 1.0);

        UpdatePreviewState();
    }

    private void SubscribeToChanges()
    {
        OutputEnabledCheckBox.IsCheckedChanged += (_, _) =>
        {
            _modulation.OutputEnabled = OutputEnabledCheckBox.IsChecked == true;
            UpdatePreviewState();
        };

        CarrierFrequencyBox.ValueChanged += (_, _) =>
        {
            _modulation.Carrier.FrequencyHz = ReadValue(
                CarrierFrequencyBox,
                _modulation.Carrier.FrequencyHz,
                MinCarrierFrequencyHz,
                MaxCarrierFrequencyHz);
            RenderPreview();
        };

        CarrierAmplitudeBox.ValueChanged += (_, _) =>
        {
            _modulation.Carrier.Amplitude = ReadValue(
                CarrierAmplitudeBox,
                _modulation.Carrier.Amplitude,
                0.0,
                1.0);
            RenderPreview();
        };

        WaveformComboBox.SelectionChanged += (_, _) =>
        {
            if (WaveformComboBox.SelectedIndex is >= 0 and <= (int)CarrierWaveform.Triangle)
            {
                _modulation.Carrier.Waveform = (CarrierWaveform)WaveformComboBox.SelectedIndex;
                RenderPreview();
            }
        };

        ModulateCheckBox.IsCheckedChanged += (_, _) =>
        {
            _modulation.Modulate = ModulateCheckBox.IsChecked == true;
            RenderPreview();
        };

        ModulationIndexBox.ValueChanged += (_, _) =>
        {
            _modulation.ModulationIndex = ReadValue(
                ModulationIndexBox,
                _modulation.ModulationIndex,
                0.0,
                1.0);
            RenderPreview();
        };

        BasebandFrequencyBox.ValueChanged += (_, _) =>
        {
            _modulation.BasebandFrequencyHz = ReadValue(
                BasebandFrequencyBox,
                _modulation.BasebandFrequencyHz,
                MinBasebandFrequencyHz,
                MaxBasebandFrequencyHz);
            RenderPreview();
        };

        BasebandAmplitudeBox.ValueChanged += (_, _) =>
        {
            _modulation.BasebandAmplitude = ReadValue(
                BasebandAmplitudeBox,
                _modulation.BasebandAmplitude,
                0.0,
                1.0);
            RenderPreview();
        };
    }

    private void UpdatePreviewState()
    {
        PreviewPlot.Opacity = _modulation.OutputEnabled ? 1.0 : 0.55;
    }

    /// <summary>
    /// Строит предпросмотр трёх периодов несущей с учётом огибающей АМ.
    /// </summary>
    private void RenderPreview()
    {
        var carrier = _modulation.Carrier;

        double frequency = ClampFinite(carrier.FrequencyHz, MinCarrierFrequencyHz, MaxCarrierFrequencyHz);
        double amplitude = ClampFinite(carrier.Amplitude, 0.0, 1.0);
        double basebandFrequency = ClampFinite(_modulation.BasebandFrequencyHz, MinBasebandFrequencyHz, MaxBasebandFrequencyHz);
        double modulationDepth = _modulation.Modulate
            ? ClampFinite(_modulation.ModulationIndex, 0.0, 1.0) * ClampFinite(_modulation.BasebandAmplitude, 0.0, 1.0)
            : 0.0;

        double duration = PreviewPeriods / frequency;

        for (var i = 0; i < PreviewPointCount; i++)
        {
            double t = duration * i / (PreviewPointCount - 1);
            double envelope = 1.0 + modulationDepth * Math.Sin(2.0 * Math.PI * basebandFrequency * t);
            double carrierPhase = t * frequency;

            _previewTimes[i] = t * 1000.0;
            _previewValues[i] = amplitude * envelope * EvaluateWaveform(carrier.Waveform, carrierPhase);
        }

        double peak = Math.Max(amplitude * (1.0 + modulationDepth), 0.05) * 1.1;

        var plot = PreviewPlot.Plot;
        plot.Clear();
        var signal = plot.Add.SignalXY(_previewTimes, _previewValues, PlotTheme.Accent);
        signal.LineWidth = 1.5f;
        plot.Axes.SetLimits(0.0, duration * 1000.0, -peak, peak);
        plot.XLabel("Время, мс");
        plot.YLabel("Амплитуда");

        PreviewPlot.Refresh();
    }

    private static double EvaluateWaveform(CarrierWaveform waveform, double phase)
    {
        double p = phase - Math.Floor(phase);

        return waveform switch
        {
            CarrierWaveform.Square => p < 0.5 ? 1.0 : -1.0,
            CarrierWaveform.Triangle => p < 0.25
                ? 4.0 * p
                : p < 0.75
                    ? 2.0 - 4.0 * p
                    : 4.0 * p - 4.0,
            _ => Math.Sin(2.0 * Math.PI * p),
        };
    }

    private static int ToWaveformIndex(CarrierWaveform waveform)
    {
        int index = (int)waveform;
        return index is >= 0 and <= (int)CarrierWaveform.Triangle ? index : 0;
    }

    private static decimal ToNumeric(double value, double minimum, double maximum)
    {
        return (decimal)ClampFinite(value, minimum, maximum);
    }

    private static double ReadValue(NumericUpDown box, double fallback, double minimum, double maximum)
    {
        return box.Value is decimal value
            ? Math.Clamp((double)value, minimum, maximum)
            : ClampFinite(fallback, minimum, maximum);
    }

    private static double ClampFinite(double value, double minimum, double maximum)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
    }
}
