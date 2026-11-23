using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VibroAnalyzer.Core.Analysis;

namespace VibroAnalyzer.App;

/// <summary>
/// Справка анализатора открывается по клику в отдельном окне.
/// Содержимое разбито на разделы, формулы выводятся построчно.
/// </summary>
internal static class HelpUi
{
    private static readonly IBrush Ink0 = new SolidColorBrush(Color.Parse("#F2F6FA"));
    private static readonly IBrush Ink1 = new SolidColorBrush(Color.Parse("#B7C3CF"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#49E8B2"));
    private static readonly FontFamily MonoFont =
        new("Cascadia Mono, Consolas, Menlo, Liberation Mono, DejaVu Sans Mono");

    public static void Attach(Button button, AnalyzerHelp help, string? title = null)
    {
        button.Click += (_, _) =>
        {
            var window = new HelpWindow(help, title);
            var owner = TopLevel.GetTopLevel(button) as Window;

            if (owner is not null)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        };
    }

    internal static Control CreateContent(AnalyzerHelp help)
    {
        var content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 700,
        };

        AddSection(content, "НАЗНАЧЕНИЕ", help.Purpose, mono: false);
        AddFormulaSection(content, help.Formulas);
        AddSection(content, "НЮАНСЫ", help.Details, mono: false);
        AddSection(content, "КАК ИСПОЛЬЗОВАТЬ", help.Usage, mono: false);
        AddSection(content, "ОГРАНИЧЕНИЯ", help.Limitations, mono: false);

        return content;
    }

    private static void AddSection(StackPanel panel, string title, string text, bool mono)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 1,
            Foreground = Accent,
            Margin = new Thickness(0, 4, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = text.Trim(),
            FontSize = 13,
            Foreground = Ink1,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontFamily = mono ? MonoFont : FontFamily.Default,
        });
    }

    private static void AddFormulaSection(StackPanel panel, string formulas)
    {
        if (string.IsNullOrWhiteSpace(formulas))
        {
            return;
        }

        panel.Children.Add(new TextBlock
        {
            Text = "ФОРМУЛЫ И ОБОЗНАЧЕНИЯ",
            FontSize = 10.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 1,
            Foreground = Accent,
            Margin = new Thickness(0, 4, 0, 0),
        });

        foreach (var formula in SplitFormulas(formulas))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "•  " + formula,
                FontFamily = MonoFont,
                FontSize = 12.5,
                Foreground = Ink0,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                Margin = new Thickness(4, 0, 0, 0),
            });
        }
    }

    private static IEnumerable<string> SplitFormulas(string formulas)
    {
        foreach (var part in formulas.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }
}
