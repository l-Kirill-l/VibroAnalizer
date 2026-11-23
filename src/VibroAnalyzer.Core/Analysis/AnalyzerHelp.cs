using System.Text;

namespace VibroAnalyzer.Core.Analysis;

/// <summary>
/// Подробная справка по анализатору: назначение, формулы, допущения и ограничения.
/// </summary>
public sealed record AnalyzerHelp(
    string Purpose,
    string Formulas,
    string Details,
    string Usage,
    string Limitations)
{
    public string ToDisplayText()
    {
        var builder = new StringBuilder();

        Append(builder, "Назначение", Purpose);
        Append(builder, "Формулы", Formulas);
        Append(builder, "Нюансы", Details);
        Append(builder, "Как использовать", Usage);
        Append(builder, "Ограничения", Limitations);

        return builder.ToString().TrimEnd();
    }

    private static void Append(StringBuilder builder, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(title).AppendLine(":");
        builder.AppendLine(text.Trim());
    }
}
