using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using VibroAnalyzer.Core.Analysis;

namespace VibroAnalyzer.App;

/// <summary>Показывает подробную справку анализатора во всплывающей подсказке.</summary>
internal static class HelpUi
{
    public static void Attach(Button button, AnalyzerHelp help)
    {
        var text = new TextBlock
        {
            Text = help.ToDisplayText(),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 540,
        };

        text.Classes.Add("hint");

        var scroll = new ScrollViewer
        {
            MaxHeight = 460,
            MaxWidth = 580,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = text,
        };

        ToolTip.SetTip(button, scroll);
    }
}
