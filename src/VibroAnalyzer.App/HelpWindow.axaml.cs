using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VibroAnalyzer.Core.Analysis;

namespace VibroAnalyzer.App;

/// <summary>
/// Справка анализатора: отдельное окно с прокруткой, разделами и формулами.
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow(AnalyzerHelp help, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(help);

        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "Справка" : $"Справка — {title}";
        HeaderText.Text = string.IsNullOrWhiteSpace(title) ? "Справка" : title;
        ContentScroll.Content = HelpUi.CreateContent(help);
        CloseButton.Click += (_, _) => Close();
    }
}
