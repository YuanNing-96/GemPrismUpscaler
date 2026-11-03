using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Views;
using System;
using System.Runtime.InteropServices;

namespace GemPrismUpscaler;

public partial class App : Application
{
    private PosixSignalRegistration? _sigterm;
    private PosixSignalRegistration? _sigint;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.ExistingInstance is { } existing)
            {
                desktop.MainWindow = new AlreadyRunningWindow(existing);
            }
            else
            {
                desktop.MainWindow = new MainWindow(Program.InstanceGuard);
            }

            desktop.ShutdownRequested += (_, _) => Log.Write("app shutdown requested");
            desktop.Exit += (_, _) =>
            {
                Log.Write($"app exit pid={Environment.ProcessId}");
                DisposeSignals();
            };

            if (OperatingSystem.IsLinux())
            {
                _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
                _sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
            }

            Log.Write($"app started pid={Environment.ProcessId} " +
                      (Program.ExistingInstance is { } info
                          ? $"second-instance pid={info.Pid} mode={info.Mode}"
                          : "mode=gui"));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Log.Write($"received {context.Signal}");
        Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
    }

    private void DisposeSignals()
    {
        _sigterm?.Dispose();
        _sigint?.Dispose();
        _sigterm = null;
        _sigint = null;
    }
}
