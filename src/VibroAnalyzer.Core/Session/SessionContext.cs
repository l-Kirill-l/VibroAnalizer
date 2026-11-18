namespace VibroAnalyzer.Core.Session;

/// <summary>
/// Контекст запуска модуля: общий каталог сессии, идентификатор модуля
/// и каталог модуля внутри сессии.
/// </summary>
public sealed class SessionContext
{
    private const string SessionDirectoryOption = "--session-dir";
    private const string ModuleIdOption = "--module-id";
    private const string DefaultSharedDirectoryName = "SharedSessionData";

    /// <summary>
    /// Идентификатор аппаратного модуля по умолчанию.
    /// </summary>
    private const string DefaultModuleId = "hw_vibro_analyzer";

    private SessionContext(string sessionDirectory, string moduleId, bool isStandalone)
    {
        SessionDirectory = sessionDirectory;
        ModuleId = moduleId;
        IsStandalone = isStandalone;
        ModuleDirectory = Path.Combine(sessionDirectory, moduleId);
    }

    /// <summary>
    /// Общий каталог сессии (обычно SharedSessionData).
    /// </summary>
    public string SessionDirectory { get; }

    /// <summary>
    /// Идентификатор модуля.
    /// </summary>
    public string ModuleId { get; }

    /// <summary>
    /// Признак самостоятельного запуска, когда каталог сессии не передан.
    /// </summary>
    public bool IsStandalone { get; }

    /// <summary>
    /// Каталог модуля внутри общего каталога сессии.
    /// </summary>
    public string ModuleDirectory { get; }

    /// <summary>
    /// Разбирает аргументы командной строки и создаёт контекст сессии.
    /// Поддерживаются формы --session-dir=value и --session-dir value,
    /// а также --module-id=value и --module-id value.
    /// </summary>
    public static SessionContext FromArgs(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? sessionDirectory = null;
        string? moduleId = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (TryReadValue(argument, SessionDirectoryOption, args, ref index, out var sessionValue))
            {
                sessionDirectory = sessionValue;
            }
            else if (TryReadValue(argument, ModuleIdOption, args, ref index, out var moduleValue))
            {
                moduleId = moduleValue;
            }
        }

        var isStandalone = string.IsNullOrWhiteSpace(sessionDirectory);
        var resolvedDirectory = isStandalone
            ? Path.Combine(Environment.CurrentDirectory, DefaultSharedDirectoryName)
            : sessionDirectory!;

        var resolvedModuleId = string.IsNullOrWhiteSpace(moduleId)
            ? DefaultModuleId
            : moduleId!;

        return new SessionContext(resolvedDirectory, resolvedModuleId, isStandalone);
    }

    /// <summary>
    /// Создаёт общий каталог сессии и каталог модуля.
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SessionDirectory);
        Directory.CreateDirectory(ModuleDirectory);
    }

    private static bool TryReadValue(
        string argument,
        string optionName,
        string[] args,
        ref int index,
        out string? value)
    {
        value = null;

        if (argument.Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 < args.Length)
            {
                var candidate = args[index + 1];
                if (!string.IsNullOrEmpty(candidate)
                    && !candidate.StartsWith("--", StringComparison.Ordinal))
                {
                    index++;
                    value = NormalizeValue(candidate);
                }
            }

            return true;
        }

        var prefix = optionName + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = NormalizeValue(argument[prefix.Length..]);
            return true;
        }

        return false;
    }

    private static string? NormalizeValue(string value)
    {
        var result = value.Trim();

        if (result.Length >= 2)
        {
            var quote = result[0];
            if ((quote == '"' || quote == '\'') && result[^1] == quote)
            {
                result = result[1..^1].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
