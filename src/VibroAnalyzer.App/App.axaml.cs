using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VibroAnalyzer.Audio;
using VibroAnalyzer.Core.Engine;
using VibroAnalyzer.Core.Session;

namespace VibroAnalyzer.App;

public partial class App : Application
{
    private AudioEngine? _engine;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var session = SessionContext.FromArgs(desktop.Args ?? Array.Empty<string>());
            session.EnsureDirectories();

            var demo = HasFlag(desktop.Args ?? Array.Empty<string>(), "--demo");
            var backend = AudioBackendFactory.Create(demo ? "Демо" : null);
            _engine = new AudioEngine(backend, session);

            var openWindows = ReadOption(desktop.Args ?? Array.Empty<string>(), "--open");
            var autoStart = HasFlag(desktop.Args ?? Array.Empty<string>(), "--start");
            var replayPath = ReadOption(desktop.Args ?? Array.Empty<string>(), "--replay");

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow(_engine, session);
            desktop.MainWindow = mainWindow;

            if (demo || !string.IsNullOrWhiteSpace(openWindows) || autoStart
                || !string.IsNullOrWhiteSpace(replayPath))
            {
                mainWindow.Opened += (_, _) =>
                {
                    if (demo)
                    {
                        mainWindow.ConfigureDemoRouting();
                    }

                    if (!string.IsNullOrWhiteSpace(openWindows))
                    {
                        mainWindow.OpenAnalyzerWindows(openWindows);
                    }

                    if (!string.IsNullOrWhiteSpace(replayPath))
                    {
                        mainWindow.OpenReplay(replayPath);
                    }

                    if (autoStart)
                    {
                        mainWindow.StartMeasurement();
                    }
                };
            }

            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument.Equals(name, StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                return args[index + 1];
            }

            var prefix = name + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _engine?.Dispose();
        _engine = null;
    }
}
