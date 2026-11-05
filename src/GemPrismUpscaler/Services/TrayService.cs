using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Platform.Linux;
using System;

namespace GemPrismUpscaler.Services;

/// <summary>
/// Optional system-tray integration. The service is created only when a tray
/// host exists; every failure falls back to "no tray, close exits", so the app
/// remains fully usable on GNOME without AppIndicator or other desktops that do
/// not expose StatusNotifierItem/XEmbed.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TrayProbeResult _probe;
    private TrayIcon? _tray;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _startItem;
    private NativeMenuItem? _stopItem;
    private NativeMenuItem? _toggleItem;

    public TrayService()
    {
        _probe = TrayProbe.Probe();
    }

    public TrayAvailability Availability => _probe.Availability;
    public string Reason => _probe.Reason;
    public bool IsCreated => _tray is not null;
    public bool CloseToTrayAllowed => _tray is not null && _probe.Availability == TrayAvailability.Available;

    public event Action? ShowRequested;
    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;

    public bool Initialize(Application app, bool enabled)
    {
        if (!enabled) return false;
        if (_probe.Availability == TrayAvailability.Unavailable)
        {
            Log.Info($"tray disabled: {_probe.Reason}");
            return false;
        }

        try
        {
            var menu = new NativeMenu();
            _statusItem = new NativeMenuItem("宝石棱镜 · 已停止") { IsEnabled = false };
            _startItem = new NativeMenuItem("开始超分");
            _stopItem = new NativeMenuItem("停止超分") { IsEnabled = false };
            _toggleItem = new NativeMenuItem("显示/隐藏覆盖层") { IsEnabled = false };
            var showItem = new NativeMenuItem("显示设置窗口");
            var exitItem = new NativeMenuItem("退出");

            showItem.Click += (_, _) =>
            {
                Log.Info("tray menu: show settings clicked");
                ShowRequested?.Invoke();
            };
            _startItem.Click += (_, _) =>
            {
                Log.Info("tray menu: start clicked");
                StartRequested?.Invoke();
            };
            _stopItem.Click += (_, _) =>
            {
                Log.Info("tray menu: stop clicked");
                StopRequested?.Invoke();
            };
            _toggleItem.Click += (_, _) =>
            {
                Log.Info("tray menu: toggle overlay clicked");
                ToggleRequested?.Invoke();
            };
            exitItem.Click += (_, _) =>
            {
                Log.Info("tray menu: exit clicked");
                ExitRequested?.Invoke();
            };

            menu.Items.Add(_statusItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(showItem);
            menu.Items.Add(_startItem);
            menu.Items.Add(_stopItem);
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            WindowIcon? icon = null;
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://GemPrismUpscaler/Assets/gemprism.png"));
                icon = new WindowIcon(stream);
            }
            catch (Exception ex)
            {
                Log.Warn($"tray icon asset missing: {ex.Message}");
            }

            _tray = new TrayIcon
            {
                ToolTipText = "宝石棱镜 GemPrismUpscaler",
                Menu = menu,
                IsVisible = true,
                Icon = icon
            };
            _tray.Clicked += (_, _) => ShowRequested?.Invoke();

            var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
            icons.Add(_tray);
            TrayIcon.SetIcons(app, icons);

            Log.Info($"tray created ({_probe.Reason})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("tray creation failed", ex);
            ErrorBus.Warning("tray", "托盘创建失败，已禁用", ex);
            Dispose();
            return false;
        }
    }

    public void UpdateState(bool sessionActive, bool canStart, bool overlayVisible)
    {
        if (_tray is null) return;
        if (_statusItem is not null)
            _statusItem.Header = sessionActive ? "宝石棱镜 · 正在超分" : "宝石棱镜 · 已停止";
        if (_startItem is not null) _startItem.IsEnabled = canStart && !sessionActive;
        if (_stopItem is not null) _stopItem.IsEnabled = sessionActive;
        if (_toggleItem is not null)
        {
            _toggleItem.IsEnabled = sessionActive;
            _toggleItem.Header = overlayVisible ? "隐藏覆盖层" : "显示覆盖层";
        }
    }

    public void SetErrorState(bool hasErrors)
    {
        if (_tray is null) return;
        _tray.ToolTipText = hasErrors
            ? "宝石棱镜 GemPrismUpscaler · 有错误"
            : "宝石棱镜 GemPrismUpscaler";
    }

    public void Dispose()
    {
        if (_tray is null) return;
        try
        {
            var app = Application.Current;
            if (app is not null)
                TrayIcon.GetIcons(app)?.Remove(_tray);
            _tray.Dispose();
        }
        catch
        {
            // Best effort on shutdown.
        }
        _tray = null;
    }
}
