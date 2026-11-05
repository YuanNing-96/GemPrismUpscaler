using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Engine;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using GemPrismUpscaler.Core.Rendering;
using GemPrismUpscaler.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace GemPrismUpscaler.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _autoRefresh = new();
    private readonly SingleInstance? _instance;
    private AppConfig _config;
    private UpscaleSession? _session;
    private List<WindowInfo> _windows = new();
    private List<MonitorInfo> _monitors = new();
    private string _runningTitle = string.Empty;
    private string _sessionHotkey = HotkeyBinding.Default.ToString();
    private ProfileConfig? _activeProfile;
    private TrayService? _tray;
    private bool _exitRequested;

    public MainWindow() : this(null, null)
    {
    }

    public MainWindow(SingleInstance? instance, AppConfig? config = null)
    {
        InitializeComponent();
        _instance = instance;
        _config = config ?? AppConfig.CreateDefault();
        if (_instance is not null)
        {
            _instance.StatusProvider = BuildInstanceInfo;
            _instance.ActivationRequested = () => Dispatcher.UIThread.Post(ActivateFromSecondInstance);
        }

        Title = $"宝石棱镜 {AppInfo.DisplayName}";
        PopulateEffectCombos();
        ApplyConfigDefaults();
        LoadSettingsIntoControls();
        WireEvents();

        Opened += (_, _) =>
        {
            RefreshWindows();
            RefreshProfiles();
            RefreshEffects();
            InitializeTray();
            ApplySettingsToRuntime();
            _watch.Tick += (_, _) => WatchSession();
            _watch.Start();
        };

        Closing += (_, e) =>
        {
            if (!_exitRequested &&
                string.Equals(_config.General.CloseAction, "tray", StringComparison.OrdinalIgnoreCase) &&
                _tray?.CloseToTrayAllowed == true)
            {
                e.Cancel = true;
                Hide();
                AppendLog("window hidden to tray; use the tray menu to exit");
                return;
            }

            AppendLog("window closing");
            StopSession("窗口关闭");
        };
        Closed += (_, _) =>
        {
            ErrorBus.Reported -= OnErrorReported;
            _watch.Stop();
            _autoRefresh.Stop();
            _tray?.Dispose();
            _tray = null;
            Log.Write($"ui closed pid={Environment.ProcessId}");
        };
    }

    private void WireEvents()
    {        RefreshButton.Click += (_, _) => RefreshWindows();
        StartButton.Click += (_, _) => StartSession();
        StopButton.Click += (_, _) => StopSession("已停止");
        ExitButton.Click += (_, _) =>
        {
            _exitRequested = true;
            Close();
        };
        WindowList.SelectionChanged += (_, _) =>
        {
            ApplySelectedProfile();
            UpdateTrayState();
        };
        ErrorCloseButton.Click += (_, _) => ErrorBanner.IsVisible = false;
        ErrorLogButton.Click += (_, _) =>
        {
            MainTabs.SelectedItem = LogTab;
            // The TextBox may be created lazily when the tab is selected; wait
            // for the next layout pass before moving the caret.
            Dispatcher.UIThread.Post(() =>
            {
                if (LogText.IsLoaded) LogText.CaretIndex = LogText.Text?.Length ?? 0;
            }, DispatcherPriority.Background);
        };
        LogTabOpenButton.Click += (_, _) => OpenPath(Log.Path);
        LogTabClearButton.Click += (_, _) =>
        {
            LogText.Text = string.Empty;
            LogTabStatusText.Text = "已清空显示";
        };
        MainTabs.SelectionChanged += (_, _) =>
        {
            if (MainTabs.SelectedItem == LogTab)
            {
                LogTabStatusText.Text = string.Empty;
                Dispatcher.UIThread.Post(() =>
                {
                    if (LogText.IsLoaded) LogText.CaretIndex = LogText.Text?.Length ?? 0;
                }, DispatcherPriority.Background);
            }
        };
        SaveSettingsButton.Click += (_, _) => SaveSettings();
        ResetSettingsButton.Click += (_, _) => ResetSettings();
        OpenConfigDirButton.Click += (_, _) => OpenPath(AppPaths.ConfigDirectory);
        OpenLogButton.Click += (_, _) => OpenPath(Log.Path);
        ReloadProfilesButton.Click += (_, _) => RefreshProfiles();
        OpenProfileConfigButton.Click += (_, _) => OpenConfigFile();
        ReloadEffectsButton.Click += (_, _) => ReloadEffects();
        OpenEffectsDirButton.Click += (_, _) => OpenEffectsDirectory();
        _autoRefresh.Tick += (_, _) => RefreshWindows(quiet: true);
        ErrorBus.Reported += OnErrorReported;
    }

    private string PreferredEffect =>
        _config.General.RememberLast && !string.IsNullOrWhiteSpace(_config.Last.Effect)
            ? _config.Last.Effect
            : _config.Effect.Default;

    private string PreferredScaling =>
        _config.General.RememberLast && !string.IsNullOrWhiteSpace(_config.Last.Scaling)
            ? _config.Last.Scaling
            : _config.Effect.Scaling;

    private string PreferredMonitor =>
        _config.General.RememberLast && !string.IsNullOrWhiteSpace(_config.Last.Monitor)
            ? _config.Last.Monitor
            : _config.Display.Monitor;

    private void PopulateEffectCombos()
    {
        var all = EffectRegistry.All;
        EffectCombo.ItemsSource = all;
        DefaultEffectCombo.ItemsSource = all;
    }

    private static void SelectEffect(ComboBox combo, string id)
    {
        combo.SelectedItem = EffectRegistry.Find(id) ?? EffectRegistry.BuiltIn(EffectMode.Anime4KS);
    }

    private void ApplyConfigDefaults()
    {
        StretchCheck.IsChecked = string.Equals(PreferredScaling, "stretch", StringComparison.OrdinalIgnoreCase);
        FollowFocusCheck.IsChecked = _config.General.FollowFocus;
        SelectEffect(EffectCombo, PreferredEffect);
        int interval = Math.Clamp(_config.General.RefreshIntervalMs, 250, 5000);
        _autoRefresh.Interval = TimeSpan.FromMilliseconds(interval);
    }

    private void LoadSettingsIntoControls()
    {
        HotkeyBox.Text = _config.Hotkey.ToggleOverlay;
        SecondInstanceCombo.SelectedIndex =
            string.Equals(_config.General.SecondInstance, "activate", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        CloseActionCombo.SelectedIndex =
            string.Equals(_config.General.CloseAction, "tray", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        FollowFocusSettingCheck.IsChecked = _config.General.FollowFocus;
        RememberLastCheck.IsChecked = _config.General.RememberLast;
        AutoRefreshCheck.IsChecked = _config.General.AutoRefreshWindows;
        RefreshIntervalBox.Text = _config.General.RefreshIntervalMs.ToString();
        TrayEnabledCheck.IsChecked = _config.Tray.Enabled;
        SelectEffect(DefaultEffectCombo, _config.Effect.Default);
        DefaultScalingCombo.SelectedIndex =
            string.Equals(_config.Effect.Scaling, "stretch", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        DirtyRectCheck.IsChecked = _config.Effect.DirtyRect;
        LogPathBox.Text = _config.Logging.Path;
        DeveloperModeCheck.IsChecked = _config.Diagnostics.DeveloperMode;
        HotkeyHint.Text = $"热键：{_config.Hotkey.ToggleOverlay} 显示/隐藏覆盖层";
        ValidationStatusText.Text = DiagnosticsRuntime.ValidationLayerEnabled ? "开启（下次启动生效）" : "关闭";
    }

    private void ApplySettingsToRuntime()
    {
        try
        {
            Log.Configure(_config.Logging.Path, _config.Logging.Level);
            if (_config.Diagnostics.DeveloperMode) Log.Level = LogLevel.Debug;
            DiagnosticsRuntime.Configure(_config);
            ValidationStatusText.Text = DiagnosticsRuntime.ValidationLayerEnabled ? "开启（下次启动生效）" : "关闭";
        }
        catch (Exception ex)
        {
            ErrorBus.Functional("settings", "应用设置失败", ex);
        }

        if (_config.General.AutoRefreshWindows)
        {
            _autoRefresh.Interval = TimeSpan.FromMilliseconds(
                Math.Clamp(_config.General.RefreshIntervalMs, 250, 5000));
            _autoRefresh.Start();
        }
        else
        {
            _autoRefresh.Stop();
        }

        FollowFocusCheck.IsChecked = _config.General.FollowFocus;
        HotkeyHint.Text = $"热键：{_config.Hotkey.ToggleOverlay} 显示/隐藏覆盖层";
        ApplyTraySettings();
    }

    private void ApplyTraySettings()
    {
        if (!_config.Tray.Enabled)
        {
            _tray?.Dispose();
            _tray = null;
            TrayStatusText.Text = "已禁用";
            _config.General.CloseAction = "exit";
            return;
        }

        InitializeTray();
        if (string.Equals(_config.General.CloseAction, "tray", StringComparison.OrdinalIgnoreCase) &&
            _tray?.CloseToTrayAllowed != true)
        {
            TrayStatusText.Text = $"不可用：{_tray?.Reason ?? "托盘未创建"}；关闭行为已回退为直接退出";
            _config.General.CloseAction = "exit";
            ErrorBus.Functional("tray", "当前环境无法使用托盘，关闭行为已回退为直接退出");
        }
    }

    private void SaveSettings()
    {
        if (!HotkeyBinding.TryParse(HotkeyBox.Text, out HotkeyBinding hotkey, out string? hotkeyError))
        {
            ErrorBus.Functional("settings", $"热键无效：{hotkeyError}");
            return;
        }

        if (!int.TryParse(RefreshIntervalBox.Text, out int interval))
            interval = _config.General.RefreshIntervalMs;
        interval = Math.Clamp(interval, 250, 5000);

        string logPath = (LogPathBox.Text ?? string.Empty).Trim();
        if (logPath.Length > 0)
        {
            try
            {
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                ErrorBus.Functional("settings", $"日志路径不可用：{ex.Message}");
                return;
            }
        }

        _config.Hotkey.ToggleOverlay = hotkey.ToString();
        _config.General.SecondInstance = SecondInstanceCombo.SelectedIndex == 1 ? "activate" : "notify";
        _config.General.CloseAction = CloseActionCombo.SelectedIndex == 1 ? "tray" : "exit";
        _config.General.FollowFocus = FollowFocusSettingCheck.IsChecked != false;
        _config.General.RememberLast = RememberLastCheck.IsChecked != false;
        _config.General.AutoRefreshWindows = AutoRefreshCheck.IsChecked == true;
        _config.General.RefreshIntervalMs = interval;
        _config.Tray.Enabled = TrayEnabledCheck.IsChecked != false;
        if (CloseActionCombo.SelectedIndex == 1)
        {
            if (!_config.Tray.Enabled)
            {
                ErrorBus.Functional("settings", "选择“最小化到托盘”前需要启用托盘");
                return;
            }
            if (_tray is null) InitializeTray();
            if (_tray?.CloseToTrayAllowed != true)
            {
                ErrorBus.Functional("settings",
                    $"当前环境托盘不可用（{_tray?.Reason ?? "未检测到"}），无法选择关闭到托盘");
                return;
            }
        }
        _config.Display.Monitor = (DefaultMonitorCombo.SelectedItem as MonitorChoice)?.Name ?? "auto";
        _config.Effect.Default = (DefaultEffectCombo.SelectedItem as EffectDefinition)?.Id ?? "anime4k-s";
        _config.Effect.Scaling = DefaultScalingCombo.SelectedIndex == 1 ? "stretch" : "fit";
        _config.Effect.DirtyRect = DirtyRectCheck.IsChecked != false;
        _config.Logging.Path = logPath;
        _config.Diagnostics.DeveloperMode = DeveloperModeCheck.IsChecked == true;

        try
        {
            ConfigStore.Save(_config);
        }
        catch (Exception ex)
        {
            ErrorBus.Functional("settings", "保存配置失败", ex);
            return;
        }

        ApplySettingsToRuntime();
        SettingsStatusText.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
        AppendLog($"settings saved to {AppPaths.ConfigPath}");

        if (_session is not null)
            AppendLog("部分设置将在下次开始超分时生效");
    }

    private void ResetSettings()
    {
        _config = AppConfig.CreateDefault();
        LoadSettingsIntoControls();
        ApplyConfigDefaults();
        ApplySettingsToRuntime();
        SettingsStatusText.Text = "已恢复默认（点击保存写入）";
        AppendLog("settings reset to defaults");
    }

    private void ActivateFromSecondInstance()
    {
        BringToFront();
        if (_session?.IsRunning == true && _session.IsOverlayVisible)
            _session.RequestUserHidden(true);
        AppendLog("second instance requested activation");
    }

    /// <summary>
    /// KWin/Wayland may ignore Activate() when the window is already mapped.
    /// Minimizing and restoring forces the compositor to raise it; this is used
    /// by the tray "显示设置窗口" item and second-instance activation.
    /// </summary>
    private void BringToFront()
    {
        Log.Info($"bring-to-front: visible={IsVisible} active={IsActive} state={WindowState}");
        if (!IsVisible) Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Activate();
            ScheduleKdeActivationFallback();
            return;
        }

        if (!IsActive)
        {
            WindowState = WindowState.Minimized;
            DispatcherTimer.RunOnce(() =>
            {
                WindowState = WindowState.Normal;
                Activate();
                Log.Info($"bring-to-front after restore: active={IsActive} state={WindowState}");
                ScheduleKdeActivationFallback();
            }, TimeSpan.FromMilliseconds(120));
        }
        else
        {
            Activate();
        }
    }

    private void ScheduleKdeActivationFallback()
    {
        if (!KdeWindowActivator.IsKde) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(450);
            KdeWindowActivator.TryActivate();
        });
    }

    private void InitializeTray()
    {
        var app = Application.Current;
        if (app is null) return;

        if (!_config.Tray.Enabled)
        {
            _tray?.Dispose();
            _tray = null;
            TrayStatusText.Text = "已禁用";
            return;
        }

        _tray ??= new TrayService();
        if (!_tray.IsCreated)
        {
            if (!_tray.Initialize(app, enabled: true))
            {
                TrayStatusText.Text = $"不可用：{_tray.Reason}";
                _config.General.CloseAction = "exit";
                return;
            }

            _tray.ShowRequested += () => Dispatcher.UIThread.Post(ShowFromTray);
            _tray.StartRequested += () => Dispatcher.UIThread.Post(() =>
            {
                ShowFromTray();
                StartSession();
            });
            _tray.StopRequested += () => Dispatcher.UIThread.Post(() => StopSession("托盘停止"));
            _tray.ToggleRequested += () => Dispatcher.UIThread.Post(ToggleOverlayFromTray);
            _tray.ExitRequested += () => Dispatcher.UIThread.Post(ExitFromTray);
        }

        TrayStatusText.Text = _tray.CloseToTrayAllowed
            ? $"可用（{_tray.Reason}）"
            : $"已创建但无法确认关闭到托盘（{_tray.Reason}）";
        UpdateTrayState();
    }

    private void ShowFromTray()
    {
        Log.Info($"tray: show settings (visible={IsVisible} active={IsActive} state={WindowState})");
        BringToFront();
        if (_session?.IsRunning == true && _session.IsOverlayVisible)
            _session.RequestUserHidden(true);
        AppendLog("tray: show settings");
    }

    private void ToggleOverlayFromTray()
    {
        if (_session is null) return;
        _session.RequestToggleVisibility();
        DispatcherTimer.RunOnce(UpdateTrayState, TimeSpan.FromMilliseconds(300));
        AppendLog("tray: toggle overlay");
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private void UpdateTrayState()
    {
        _tray?.UpdateState(
            sessionActive: _session is not null,
            canStart: WindowList.SelectedItem is WindowInfo,
            overlayVisible: _session?.IsOverlayVisible == true);
    }

    private InstanceInfo BuildInstanceInfo() => new(
        Environment.ProcessId,
        "gui",
        _instance?.StartedAt ?? DateTimeOffset.Now,
        _session?.IsRunning == true,
        _session?.MonitorName);

    private void RefreshWindows(bool quiet = false)
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

            string status = $"发现 {_windows.Count} 个窗口 · {_monitors.Count} 台显示器";
            if (!string.Equals(StatusText.Text, status, StringComparison.Ordinal))
                StatusText.Text = status;

            if (!quiet)
            {
                AppendLog($"refresh: {_windows.Count} windows, {_monitors.Count} monitors");
                foreach (var monitor in _monitors)
                    AppendLog($"monitor: {monitor}");
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "X11 连接失败";
            ErrorBus.Functional("capture", "刷新窗口列表失败", ex);
            if (!quiet) AppendLog($"refresh error: {ex.Message}");
        }
    }

    private void PopulateMonitors()
    {
        string? previous = (MonitorCombo.SelectedItem as MonitorChoice)?.Name;
        if (MonitorCombo.SelectedItem is null &&
            !string.Equals(PreferredMonitor, "auto", StringComparison.OrdinalIgnoreCase))
            previous = PreferredMonitor;

        var choices = new List<MonitorChoice>
        {
            new(null, "自动（跟随游戏窗口）")
        };
        choices.AddRange(_monitors.Select(m => new MonitorChoice(
            m.Name,
            $"{m.Name} · {m.Width}×{m.Height}{(m.Primary ? "（主屏）" : string.Empty)}")));

        MonitorCombo.ItemsSource = choices;
        MonitorCombo.SelectedItem = choices.FirstOrDefault(c => c.Name == previous) ?? choices[0];

        string? defaultMonitor = (DefaultMonitorCombo.SelectedItem as MonitorChoice)?.Name;
        DefaultMonitorCombo.ItemsSource = choices;
        DefaultMonitorCombo.SelectedItem = choices.FirstOrDefault(c => c.Name == defaultMonitor)
                                           ?? choices.FirstOrDefault(c => c.Name == _config.Display.Monitor)
                                           ?? choices[0];
    }

    private void ApplySelectedProfile()
    {
        if (WindowList.SelectedItem is not WindowInfo info)
        {
            _activeProfile = null;
            return;
        }

        var match = ProfileMatcher.Match(_config.Profile, info);
        if (match is null)
        {
            _activeProfile = null;
            ProfileStatusText.Text = $"当前窗口未匹配 Profile：{info.ClassName}";
            return;
        }

        var profile = match.Profile;
        _activeProfile = profile;

        if (!string.IsNullOrWhiteSpace(profile.Effect))
            SelectEffect(EffectCombo, profile.Effect);
        if (!string.IsNullOrWhiteSpace(profile.Scaling))
            StretchCheck.IsChecked = string.Equals(profile.Scaling, "stretch", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(profile.Monitor))
            SelectMonitor(profile.Monitor);
        if (profile.FollowFocus.HasValue)
            FollowFocusCheck.IsChecked = profile.FollowFocus.Value;

        ProfileStatusText.Text = $"已套用：{profile.Name}（{match.Field}={match.Value}）";
        AppendLog($"profile '{profile.Name}' matched by {match.Field}={match.Value}");
    }

    private void SelectMonitor(string name)
    {
        if (MonitorCombo.ItemsSource is not IEnumerable<MonitorChoice> choices) return;
        var match = choices.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) MonitorCombo.SelectedItem = match;
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
        var effect = EffectCombo.SelectedItem as EffectDefinition
                     ?? EffectRegistry.BuiltIn(EffectMode.Anime4KS);
        if (!effect.IsAvailable)
        {
            ErrorBus.Functional("effect", $"效果“{effect.DisplayName}”不可用：{effect.UnavailableReason}");
            return;
        }
        string? monitorName = (MonitorCombo.SelectedItem as MonitorChoice)?.Name;
        var hotkey = HotkeyBinding.Default;
        string hotkeyText = !string.IsNullOrWhiteSpace(_activeProfile?.Hotkey)
            ? _activeProfile!.Hotkey!
            : _config.Hotkey.ToggleOverlay;
        if (!HotkeyBinding.TryParse(hotkeyText, out hotkey, out _))
            hotkey = HotkeyBinding.Default;
        _sessionHotkey = hotkey.ToString();

        var session = new UpscaleSession(info, mode, effect.Mode, monitorName, 0, 0, hotkey, effect)
        {
            FollowFocus = FollowFocusCheck.IsChecked != false,
            DirtyRectEnabled = _activeProfile?.DirtyRect ?? _config.Effect.DirtyRect
        };
        session.Message += OnSessionMessage;
        try
        {
            session.Start();
            _session = session;
            _runningTitle = info.Title;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = $"运行中：{_runningTitle}（显示器：{session.MonitorName}，{hotkey} 显示/隐藏）";
            AppendLog($"started: 0x{info.WindowId:x} '{info.Title}' {info.Width}x{info.Height} " +
                      $"mode={mode} effect={session.EffectName} monitor={session.MonitorName} hotkey={hotkey}" +
                      (_activeProfile is null ? string.Empty : $" profile={_activeProfile.Name}"));
            Log.Write($"ui session started pid={Environment.ProcessId} window=0x{info.WindowId:x} " +
                      $"monitor={session.MonitorName} effect={session.EffectName}");
            RememberLast(monitorName, mode);
            UpdateTrayState();
        }
        catch (Exception ex)
        {
            session.Dispose();
            StatusText.Text = "启动失败";
            AppendLog($"start error: {ex.Message}");
            ErrorBus.Functional("session", "启动超分失败", ex);
        }
    }

    private void RememberLast(string? monitorName, ScalingMode mode)
    {
        if (!_config.General.RememberLast) return;
        _config.Last.Effect = (EffectCombo.SelectedItem as EffectDefinition)?.Id ?? "anime4k-s";
        _config.Last.Scaling = mode == ScalingMode.Stretch ? "stretch" : "fit";
        _config.Last.Monitor = monitorName ?? "auto";
        try
        {
            ConfigStore.Save(_config);
        }
        catch (Exception ex)
        {
            Log.Warn($"remember-last save failed: {ex.Message}");
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
        UpdateTrayState();
    }

    private void WatchSession()
    {
        var session = _session;
        if (session is null) return;

        if (session.IsRunning)
        {
            // Auto mode can move the overlay to another monitor; keep the status
            // line in sync without waiting for a message.
            string hotkey = _sessionHotkey;
            string text = $"运行中：{_runningTitle}（显示器：{session.MonitorName}，{hotkey} 显示/隐藏）";
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

    private void OnErrorReported(AppError error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _tray?.SetErrorState(true);
            if (error.Kind == AppErrorKind.Functional)
            {
                ErrorText.Text = $"{error.Source}: {error.DisplayText}";
                ErrorBanner.IsVisible = true;
            }
            else if (DiagnosticsRuntime.DeveloperMode)
            {
                LogTabStatusText.Text = "有新的诊断错误";
                AppendLog($"[{error.Severity}] {error.Source}: {error.DisplayText}");
            }
        });
    }

    private void RefreshProfiles()
    {
        var items = _config.Profile.Select(p =>
        {
            string match = string.Join(", ", p.Class.Concat(p.Exe));
            return new ProfileViewItem(p.Name, match, p.Effect ?? "(默认)");
        }).ToList();
        ProfileList.ItemsSource = items;
        ProfileStatusText.Text = items.Count == 0
            ? "没有 Profile；在 config.toml 里添加 [[profile]]"
            : $"{items.Count} 个 Profile（先匹配先赢）";
    }

    private void ReloadEffects()
    {
        string? selected = (EffectCombo.SelectedItem as EffectDefinition)?.Id;
        string? defaultSelected = (DefaultEffectCombo.SelectedItem as EffectDefinition)?.Id;
        EffectRegistry.Reload();
        PopulateEffectCombos();
        SelectEffect(EffectCombo, selected ?? PreferredEffect);
        SelectEffect(DefaultEffectCombo, defaultSelected ?? _config.Effect.Default);
        RefreshEffects();
    }

    private void RefreshEffects()
    {
        var all = EffectRegistry.All;
        EffectList.ItemsSource = all
            .Select(effect => new EffectViewItem(effect))
            .ToList();
        int custom = all.Count(effect => !effect.IsBuiltIn);
        EffectStatusText.Text = $"{all.Count} 个效果（{custom} 个自定义）；目录 {AppPaths.EffectsDirectory}";
    }

    private void OpenConfigFile()
    {
        if (!File.Exists(AppPaths.ConfigPath))
        {
            try { ConfigStore.Save(_config); }
            catch (Exception ex)
            {
                ErrorBus.Functional("settings", "创建配置文件失败", ex);
                return;
            }
        }
        OpenPath(AppPaths.ConfigPath);
    }

    private void OpenEffectsDirectory()
    {
        try { Directory.CreateDirectory(AppPaths.EffectsDirectory); }
        catch (Exception ex)
        {
            ErrorBus.Functional("effect", "创建效果目录失败", ex);
            return;
        }
        OpenPath(AppPaths.EffectsDirectory);
    }

    private static void OpenPath(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorBus.Warning("ui", $"无法打开 {path}", ex);
        }
    }

    private void AppendLog(string message)
    {
        if (LogText is null) return;
        var text = $"{DateTime.Now:HH:mm:ss} {message}";
        var current = LogText.Text ?? string.Empty;
        var lines = current.Split('\n');
        if (lines.Length > 300) current = string.Join('\n', lines.Skip(lines.Length - 250));
        LogText.Text = current + text + "\n";

        // A non-selected tab has no TextBox template yet. Touching CaretIndex
        // there can trigger "Could not create glyphTypeface" during a later
        // layout pass, so only move the caret when the log tab is on screen.
        if (MainTabs.SelectedItem == LogTab && LogText.IsLoaded)
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

    private sealed class EffectViewItem
    {
        private readonly EffectDefinition _effect;

        public EffectViewItem(EffectDefinition effect)
        {
            _effect = effect;
        }

        public override string ToString()
        {
            string state = _effect.IsAvailable
                ? string.Empty
                : $"（不可用：{_effect.UnavailableReason}）";
            string kind = _effect.IsBuiltIn ? "内置" : "自定义";
            return $"{_effect.DisplayName}  [{_effect.Id}] · {kind}{state}";
        }
    }

    private sealed class ProfileViewItem
    {
        public ProfileViewItem(string name, string match, string effect)
        {
            Name = name;
            Match = match;
            Effect = effect;
        }

        public string Name { get; }
        public string Match { get; }
        public string Effect { get; }

        public override string ToString() =>
            $"{Name} · 匹配 [{Match}] · effect {Effect}";
    }
}
