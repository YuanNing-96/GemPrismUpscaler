using Avalonia;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Engine;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using GemPrismUpscaler.Core.Rendering;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace GemPrismUpscaler;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--list" || args[0] == "--window" ||
                                args[0] == "--help" || args[0] == "--version"))
        {
            Environment.Exit(RunConsole(args));
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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

            using var display = new X11Display();
            if (args[0] == "--list" || args[0] == "--help")
            {
                foreach (var w in X11WindowEnumerator.List(display))
                    Console.WriteLine($"{w.WindowId} 0x{w.WindowId:x} {w.Width}x{w.Height} pid={w.Pid} '{w.Title}' [{w.ClassName}]");
                return 0;
            }

            if (args.Length < 2) { Console.Error.WriteLine("usage: --window <id> [--stretch] [--effect anime4k|bilinear] [--no-follow-focus]"); return 2; }
            ulong id = ParseId(args[1]);
            var info = X11WindowEnumerator.List(display).FirstOrDefault(w => w.WindowId == id);
            if (info is null) { Console.Error.WriteLine($"window 0x{id:x} not found"); return 3; }
            var mode = args.Any(a => a == "--stretch") ? ScalingMode.Stretch : ScalingMode.Fit;
            var effect = EffectFromArgs(args);
            using var session = new UpscaleSession(info, mode, effect)
            {
                FollowFocus = !args.Any(a => a == "--no-follow-focus")
            };
            session.Message += m => Console.WriteLine(m);
            session.Start();
            Console.WriteLine($"running; overlay=0x{session.OverlayWindowId:x}; Ctrl+C to stop");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; session.Stop(); };
            while (session.IsRunning) Thread.Sleep(200);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"fatal: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static EffectMode EffectFromArgs(string[] args)
    {
        int index = Array.IndexOf(args, "--effect");
        if (index >= 0 && index + 1 < args.Length)
            return args[index + 1].Equals("bilinear", StringComparison.OrdinalIgnoreCase)
                ? EffectMode.Bilinear
                : EffectMode.Anime4K;
        return EffectMode.Anime4K;
    }

    private static ulong ParseId(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ulong.Parse(s, CultureInfo.InvariantCulture);
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
