using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using ScottPlot;
using VibroAnalyzer.Core.Dsp;
using VibroAnalyzer.Core.Routing;

namespace VibroAnalyzer.App;

/// <summary>
/// Накладывает временные формы и спектры нескольких шин на общие графики.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly int[] FftSizes = { 1024, 2048, 4096, 8192, 16384, 32768 };

    private readonly IReadOnlyList<ISignalBus> _buses;
    private readonly DispatcherTimer _timer;

    public OverlayWindow(IReadOnlyList<ISignalBus> buses)
    {
        ArgumentNullException.ThrowIfNull(buses);
        _buses = buses;

        InitializeComponent();

        FftSizeBox.ItemsSource = FftSizes;
        FftSizeBox.SelectedIndex = 3;
        SourceBox.SelectedIndex = 0;

        PlotTheme.Apply(TimePlot.Plot);
        PlotTheme.Apply(SpectrumPlot.Plot);

        TimePlot.Plot.XLabel("Время, с");
        TimePlot.Plot.YLabel("Амплитуда");
        SpectrumPlot.Plot.XLabel("Частота, Гц");
        SpectrumPlot.Plot.YLabel("Уровень, дБFS (ампл.)");
        TimePlot.Plot.ShowLegend(Alignment.UpperRight);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdatePlots();

        Opened += (_, _) =>
        {
            _timer.Start();
            UpdatePlots();
        };

        Closed += (_, _) => _timer.Stop();
    }

    private void UpdatePlots()
    {
        try
        {
            var demodulated = SourceBox.SelectedIndex == 1;
            var timeWindow = TimeWindowBox.Value is decimal value ? (double)value : 0.05;
            var fftSize = FftSizeBox.SelectedItem is int selected ? selected : 8192;

            var timePlot = TimePlot.Plot;
            var spectrumPlot = SpectrumPlot.Plot;
            timePlot.Clear();
            spectrumPlot.Clear();

            for (var index = 0; index < _buses.Count; index++)
            {
                var bus = _buses[index];
                var color = PlotTheme.Palette[index % PlotTheme.Palette.Length];
                var buffer = demodulated ? bus.DemodBuffer : bus.RawBuffer;

                if (bus.SampleRate <= 0)
                {
                    continue;
                }

                var timeFrames = Math.Max(16, (int)Math.Round(timeWindow * bus.SampleRate));
                var values = buffer.Snapshot(timeFrames, 0);
                var times = new double[timeFrames];

                for (var i = 0; i < timeFrames; i++)
                {
                    times[i] = (double)i / bus.SampleRate;
                }

                var signal = timePlot.Add.SignalXY(times, values, color);
                signal.LineWidth = 1.4f;
                signal.LegendText = bus.Name;

                var spectrumValues = buffer.Snapshot(fftSize, 0);
                var spectrum = FftProcessor.Compute(spectrumValues, bus.SampleRate, WindowKind.Hann);

                if (spectrum.Frequencies.Length > 1)
                {
                    var spectrumSignal = spectrumPlot.Add.SignalXY(
                        spectrum.Frequencies,
                        spectrum.MagnitudesDb,
                        color);
                    spectrumSignal.LineWidth = 1.2f;
                }
            }

            timePlot.Axes.AutoScale();
            spectrumPlot.Axes.AutoScale();
            TimePlot.Refresh();
            SpectrumPlot.Refresh();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Наложение: не удалось обновить графики: {ex.Message}");
        }
    }
}
