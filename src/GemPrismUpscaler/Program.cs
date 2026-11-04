using Avalonia;
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

    [STAThread]
    public static void Main(string[] args)
    {
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

        // GUI launch. A second GUI instance must only report an error; it must
        // never activate or disturb the existing window (that is a later setting).
        if (!SingleInstance.TryAcquire(InstanceMode.Gui, out var guard, out var existing))
        {
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

            ulong id = ParseId(args[1]);
            var info = X11WindowEnumerator.List(display).FirstOrDefault(w => w.WindowId == id);
            if (info is null)
            {
                Console.Error.WriteLine($"window 0x{id:x} not found");
                return 3;
            }

            string? monitorName = GetOption(args, "--monitor");
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

                var mode = args.Any(a => a == "--stretch") ? ScalingMode.Stretch : ScalingMode.Fit;
                if (!TryEffectFromArgs(args, out var effect, out string? effectError))
                {
                    Console.Error.WriteLine(effectError);
                    return 2;
                }
                session = new UpscaleSession(info, mode, effect, monitorName)
                {
                    FollowFocus = !args.Any(a => a == "--no-follow-focus")
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
                    Console.WriteLine($"running; overlay=0x{session.OverlayWindowId:x}; monitor={session.MonitorName}; Ctrl+C to stop");
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

            options for --window:
              --monitor <name>          output monitor (auto follows the game by default)
              --effect anime4k-s|anime4k-m|anime4k-l|bilinear
                                        default anime4k-s; 'anime4k' is an alias for s
              --stretch                 stretch instead of preserving aspect ratio
              --no-follow-focus         keep the overlay when the game loses focus
            """);
    }

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool TryEffectFromArgs(string[] args, out EffectMode effect, out string? error)
    {
        effect = EffectMode.Anime4KS;
        error = null;
        int index = Array.IndexOf(args, "--effect");
        if (index < 0) return true;
        if (index + 1 >= args.Length)
        {
            error = "--effect requires a value (anime4k-s, anime4k-m, anime4k-l, bilinear)";
            return false;
        }

        string value = args[index + 1];
        if (!EffectModeExtensions.TryParse(value, out effect))
        {
            error = $"unknown effect '{value}'; use anime4k-s, anime4k-m, anime4k-l or bilinear";
            return false;
        }
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
