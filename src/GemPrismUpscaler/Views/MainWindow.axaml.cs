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
        var session = new UpscaleSession(info, mode);
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

    private void StopSession()
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
        if (StatusText is not null) StatusText.Text = "已停止";
    }

    private void WatchSession()
    {
        if (_session is null || _session.IsRunning) return;
        StopSession();
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
