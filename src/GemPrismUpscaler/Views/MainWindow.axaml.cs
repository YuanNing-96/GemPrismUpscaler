using Avalonia.Controls;
using Avalonia.Threading;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Engine;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using GemPrismUpscaler.Core.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GemPrismUpscaler.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly SingleInstance? _instance;
    private UpscaleSession? _session;
    private List<WindowInfo> _windows = new();
    private List<MonitorInfo> _monitors = new();
    private string _runningTitle = string.Empty;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(SingleInstance? instance)
    {
        InitializeComponent();
        _instance = instance;
        if (_instance is not null)
            _instance.StatusProvider = BuildInstanceInfo;

        Title = $"宝石棱镜 {AppInfo.DisplayName}";
        RefreshButton.Click += (_, _) => RefreshWindows();
        StartButton.Click += (_, _) => StartSession();
        StopButton.Click += (_, _) => StopSession("已停止");
        ExitButton.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            RefreshWindows();
            _watch.Tick += (_, _) => WatchSession();
            _watch.Start();
        };
        Closing += (_, _) =>
        {
            AppendLog("window closing");
            StopSession("窗口关闭");
        };
        Closed += (_, _) => Log.Write($"ui closed pid={Environment.ProcessId}");
    }

    private InstanceInfo BuildInstanceInfo() => new(
        Environment.ProcessId,
        "gui",
        _instance?.StartedAt ?? DateTimeOffset.Now,
        _session?.IsRunning == true,
        _session?.MonitorName);

    private void RefreshWindows()
    {
        try
        {
            using var display = new X11Display();
            _monitors = X11MonitorEnumerator.List(display);
            PopulateMonitors();

            ulong? previousId = (WindowList.SelectedItem as WindowInfo)?.WindowId;
            _windows = X11WindowEnumerator.List(display)
                .Where(w => w.Pid != Environment.ProcessId)
                .OrderByDescending(w => w.Width * w.Height)
                .ToList();

            WindowList.ItemsSource = _windows;
            WindowList.SelectedItem = previousId is { } id
                ? _windows.FirstOrDefault(w => w.WindowId == id) ?? _windows.FirstOrDefault()
                : _windows.FirstOrDefault();

            StatusText.Text = $"发现 {_windows.Count} 个窗口 · {_monitors.Count} 台显示器";
            AppendLog($"refresh: {_windows.Count} windows, {_monitors.Count} monitors");
            foreach (var monitor in _monitors)
                AppendLog($"monitor: {monitor}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "X11 连接失败";
            AppendLog($"refresh error: {ex.Message}");
        }
    }

    private void PopulateMonitors()
    {
        string? previous = (MonitorCombo.SelectedItem as MonitorChoice)?.Name;
        var choices = new List<MonitorChoice>
        {
            new(null, "自动（跟随游戏窗口）")
        };
        choices.AddRange(_monitors.Select(m => new MonitorChoice(
            m.Name,
            $"{m.Name} · {m.Width}×{m.Height}{(m.Primary ? "（主屏）" : string.Empty)}")));

        MonitorCombo.ItemsSource = choices;
        MonitorCombo.SelectedItem = choices.FirstOrDefault(c => c.Name == previous) ?? choices[0];
    }

    private void StartSession()
    {
        if (WindowList.SelectedItem is not WindowInfo info)
        {
            StatusText.Text = "请先选择一个窗口";
            return;
        }

        StopSession("重新启动");
        var mode = StretchCheck.IsChecked == true ? ScalingMode.Stretch : ScalingMode.Fit;
        var effect = EffectCombo.SelectedIndex == 1 ? EffectMode.Bilinear : EffectMode.Anime4K;
        string? monitorName = (MonitorCombo.SelectedItem as MonitorChoice)?.Name;
        var session = new UpscaleSession(info, mode, effect, monitorName)
        {
            FollowFocus = FollowFocusCheck.IsChecked != false
        };
        session.Message += OnSessionMessage;
        try
        {
            session.Start();
            _session = session;
            _runningTitle = info.Title;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = $"运行中：{_runningTitle}（显示器：{session.MonitorName}，Alt+Shift+A 显示/隐藏）";
            AppendLog($"started: 0x{info.WindowId:x} '{info.Title}' {info.Width}x{info.Height} " +
                      $"mode={mode} effect={session.EffectName} monitor={session.MonitorName}");
            Log.Write($"ui session started pid={Environment.ProcessId} window=0x{info.WindowId:x} " +
                      $"monitor={session.MonitorName} effect={session.EffectName}");
        }
        catch (Exception ex)
        {
            session.Dispose();
            StatusText.Text = "启动失败";
            AppendLog($"start error: {ex.Message}");
        }
    }

    private void StopSession(string? status = null)
    {
        var session = _session;
        _session = null;
        _runningTitle = string.Empty;
        if (session is not null)
        {
            session.Message -= OnSessionMessage;
            AppendLog($"stopping session ({status ?? "requested"})");
            try { session.Stop(); } catch (Exception ex) { AppendLog($"stop error: {ex.Message}"); }
            session.Dispose();
        }
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        if (StatusText is not null) StatusText.Text = status ?? "已停止";
    }

    private void WatchSession()
    {
        var session = _session;
        if (session is null) return;

        if (session.IsRunning)
        {
            // Auto mode can move the overlay to another monitor; keep the status
            // line in sync without waiting for a message.
            string text = $"运行中：{_runningTitle}（显示器：{session.MonitorName}，Alt+Shift+A 显示/隐藏）";
            if (!string.Equals(StatusText.Text, text, StringComparison.Ordinal))
                StatusText.Text = text;
            return;
        }

        // The engine stops itself when the game window disappears. Surface the
        // reason instead of leaving the UI in "running" state.
        string reason = session.ExitReason;
        StopSession(string.IsNullOrWhiteSpace(reason) ? "已停止" : $"已停止：{ChineseReason(reason)}");
    }

    private static string ChineseReason(string reason)
    {
        if (reason.Contains("game window closed", StringComparison.Ordinal)) return "游戏窗口已关闭";
        if (reason.Contains("source window closed", StringComparison.Ordinal)) return "游戏窗口已关闭";
        if (reason.Contains("capture failed", StringComparison.Ordinal)) return "捕获失败，游戏可能已关闭";
        return reason;
    }

    private void OnSessionMessage(string message)
    {
        Dispatcher.UIThread.Post(() => AppendLog(message));
    }

    private void AppendLog(string message)
    {
        if (LogText is null) return;
        var text = $"{DateTime.Now:HH:mm:ss} {message}";
        var current = LogText.Text ?? string.Empty;
        var lines = current.Split('\n');
        if (lines.Length > 300) current = string.Join('\n', lines.Skip(lines.Length - 250));
        LogText.Text = current + text + "\n";
        LogText.CaretIndex = LogText.Text?.Length ?? 0;
    }

    private sealed class MonitorChoice
    {
        public MonitorChoice(string? name, string display)
        {
            Name = name;
            Display = display;
        }

        public string? Name { get; }
        public string Display { get; }

        public override string ToString() => Display;
    }
}
