using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using VibroAnalyzer.Core.Analysis;
using VibroAnalyzer.Core.Routing;

namespace VibroAnalyzer.App;

/// <summary>
/// Каталог анализаторов по категориям. Открывает универсальные окна,
/// привязанные к выбранной шине.
/// </summary>
public partial class AnalyzerCatalogWindow : Window
{
    private readonly ISignalBus _bus;

    public AnalyzerCatalogWindow(ISignalBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;

        InitializeComponent();

        BoundText.Text = $"Шина: {bus.Name}";
        BuildCatalog();
    }

    private void BuildCatalog()
    {
        var orderedCategories = AnalyzerCatalog.CategoryDescriptions.Keys
            .Concat(AnalyzerCatalog.All.Select(descriptor => descriptor.Category))
            .Distinct()
            .ToList();

        foreach (var category in orderedCategories)
        {
            var analyzers = AnalyzerCatalog.All
                .Where(descriptor => descriptor.Category == category)
                .ToList();

            if (analyzers.Count == 0)
            {
                continue;
            }

            var content = new StackPanel { Spacing = 8 };

            var title = new TextBlock { Text = category.ToUpperInvariant() };
            title.Classes.Add("module-label");
            content.Children.Add(title);

            if (AnalyzerCatalog.CategoryDescriptions.TryGetValue(category, out var categoryDescription))
            {
                var description = new TextBlock { Text = categoryDescription };
                description.Classes.Add("hint");
                content.Children.Add(description);
            }

            foreach (var descriptor in analyzers)
            {
                content.Children.Add(BuildAnalyzerRow(descriptor));
            }

            var card = new Border { Child = content };
            card.Classes.Add("card");
            CategoriesPanel.Children.Add(card);
        }
    }

    private Border BuildAnalyzerRow(AnalyzerDescriptor descriptor)
    {
        var textPanel = new StackPanel { Spacing = 2 };

        var title = new TextBlock { Text = descriptor.Title };
        title.Classes.Add("route-title");

        var description = new TextBlock { Text = descriptor.Description };
        description.Classes.Add("route-subtitle");

        textPanel.Children.Add(title);
        textPanel.Children.Add(description);

        var open = new Button { Content = "Открыть", MinWidth = 100 };
        open.Classes.Add("small");
        open.Click += (_, _) => OpenAnalyzer(descriptor);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(textPanel, 0);
        Grid.SetColumn(open, 1);
        textPanel.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(textPanel);
        grid.Children.Add(open);

        var row = new Border { Child = grid };
        row.Classes.Add("route");
        return row;
    }

    private void OpenAnalyzer(AnalyzerDescriptor descriptor)
    {
        try
        {
            var analyzer = descriptor.Factory();
            _bus.AddAnalyzer(analyzer);

            var window = new AnalyzerWindow(analyzer, _bus.Name);
            window.Closed += (_, _) => _bus.RemoveAnalyzer(analyzer);
            window.Show();
        }
        catch (Exception)
        {
            // Ошибка создания анализатора не должна закрывать каталог.
        }
    }
}
