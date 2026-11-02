using Avalonia.Controls;
using Avalonia.Threading;
using GemPrismUpscaler.Core.Engine;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GemPrismUpscaler.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private UpscaleSession? _session;
    private List<WindowInfo> _windows = new();

    public MainWindow()
    {
        InitializeComponent();
        RefreshButton.Click += (_, _) => RefreshWindows();
        StartButton.Click += (_, _) => StartSession();
        StopButton.Click += (_, _) => StopSession();
        ExitButton.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            RefreshWindows();
            _watch.Tick += (_, _) => WatchSession();
            _watch.Start();
        };
        Closing += (_, _) => StopSession();
    }

    private void RefreshWindows()
    {
        try
        {
            using var display = new X11Display();
            _windows = X11WindowEnumerator.List(display)
                .Where(w => w.Pid != Environment.ProcessId)
                .OrderByDescending(w => w.Width * w.Height)
                .ToList();
            WindowList.ItemsSource = _windows;
            if (_windows.Count > 0) WindowList.SelectedIndex = 0;
            StatusText.Text = $"发现 {_windows.Count} 个窗口";
            AppendLog($"refresh: {_windows.Count} windows");
        }
        catch (Exception ex)
        {
            StatusText.Text = "X11 连接失败";
            AppendLog($"refresh error: {ex.Message}");
        }
    }

    private void StartSession()
    {
        if (WindowList.SelectedItem is not WindowInfo info)
        {
            StatusText.Text = "请先选择一个窗口";
            return;
        }

        StopSession();
        var mode = StretchCheck.IsChecked == true ? ScalingMode.Stretch : ScalingMode.Fit;
        var session = new UpscaleSession(info, mode)
        {
            FollowFocus = FollowFocusCheck.IsChecked != false
        };
        session.Message += OnSessionMessage;
        try
        {
            session.Start();
            _session = session;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = $"运行中：{info.Title}（Alt+Shift+A 显示/隐藏）";
            AppendLog($"started: 0x{info.WindowId:x} '{info.Title}' {info.Width}x{info.Height} mode={mode}");
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
        if (session is not null)
        {
            session.Message -= OnSessionMessage;
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
        if (session is null || session.IsRunning) return;

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
}
