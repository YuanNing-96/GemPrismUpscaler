using System.Diagnostics;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Platform.Linux.X11;

namespace GemPrismUpscaler.Core.Platform.Linux;

public enum TrayAvailability
{
    Available,
    Unavailable,
    Unknown
}

public readonly record struct TrayProbeResult(TrayAvailability Availability, string Reason);

/// <summary>
/// Best-effort tray probe. Linux desktops expose either a StatusNotifierItem
/// watcher (KDE, XFCE, GNOME+AppIndicator, ...) or the legacy XEmbed tray.
/// A failed probe never blocks startup; it only disables close-to-tray.
/// </summary>
public static class TrayProbe
{
    public static TrayProbeResult Probe()
    {
        if (!OperatingSystem.IsLinux())
            return new TrayProbeResult(TrayAvailability.Available, "非 Linux 平台");

        bool wayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        bool x11 = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));

        bool? sni = ProbeStatusNotifier();
        if (sni == true)
            return new TrayProbeResult(TrayAvailability.Available, "StatusNotifierItem/AppIndicator");
        if (x11 && HasXEmbedTray())
            return new TrayProbeResult(TrayAvailability.Available, "XEmbed 系统托盘");
        if (sni == false && wayland)
            return new TrayProbeResult(TrayAvailability.Unavailable,
                "未检测到 StatusNotifierItem/AppIndicator 宿主");
        if (x11)
            return new TrayProbeResult(TrayAvailability.Unavailable,
                "未检测到 StatusNotifierItem 或 XEmbed 托盘");
        if (wayland)
            return new TrayProbeResult(TrayAvailability.Unknown,
                "无法探测托盘（gdbus 不可用）");
        return new TrayProbeResult(TrayAvailability.Unavailable, "没有可用的图形会话");
    }

    private static bool? ProbeStatusNotifier()
    {
        try
        {
            var psi = new ProcessStartInfo("gdbus")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("introspect");
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add("--dest");
            psi.ArgumentList.Add("org.kde.StatusNotifierWatcher");
            psi.ArgumentList.Add("--object-path");
            psi.ArgumentList.Add("/StatusNotifierWatcher");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(700))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string output = read.GetAwaiter().GetResult();
            if (output.Contains("IsStatusNotifierHostRegistered", StringComparison.Ordinal))
            {
                if (output.Contains("IsStatusNotifierHostRegistered = true", StringComparison.Ordinal))
                    return true;
                return false;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasXEmbedTray()
    {
        try
        {
            using var display = new X11Display();
            ulong atom = X11Native.XInternAtom(display.Handle, "_NET_SYSTEM_TRAY_S0", 1);
            return atom != 0 && X11Native.XGetSelectionOwner(display.Handle, atom) != 0;
        }
        catch (Exception ex)
        {
            Log.Debug($"tray probe: XEmbed check failed: {ex.Message}");
            return false;
        }
    }
}
