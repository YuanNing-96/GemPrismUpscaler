using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Platform.Linux.Portal;
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
            // KDE uses the desktop entry for the taskbar icon. Create/replace
            // it even before a portal feature is used, so the icon is the same
            // on a fresh machine as it is in the tray.
            PortalAppId.EnsureDesktopFile();
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log.Error("UI thread unhandled exception", e.Exception);
                ErrorBus.Functional("ui", "界面出现未处理异常", e.Exception);
                e.Handled = true;
            };

            if (Program.ExistingInstance is { } existing)
            {
                desktop.MainWindow = new AlreadyRunningWindow(existing);
            }
            else
            {
                desktop.MainWindow = new MainWindow(Program.InstanceGuard, Program.Config);
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
