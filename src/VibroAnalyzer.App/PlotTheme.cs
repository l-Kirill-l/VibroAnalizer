using ScottPlot;

namespace VibroAnalyzer.App;

/// <summary>
/// Единое оформление графиков в стиле VWL: тёмный экран, мятный луч,
/// приглушённая сетка и подписи цветом чернил.
/// </summary>
internal static class PlotTheme
{
    public static readonly Color Screen = Color.FromHex("#0D1218");
    public static readonly Color Ink0 = Color.FromHex("#F2F6FA");
    public static readonly Color Ink1 = Color.FromHex("#B7C3CF");
    public static readonly Color Ink2 = Color.FromHex("#798793");
    public static readonly Color Accent = Color.FromHex("#49E8B2");
    public static readonly Color AccentBlue = Color.FromHex("#3F8CE8");
    public static readonly Color AccentOrange = Color.FromHex("#E06A33");
    public static readonly Color GridMajor = Color.FromHex("#1E2A35");
    public static readonly Color GridMinor = Color.FromHex("#131C25");
    public static readonly Color Frame = Color.FromHex("#2AAAC8DC");

    /// <summary>Палитра для наложения нескольких датчиков.</summary>
    public static readonly Color[] Palette =
    {
        Accent,
        AccentBlue,
        AccentOrange,
        Color.FromHex("#B48EAD"),
        Color.FromHex("#E5C07B"),
        Color.FromHex("#56B6C2"),
        Color.FromHex("#98C379"),
        Color.FromHex("#E06C75"),
    };

    public static void Apply(Plot plot)
    {
        plot.FigureBackground.Color = Screen;
        plot.DataBackground.Color = Screen;

        plot.Grid.MajorLineColor = GridMajor;
        plot.Grid.MinorLineColor = GridMinor;

        plot.Axes.Color(Ink1);
        plot.Axes.FrameColor(Frame);

        plot.Axes.Title.Label.ForeColor = Ink0;
        plot.Axes.Left.Label.ForeColor = Ink1;
        plot.Axes.Bottom.Label.ForeColor = Ink1;
        plot.Axes.Right.Label.ForeColor = Ink1;
        plot.Axes.Top.Label.ForeColor = Ink1;

        plot.Axes.Left.TickLabelStyle.ForeColor = Ink2;
        plot.Axes.Bottom.TickLabelStyle.ForeColor = Ink2;
        plot.Axes.Right.TickLabelStyle.ForeColor = Ink2;
        plot.Axes.Top.TickLabelStyle.ForeColor = Ink2;

        plot.Legend.BackgroundColor = Color.FromHex("#F21B232C");
        plot.Legend.FontColor = Ink1;
        plot.Legend.OutlineColor = Color.FromHex("#42BED7EB");
        plot.Legend.OutlineWidth = 1;
        plot.Legend.FontSize = 12;
        plot.Legend.ShadowColor = Color.FromHex("#00000000");
    }
}
