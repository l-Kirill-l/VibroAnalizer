using System;
using Avalonia.Controls;
using Avalonia.Threading;
using VibroAnalyzer.Core.Modulation;

namespace VibroAnalyzer.App;

/// <summary>
/// Отдельное окно настройки цепочки демодуляции и просмотра последних оценок.
/// Все изменения сразу записываются в <see cref="DemodulationChain"/>.
/// </summary>
public partial class DemodulationWindow : Window
{
    private const double MinCarrierFrequencyHz = 10.0;
    private const double MaxCarrierFrequencyHz = 20000.0;
    private const double MinLowPassCutoffHz = 1.0;
    private const double MaxLowPassCutoffHz = 20000.0;

    private readonly DemodulationChain _demodulation;
    private readonly DispatcherTimer _readoutTimer;

    public DemodulationWindow(DemodulationChain demodulation)
        : this(demodulation, null)
    {
    }

    public DemodulationWindow(DemodulationChain demodulation, string? busName)
    {
        ArgumentNullException.ThrowIfNull(demodulation);
        _demodulation = demodulation;

        InitializeComponent();

        BoundText.Text = string.IsNullOrWhiteSpace(busName)
            ? "Шина не задана"
            : $"Шина: {busName}";

        LoadFromChain();
        SubscribeToChanges();
        UpdateReadout();

        _readoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _readoutTimer.Tick += (_, _) => UpdateReadout();
        _readoutTimer.Start();

        Closed += (_, _) => _readoutTimer.Stop();
    }

    private void LoadFromChain()
    {
        EnabledCheckBox.IsChecked = _demodulation.Enabled;
        KindComboBox.SelectedIndex = ToKindIndex(_demodulation.Kind);
        CarrierFrequencyBox.Value = ToNumeric(
            _demodulation.CarrierFrequencyHz,
            MinCarrierFrequencyHz,
            MaxCarrierFrequencyHz);
        LowPassCutoffBox.Value = ToNumeric(
            _demodulation.LowPassCutoffHz,
            MinLowPassCutoffHz,
            MaxLowPassCutoffHz);
        SourceChannelBox.Value = ToNumeric(_demodulation.SourceChannel, 0.0, 7.0);
    }

    private void SubscribeToChanges()
    {
        EnabledCheckBox.IsCheckedChanged += (_, _) =>
        {
            _demodulation.Enabled = EnabledCheckBox.IsChecked == true;
        };

        KindComboBox.SelectionChanged += (_, _) =>
        {
            _demodulation.Kind = FromKindIndex(KindComboBox.SelectedIndex);
        };

        CarrierFrequencyBox.ValueChanged += (_, _) =>
        {
            _demodulation.CarrierFrequencyHz = ReadValue(
                CarrierFrequencyBox,
                _demodulation.CarrierFrequencyHz,
                MinCarrierFrequencyHz,
                MaxCarrierFrequencyHz);
        };

        LowPassCutoffBox.ValueChanged += (_, _) =>
        {
            _demodulation.LowPassCutoffHz = ReadValue(
                LowPassCutoffBox,
                _demodulation.LowPassCutoffHz,
                MinLowPassCutoffHz,
                MaxLowPassCutoffHz);
        };

        SourceChannelBox.ValueChanged += (_, _) =>
        {
            _demodulation.SourceChannel = (int)Math.Round(ReadValue(
                SourceChannelBox,
                _demodulation.SourceChannel,
                0.0,
                7.0));
        };
    }

    private void UpdateReadout()
    {
        double amplitude = _demodulation.LastAmplitude;
        double phaseDegrees = _demodulation.LastPhaseDegrees;

        LastAmplitudeText.Text = double.IsFinite(amplitude)
            ? amplitude.ToString("0.0000")
            : "—";

        LastPhaseText.Text = double.IsFinite(phaseDegrees)
            ? phaseDegrees.ToString("0.0")
            : "—";
    }

    private static int ToKindIndex(DemodulationKind kind)
    {
        return kind switch
        {
            DemodulationKind.Envelope => 1,
            DemodulationKind.Synchronous => 2,
            _ => 0,
        };
    }

    private static DemodulationKind FromKindIndex(int index)
    {
        return index switch
        {
            1 => DemodulationKind.Envelope,
            2 => DemodulationKind.Synchronous,
            _ => DemodulationKind.None,
        };
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
