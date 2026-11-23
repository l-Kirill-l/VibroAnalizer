# Сборка под Windows

Проект кросс-платформенный: код PipeWire закрыт проверкой `OperatingSystem.IsLinux()`,
а WASAPI использует NAudio и компилируется в Windows-сборке.

## Framework-dependent `.exe` (нужен .NET 10 Desktop Runtime)

На Linux или Windows:

```bash
dotnet publish src/VibroAnalyzer.App/VibroAnalyzer.App.csproj \
  -c Release -r win-x64 --self-contained false \
  -o publish/win-x64
```

Результат: `publish/win-x64/VibroAnalyzer.App.exe` и зависимости.
На целевой машине должен быть установлен **.NET 10 Desktop Runtime**.

## Self-contained `.exe` (без установленного .NET)

```bash
dotnet publish src/VibroAnalyzer.App/VibroAnalyzer.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/win-x64-self-contained
```

Размер будет больше (включает рантайм), зато `.exe` запускается без установки
.NET. Для Windows-аудио используется WASAPI/NAudio; PipeWire-код в Windows
не активируется.

## Проверка

```bash
dotnet build VibroAnalyzer.slnx -c Release
dotnet test tests/VibroAnalyzer.Tests/VibroAnalyzer.Tests.csproj -c Release
```
