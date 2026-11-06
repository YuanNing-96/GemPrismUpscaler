using Avalonia;
using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Engine;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using GemPrismUpscaler.Core.Rendering;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace GemPrismUpscaler;

sealed class Program
{
    internal static SingleInstance? InstanceGuard { get; private set; }
    internal static InstanceInfo? ExistingInstance { get; private set; }
    internal static AppConfig Config { get; private set; } = AppConfig.CreateDefault();

    private static bool _configLoaded;

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Error("unhandled exception", ex);
                ErrorBus.Functional("app", "程序出现未处理异常", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task exception", e.Exception);
            ErrorBus.Warning("app", "后台任务出现未观察异常", e.Exception);
            e.SetObserved();
        };

        if (args.Length > 0 && args[0] == "--status")
        {
            Environment.Exit(RunStatus());
            return;
        }

        if (args.Length > 0 && (args[0] == "--list" || args[0] == "--window" ||
                                args[0] == "--help" || args[0] == "--version"))
        {
            Environment.Exit(RunConsole(args));
            return;
        }

        EnsureConfiguration(args);

        // GUI launch. A second GUI instance must only report an error or, when
        // configured, ask the existing instance to come forward.
        if (!SingleInstance.TryAcquire(InstanceMode.Gui, out var guard, out var existing))
        {
            if (Config.General.SecondInstance == "activate" && SingleInstance.TryActivate())
            {
                Log.Info("second GUI activated the existing instance");
                return;
            }

            ExistingInstance = existing;
            ReportExistingInstance(existing, gui: true);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        InstanceGuard = guard;
        if (guard is not null)
        {
            guard.StatusProvider = () => new InstanceInfo(
                Environment.ProcessId, "gui", guard.StartedAt, false, null);
        }

        Log.Write($"app starting pid={Environment.ProcessId} mode=gui version={AppInfo.Version}");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.Write($"app exiting pid={Environment.ProcessId}");
            guard?.Dispose();
            InstanceGuard = null;
        }
    }

    private static int RunStatus()
    {
        try
        {
            var info = SingleInstance.TryQuery();
            if (info is null)
            {
                Console.WriteLine("not running");
                return 1;
            }

            Console.WriteLine($"running pid={info.Pid} mode={info.Mode} session={(info.SessionActive ? 1 : 0)} " +
                              $"monitor={info.Monitor ?? "-"} started={info.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"status failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunConsole(string[] args)
    {
        try
        {
            if (args[0] == "--version")
            {
                Console.WriteLine(AppInfo.DisplayName);
                return 0;
            }

            if (args[0] == "--help")
            {
                PrintUsage();
                return 0;
            }

            using var display = new X11Display();
            if (args[0] == "--list")
            {
                Console.WriteLine("monitors:");
                foreach (var m in X11MonitorEnumerator.List(display))
                    Console.WriteLine($"  {m.Name} {m.Width}x{m.Height}+{m.X}+{m.Y}" +
                                      $"{(m.Primary ? " primary" : string.Empty)}");
                Console.WriteLine("windows:");
                foreach (var w in X11WindowEnumerator.List(display))
                    Console.WriteLine($"{w.WindowId} 0x{w.WindowId:x} {w.Width}x{w.Height} pid={w.Pid} " +
                                      $"monitor={(string.IsNullOrWhiteSpace(w.MonitorName) ? "-" : w.MonitorName)} " +
                                      $"'{w.Title}' [{w.ClassName}]");
                return 0;
            }

            if (args.Length < 2)
            {
                PrintUsage();
                return 2;
            }

            EnsureConfiguration(args);

            ulong id = ParseId(args[1]);
            var info = X11WindowEnumerator.List(display).FirstOrDefault(w => w.WindowId == id);
            if (info is null)
            {
                Console.Error.WriteLine($"window 0x{id:x} not found");
                return 3;
            }

            string? monitorName = GetOption(args, "--monitor");
            if (string.IsNullOrWhiteSpace(monitorName) &&
                !string.Equals(Config.Display.Monitor, "auto", StringComparison.OrdinalIgnoreCase))
                monitorName = Config.Display.Monitor;
            if (!string.IsNullOrWhiteSpace(monitorName))
            {
                var monitor = X11MonitorEnumerator.List(display)
                    .FirstOrDefault(m => string.Equals(m.Name, monitorName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(monitor.Name))
                {
                    Console.Error.WriteLine($"monitor '{monitorName}' not found; use --list to see names");
                    return 5;
                }
                monitorName = monitor.Name;
            }

            if (!SingleInstance.TryAcquire(InstanceMode.Cli, out var guard, out var existing))
            {
                ReportExistingInstance(existing, gui: false);
                return 4;
            }

            using (guard)
            {
                UpscaleSession? session = null;
                if (guard is not null)
                {
                    guard.StatusProvider = () => new InstanceInfo(
                        Environment.ProcessId, "cli", guard.StartedAt,
                        session?.IsRunning == true, session?.MonitorName);
                }

                var mode = args.Any(a => a == "--stretch") ||
                           string.Equals(Config.Effect.Scaling, "stretch", StringComparison.OrdinalIgnoreCase)
                    ? ScalingMode.Stretch
                    : ScalingMode.Fit;
                if (!TryEffectFromArgs(args, out var effect, out string? effectError))
                {
                    Console.Error.WriteLine(effectError);
                    return 2;
                }

                int outputWidth = 0, outputHeight = 0;
                string? outputSize = GetOption(args, "--output-size");
                if (!string.IsNullOrWhiteSpace(outputSize) &&
                    !TryParseSize(outputSize, out outputWidth, out outputHeight))
                {
                    Console.Error.WriteLine($"invalid --output-size '{outputSize}'; expected WxH");
                    return 2;
                }

                var hotkey = HotkeyBinding.Default;
                if (!HotkeyBinding.TryParse(Config.Hotkey.ToggleOverlay, out hotkey, out string? hotkeyError))
                {
                    Log.Warn($"config hotkey invalid ({hotkeyError}); using {HotkeyBinding.Default}");
                    hotkey = HotkeyBinding.Default;
                }

                var overlayBackend = ResolveOverlayBackend(args);
                var pointerPassthrough = ResolvePointerPassthrough(args);
                string captureMode = GetOption(args, "--capture") ?? Config.Capture.Mode;
                if (captureMode is not ("auto" or "x11" or "portal"))
                {
                    Console.Error.WriteLine($"invalid --capture '{captureMode}'; expected auto|x11|portal");
                    return 2;
                }
                if (captureMode == "portal")
                    Console.Error.WriteLine("portal capture is not available in this build yet; using X11 capture");

                session = new UpscaleSession(info, mode, effect.Mode, monitorName, outputWidth, outputHeight,
                    hotkey, effect, overlayBackend, pointerPassthrough)
                {
                    FollowFocus = !args.Any(a => a == "--no-follow-focus") && Config.General.FollowFocus,
                    DirtyRectEnabled = !args.Any(a => a == "--no-dirty-rect") && Config.Effect.DirtyRect
                };
                session.Message += m => Console.WriteLine(m);

                using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    session.Stop();
                });
                using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                {
                    ctx.Cancel = true;
                    session.Stop();
                });

                try
                {
                    session.Start();
                    Console.WriteLine($"running; backend={(overlayBackend == OverlayBackend.Wayland ? "wayland" : "x11")}; " +
                                      $"overlay={session.OverlayDescription}; monitor={session.MonitorName}; Ctrl+C to stop");
                    while (session.IsRunning) Thread.Sleep(200);
                    Console.WriteLine($"stopped: {session.ExitReason}");
                }
                finally
                {
                    session.Dispose();
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"fatal: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static OverlayBackend ResolveOverlayBackend(string[] args)
    {
        string requested = GetOption(args, "--backend") ?? Config.Capture.Backend;
        bool waylandSession = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        bool x11 = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));

        if (requested is not ("auto" or "x11" or "wayland"))
        {
            Console.Error.WriteLine($"invalid --backend '{requested}'; expected auto|x11|wayland");
            return OverlayBackend.X11;
        }
        if (requested == "wayland")
            return OverlayBackend.Wayland;
        if (requested == "x11")
            return OverlayBackend.X11;
        return waylandSession && x11 ? OverlayBackend.Wayland : OverlayBackend.X11;
    }

    private static bool ResolvePointerPassthrough(string[] args)
    {
        string mode = GetOption(args, "--input") ?? Config.Input.PointerMode;
        return mode switch
        {
            "passthrough" => true,
            "forward" => false,
            "auto" => false,
            _ => false
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            usage:
              GemPrismUpscaler                          launch the configuration UI
              GemPrismUpscaler --list                   list capturable windows and monitors
              GemPrismUpscaler --window <id> [options]  start a session directly
              GemPrismUpscaler --status                 query the running instance
              GemPrismUpscaler --version                print version
              GemPrismUpscaler --help                   this text

            global options:
              --dev                     force developer mode (debug logs, diagnostics)

            options for --window:
              --monitor <name>          output monitor (auto follows the game by default)
              --effect anime4k-s|anime4k-m|anime4k-l|anime4k-vl|bilinear
                                        default anime4k-s; 'anime4k' is an alias for s
              --stretch                 stretch instead of preserving aspect ratio
              --no-follow-focus         keep the overlay when the game loses focus
              --no-dirty-rect           disable dirty-tile rendering (A/B baseline)
              --output-size <WxH>       debug/benchmark output override (e.g. 3840x2160)
              --backend auto|x11|wayland
                                        presentation backend (auto by session)
              --capture auto|x11|portal
                                        capture backend (portal is native Wayland, M2a)
              --input auto|passthrough|forward
                                        Wayland pointer handling (forward = XSendEvent)
            """);
    }

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool TryParseSize(string value, out int width, out int height)
    {
        width = 0;
        height = 0;
        string[] parts = value.Split('x', 'X');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)) return false;
        return width is >= 320 and <= 16384 && height is >= 240 and <= 16384;
    }

    private static void EnsureConfiguration(string[] args)
    {
        if (_configLoaded) return;
        _configLoaded = true;

        var result = ConfigStore.Load();
        Config = result.Config;
        if (args.Any(a => a == "--dev"))
            Config.Diagnostics.DeveloperMode = true;

        Log.Configure(Config.Logging.Path, Config.Logging.Level);
        if (Config.Diagnostics.DeveloperMode) Log.Level = LogLevel.Debug;
        DiagnosticsRuntime.Configure(Config);

        foreach (string warning in result.Warnings)
            Log.Warn($"config: {warning}");
        if (!string.IsNullOrWhiteSpace(result.Error))
            ErrorBus.Functional("config", result.Error);

        Log.Info($"config loaded path={result.Path} schema={Config.Schema} " +
                 $"profiles={Config.Profile.Count} fileExisted={result.FileExisted} " +
                 $"recovered={result.RecoveredFromError} " + DiagnosticsRuntime.Describe());
    }

    private static bool TryEffectFromArgs(string[] args, out EffectDefinition effect, out string? error)
    {
        effect = EffectRegistry.Resolve(Config.Effect.Default);
        if (!effect.IsAvailable)
        {
            Log.Warn($"config effect.default='{Config.Effect.Default}' unavailable ({effect.UnavailableReason}); using anime4k-s");
            effect = EffectRegistry.BuiltIn(EffectMode.Anime4KS);
        }

        error = null;
        int index = Array.IndexOf(args, "--effect");
        if (index < 0) return true;
        if (index + 1 >= args.Length)
        {
            error = "--effect requires a value (built-in id or a custom effect id)";
            return false;
        }

        string value = args[index + 1];
        var found = EffectRegistry.Find(value);
        if (found is null)
        {
            string available = string.Join(", ", EffectRegistry.All.Select(e => e.Id));
            error = $"unknown effect '{value}'; available: {available}";
            return false;
        }
        if (!found.IsAvailable)
        {
            error = $"effect '{value}' is unavailable: {found.UnavailableReason}";
            return false;
        }

        effect = found;
        return true;
    }

    private static ulong ParseId(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ulong.Parse(s, CultureInfo.InvariantCulture);
    }

    private static void ReportExistingInstance(InstanceInfo? info, bool gui)
    {
        string message = info is null
            ? "另一个 GemPrismUpscaler 实例正在运行，但状态读取失败。"
            : $"已有 GemPrismUpscaler 实例在运行：{info.DisplayText}。\n" +
              "本次启动不会创建第二个覆盖层，也不会激活已有窗口。";

        Console.Error.WriteLine(message.Replace('\n', ' '));
        if (!gui) return;

        // A running overlay is override-redirect and may cover a normal dialog.
        // A desktop notification guarantees the user sees why nothing started.
        try
        {
            if (Environment.GetEnvironmentVariable("DISPLAY") is not null ||
                Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null)
            {
                var psi = new ProcessStartInfo("notify-send")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add("GemPrismUpscaler");
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add("normal");
                psi.ArgumentList.Add("GemPrismUpscaler 已在运行");
                psi.ArgumentList.Add(message);
                using var process = Process.Start(psi);
                process?.WaitForExit(400);
            }
        }
        catch
        {
            // notify-send is optional; the error window remains.
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null)
            builder = builder.UseWayland();

        return builder;
    }
}
