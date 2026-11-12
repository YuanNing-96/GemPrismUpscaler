using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Engine;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux;
using GemPrismUpscaler.Core.Platform.Linux.Nested;
using GemPrismUpscaler.Core.Platform.Linux.Portal;
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
    private PortalUpscaleSession? _portalSession;
    private NestedSession? _nestedShell;
    private NestedUpscaleSession? _nestedUpscale;
    private bool _nestedStarting;
    private string? _nestedMonitorName;
    private List<WindowInfo> _windows = new();
    private List<MonitorInfo> _monitors = new();
    private string _runningTitle = string.Empty;
    private string _sessionHotkey = HotkeyBinding.Default.ToString();
    private ProfileConfig? _activeProfile;
    private string? _baseEffect;
    private string? _baseScaling;
    private string? _baseMonitor;
    private bool _baseFollowFocus;
    private bool _hasProfileBase;
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
        try
        {
            using var iconStream = AssetLoader.Open(
                new Uri("avares://GemPrismUpscaler/Assets/gemprism.png"));
            Icon = new WindowIcon(iconStream);
        }
        catch (Exception ex)
        {
            Log.Warn($"window icon asset missing: {ex.Message}");
        }
        PopulateEffectCombos();
        ApplyConfigDefaults();
        LoadSettingsIntoControls();
        WireEvents();

        Opened += (_, _) =>
        {
            UpdateLaunchUi();
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
        StartButton.Click += (_, _) => StartFromUi();
        PortalButton.Click += (_, _) => StartPortalSession();
        NestedStartButton.Click += (_, _) => StartNestedSession();
        StopButton.Click += (_, _) => StopSession("已停止");
        PauseButton.Click += (_, _) => TogglePause();
        LaunchModeCombo.SelectionChanged += (_, _) => UpdateLaunchUi();
        WindowBackendCombo.SelectionChanged += (_, _) => UpdateLaunchUi();
        ExitButton.Click += (_, _) =>
        {
            _exitRequested = true;
            Close();
        };
        WindowList.SelectionChanged += (_, _) =>
        {
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
        LogTabOpenButton.Click += (_, _) => OpenLogDirectory();
        ExportDiagnosticsButton.Click += (_, _) =>
        {
            try
            {
                string zip = DiagnosticsPackage.Create();
                LogTabStatusText.Text = $"诊断包：{zip}";
                AppendLog($"diagnostics package: {zip}");
                OpenPath(Path.GetDirectoryName(zip) ?? AppPaths.ConfigDirectory);
            }
            catch (Exception ex)
            {
                ErrorBus.Functional("diagnostics", "导出诊断包失败", ex);
            }
        };
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
        OpenLogButton.Click += (_, _) => OpenLogDirectory();
        ReloadProfilesButton.Click += (_, _) => ReloadProfilesFromDisk();
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
        var builtIns = EffectRegistry.All.Where(e => e.IsBuiltIn).ToList();
        EffectCombo.ItemsSource = builtIns;
        DefaultEffectCombo.ItemsSource = builtIns;
    }

    private static void SelectEffect(ComboBox combo, string id)
    {
        var effect = EffectRegistry.Find(id);
        combo.SelectedItem = effect is { IsBuiltIn: true }
            ? effect
            : EffectRegistry.BuiltIn(EffectMode.Anime4KS);
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
        _config.Hotkey.ToggleOverlay = HotkeyBinding.Default.ToString();
        HotkeyBox.Text = _config.Hotkey.ToggleOverlay;
        NestedCommandBox.Text = _config.Nested.Command;
        SecondInstanceCombo.SelectedIndex =
            string.Equals(_config.General.SecondInstance, "activate", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        CloseActionCombo.SelectedIndex =
            string.Equals(_config.General.CloseAction, "tray", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        FollowFocusSettingCheck.IsChecked = _config.General.FollowFocus;
        RememberLastCheck.IsChecked = _config.General.RememberLast;
        AutoRefreshCheck.IsChecked = true;
        _config.General.AutoRefreshWindows = true;
        RefreshIntervalBox.Text = _config.General.RefreshIntervalMs.ToString();
        TrayEnabledCheck.IsChecked = _config.Tray.Enabled;
        SelectEffect(DefaultEffectCombo, _config.Effect.Default);
        DefaultScalingCombo.SelectedIndex =
            string.Equals(_config.Effect.Scaling, "stretch", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        DirtyRectCheck.IsChecked = _config.Effect.DirtyRect;
        LogPathBox.Text = string.IsNullOrWhiteSpace(_config.Logging.Path)
            ? AppPaths.DefaultLogPath
            : _config.Logging.Path;
        DeveloperModeCheck.IsChecked = _config.Diagnostics.DeveloperMode;
        HotkeyHint.Text = "热键：Alt+Shift+A（v1.0 暂不支持修改）";
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
        HotkeyHint.Text = "热键：Alt+Shift+A（v1.0 暂不支持修改）";
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
        if (string.Equals(logPath, AppPaths.DefaultLogPath, StringComparison.Ordinal))
            logPath = string.Empty;
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

        _config.Hotkey.ToggleOverlay = HotkeyBinding.Default.ToString();
        _config.General.SecondInstance = SecondInstanceCombo.SelectedIndex == 1 ? "activate" : "notify";
        _config.General.CloseAction = CloseActionCombo.SelectedIndex == 1 ? "tray" : "exit";
        _config.General.FollowFocus = FollowFocusSettingCheck.IsChecked != false;
        _config.General.RememberLast = RememberLastCheck.IsChecked != false;
        _config.General.AutoRefreshWindows = true;
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
        _config.Nested.Command = (NestedCommandBox.Text ?? string.Empty).Trim();

        MergeProfilesFromDisk();
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
                StartFromUi();
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
            sessionActive: _session is not null || _portalSession is not null,
            canStart: WindowList.SelectedItem is WindowInfo,
            overlayVisible: _session?.IsOverlayVisible == true);
    }

    private InstanceInfo BuildInstanceInfo() => new(
        Environment.ProcessId,
        "gui",
        _instance?.StartedAt ?? DateTimeOffset.Now,
        _session?.IsRunning == true || _portalSession?.IsRunning == true ||
        _nestedUpscale?.IsRunning == true,
        _session?.MonitorName ?? _portalSession?.OverlayDescription ?? _nestedMonitorName);

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

        var choices = _monitors.Select(m => new MonitorChoice(
            m.Name,
            $"{m.Name} · {m.Width}×{m.Height}{(m.Primary ? "（主屏）" : string.Empty)}")).ToList();

        MonitorCombo.ItemsSource = choices;
        if (choices.Count == 0)
            return;
        MonitorCombo.SelectedItem = choices.FirstOrDefault(c => c.Name == previous)
                                   ?? choices.FirstOrDefault(c => _monitors.First(m => m.Name == c.Name).Primary)
                                   ?? choices[0];

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
            if (_activeProfile is not null)
            {
                RestoreProfileBase();
                _activeProfile = null;
                ProfileStatusText.Text = "当前窗口未匹配 Profile，已恢复全局设置";
                return;
            }

            string id = string.IsNullOrWhiteSpace(info.ClassName)
                ? $"pid={info.Pid}（WM_CLASS 为空，请改用 exe 匹配）"
                : info.ClassName;
            ProfileStatusText.Text = $"当前窗口未匹配 Profile：{id}";
            return;
        }

        if (_activeProfile is null)
            CaptureProfileBase();

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

    private void CaptureProfileBase()
    {
        _baseEffect = (EffectCombo.SelectedItem as EffectDefinition)?.Id;
        _baseScaling = StretchCheck.IsChecked == true ? "stretch" : "fit";
        _baseMonitor = (MonitorCombo.SelectedItem as MonitorChoice)?.Name ?? "auto";
        _baseFollowFocus = FollowFocusCheck.IsChecked == true;
        _hasProfileBase = true;
    }

    private void RestoreProfileBase()
    {
        if (!_hasProfileBase) return;
        SelectEffect(EffectCombo, _baseEffect ?? PreferredEffect);
        StretchCheck.IsChecked = string.Equals(_baseScaling, "stretch", StringComparison.OrdinalIgnoreCase);
        SelectMonitor(_baseMonitor ?? "auto");
        FollowFocusCheck.IsChecked = _baseFollowFocus;
        _hasProfileBase = false;
    }

    private void SelectMonitor(string name)
    {
        if (MonitorCombo.ItemsSource is not IEnumerable<MonitorChoice> choices) return;
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (choices.FirstOrDefault() is { } auto)
                MonitorCombo.SelectedItem = auto;
            return;
        }
        var match = choices.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) MonitorCombo.SelectedItem = match;
    }

    private void StartNestedSession()
    {
        if (_nestedStarting || _nestedUpscale is not null || _nestedShell is not null)
            return;

        string commandText = (NestedCommandBox.Text ?? string.Empty).Trim();
        if (commandText.Length == 0)
        {
            ErrorBus.Functional("nested", "请先填写嵌套启动命令");
            StatusText.Text = "嵌套命令为空";
            return;
        }

        if (commandText.Contains("lutris", StringComparison.OrdinalIgnoreCase) && IsLutrisRunning())
        {
            const string message = "检测到 Lutris 正在运行。请先完全退出 Lutris（包括托盘图标），" +
                                   "再启动嵌套会话；否则 Lutris 会把启动请求转给已有实例并直接退出。";
            ErrorBus.Functional("nested", message);
            StatusText.Text = "Lutris 仍在运行，嵌套未启动";
            AppendLog("nested preflight: Lutris is still running; start refused");
            return;
        }

        string[] command;
        try
        {
            command = NestedCommandLine.Split(commandText);
        }
        catch (FormatException ex)
        {
            ErrorBus.Functional("nested", $"嵌套命令格式错误：{ex.Message}");
            return;
        }
        if (command.Length == 0)
        {
            ErrorBus.Functional("nested", "嵌套命令为空");
            return;
        }

        var mode = string.Equals(_config.Effect.Scaling, "stretch", StringComparison.OrdinalIgnoreCase)
            ? ScalingMode.Stretch : ScalingMode.Fit;
        var effect = EffectCombo.SelectedItem as EffectDefinition
                     ?? EffectRegistry.BuiltIn(EffectMode.Anime4KS);
        if (!effect.IsAvailable)
        {
            ErrorBus.Functional("effect", $"效果“{effect.DisplayName}”不可用：{effect.UnavailableReason}");
            return;
        }

        string? monitorName = (MonitorCombo.SelectedItem as MonitorChoice)?.Name;
        if (string.Equals(monitorName, "auto", StringComparison.OrdinalIgnoreCase))
            monitorName = null;

        NestedMonitorInfo monitor;
        try
        {
            monitor = NestedMonitor.Resolve(monitorName);
        }
        catch (Exception ex)
        {
            ErrorBus.Functional("nested", "无法解析嵌套显示器", ex);
            return;
        }

        string swayPath = NestedSession.ResolveSwayPath();
        string? swayMsgPath = NestedSession.ResolveSwayMsgPath();
        NestedSession shell;
        NestedUpscaleSession nested;
        try
        {
            shell = new NestedSession(swayPath, command, monitor.Width, monitor.Height,
                swayMsgPath, refreshMilliHz: monitor.RefreshMilliHz);
            nested = new NestedUpscaleSession(shell, mode, effect, monitor.Name,
                waitForCommand: true);
        }
        catch (Exception ex)
        {
            ErrorBus.Functional("nested", "创建嵌套会话失败", ex);
            return;
        }

        _config.Nested.Command = commandText;
        try { ConfigStore.Save(_config); } catch (Exception ex) { Log.Warn($"nested command save failed: {ex.Message}"); }

        nested.Message += OnSessionMessage;
        _nestedShell = shell;
        _nestedUpscale = nested;
        _nestedMonitorName = monitor.Name;
        _nestedStarting = true;
        _runningTitle = "嵌套会话";
        StartButton.IsEnabled = false;
        PortalButton.IsEnabled = false;
        NestedStartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        UpdatePauseButton();
        StatusText.Text = $"正在启动嵌套会话：{monitor.Name} {monitor.Width}x{monitor.Height}…";
        AppendLog($"nested start: monitor={monitor.Name} size={monitor.Width}x{monitor.Height} " +
                  $"effect={effect.Id} command={commandText}");

        Task.Run(() =>
        {
            try
            {
                shell.Start();
                shell.StartCommand();
                nested.Start();
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_nestedUpscale, nested))
                    {
                        try { nested.Stop(); } catch { }
                        try { nested.Dispose(); } catch { }
                        try { shell.Stop(); } catch { }
                        try { shell.Dispose(); } catch { }
                        return;
                    }
                    _nestedStarting = false;
                    UpdatePauseButton();
                    StatusText.Text = $"运行中：嵌套（{monitor.Name}，{effect.DisplayName}）";
                    AppendLog("nested session started");
                    UpdateTrayState();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_nestedUpscale, nested))
                    {
                        _nestedUpscale = null;
                        _nestedShell = null;
                        _nestedMonitorName = null;
                        _nestedStarting = false;
                        _runningTitle = string.Empty;
                        StartButton.IsEnabled = true;
                        PortalButton.IsEnabled = true;
                        NestedStartButton.IsEnabled = true;
                        StopButton.IsEnabled = false;
                        StatusText.Text = "嵌套会话启动失败";
                    }
                    AppendLog($"nested start error: {ex.Message}");
                    ErrorBus.Functional("nested", "嵌套会话启动失败", ex);
                    try { nested.Message -= OnSessionMessage; } catch { }
                    try { nested.Dispose(); } catch { }
                    try { shell.Stop(); } catch { }
                    try { shell.Dispose(); } catch { }
                });
            }
        });
    }

    private void StartFromUi()
    {
        if (LaunchModeCombo.SelectedIndex == 1)
            StartNestedSession();
        else if (WindowBackendCombo.SelectedIndex == 1)
            StartPortalSession();
        else
            StartSession();
    }

    private void UpdateLaunchUi()
    {
        bool nested = LaunchModeCombo.SelectedIndex == 1;
        bool existing = !nested;
        bool wayland = existing && WindowBackendCombo.SelectedIndex == 1;

        WindowBackendPanel.IsVisible = existing;
        WindowBackendCombo.IsVisible = existing;
        WindowList.IsVisible = existing && !wayland;
        NestedPanel.IsVisible = nested;
        WaylandPanel.IsVisible = wayland;
        StartButton.Content = wayland ? "选择 Wayland 全屏窗口" : "开始超分";
    }

    private void TogglePause()
    {
        if (_nestedUpscale is not null)
            _nestedUpscale.RequestToggleUpscale();
        else if (_session is not null)
            _session.RequestToggleVisibility();
        else if (_portalSession is not null)
            _portalSession.RequestToggleVisibility();
        else
            return;
        DispatcherTimer.RunOnce(UpdatePauseButton, TimeSpan.FromMilliseconds(350));
    }

    private void UpdatePauseButton()
    {
        bool active = _session is not null || _portalSession is not null || _nestedUpscale is not null;
        PauseButton.IsEnabled = active;
        if (_nestedUpscale is not null)
            PauseButton.Content = _nestedUpscale.IsUpscaleMode ? "暂停超分" : "继续超分";
        else if (_session is not null)
            PauseButton.Content = _session.IsOverlayVisible ? "暂停超分" : "继续超分";
        else if (_portalSession is not null)
            PauseButton.Content = _portalSession.IsOverlayVisible ? "暂停超分" : "继续超分";
        else
            PauseButton.Content = "暂停超分";
    }

    private static bool IsLutrisRunning()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("lutris"))
            {
                process.Dispose();
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"nested preflight: Lutris process check failed: {ex.Message}");
        }
        return false;
    }

    private void OpenLogDirectory()
    {
        string? directory = Path.GetDirectoryName(Log.Path);
        OpenPath(string.IsNullOrWhiteSpace(directory) ? AppPaths.DefaultLogPath : directory);
    }

    private void StartPortalSession()
    {
        StopSession("切换为 Wayland 门户");
        var mode = string.Equals(_config.Effect.Scaling, "stretch", StringComparison.OrdinalIgnoreCase)
            ? ScalingMode.Stretch : ScalingMode.Fit;
        var effect = EffectCombo.SelectedItem as EffectDefinition
                     ?? EffectRegistry.BuiltIn(EffectMode.Anime4KS);
        if (!effect.IsAvailable)
        {
            ErrorBus.Functional("effect", $"效果“{effect.DisplayName}”不可用：{effect.UnavailableReason}");
            return;
        }
        string? monitorName = (MonitorCombo.SelectedItem as MonitorChoice)?.Name;
        var portalHotkey = HotkeyBinding.Default;
        if (!HotkeyBinding.TryParse(_config.Hotkey.ToggleOverlay, out portalHotkey, out _))
            portalHotkey = HotkeyBinding.Default;
        _sessionHotkey = portalHotkey.ToString();
        var session = new PortalUpscaleSession(mode, effect, monitorName,
            PortalSourceType.Window, reuseToken: false, portalHotkey);
        session.Message += OnSessionMessage;
        _portalSession = session;
        _runningTitle = "Wayland 窗口";
        StartButton.IsEnabled = false;
        PortalButton.IsEnabled = false;
        NestedStartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        UpdatePauseButton();
        StatusText.Text = "请在系统对话框中选择要共享的窗口…";
        AppendLog("portal: waiting for system window picker");

        Task.Run(() =>
        {
            try
            {
                session.Start();
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_portalSession, session)) return;
                    StatusText.Text = $"运行中：Wayland 门户（{session.OverlayDescription}）";
                    AppendLog("portal session started");
                    UpdateTrayState();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_portalSession, session))
                    {
                        _portalSession = null;
                        _runningTitle = string.Empty;
                        StartButton.IsEnabled = true;
                        PortalButton.IsEnabled = true;
                        NestedStartButton.IsEnabled = true;
                        StopButton.IsEnabled = false;
                        UpdatePauseButton();
                        StatusText.Text = "Wayland 门户启动失败";
                    }
                    AppendLog($"portal start error: {ex.Message}");
                    ErrorBus.Functional("portal", "Wayland 门户启动失败", ex);
                    session.Dispose();
                });
            }
        });
    }

    private void StartSession()
    {
        if (WindowList.SelectedItem is not WindowInfo info)
        {
            StatusText.Text = "请先选择一个窗口";
            return;
        }

        StopSession("重新启动");
        var mode = string.Equals(_config.Effect.Scaling, "stretch", StringComparison.OrdinalIgnoreCase)
            ? ScalingMode.Stretch : ScalingMode.Fit;
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

        var backend = _config.Capture.Backend switch
        {
            "wayland" => OverlayBackend.Wayland,
            "x11" => OverlayBackend.X11,
            _ => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
                 !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
                ? OverlayBackend.Wayland
                : OverlayBackend.X11
        };
        bool pointerPassthrough = string.Equals(_config.Input.PointerMode, "passthrough",
            StringComparison.OrdinalIgnoreCase);

        var session = new UpscaleSession(info, mode, effect.Mode, monitorName, 0, 0, hotkey, effect,
            backend, pointerPassthrough)
        {
            FollowFocus = _config.General.FollowFocus,
            DirtyRectEnabled = _config.Effect.DirtyRect
        };
        session.Message += OnSessionMessage;
        try
        {
            session.Start();
            _session = session;
            _runningTitle = info.Title;
            StartButton.IsEnabled = false;
            NestedStartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            UpdatePauseButton();
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

    /// <summary>
    /// Profiles are edited in the TOML file, not in the UI. Before any full
    /// config save, pick up the on-disk profile list so a session start or a
    /// settings save does not silently overwrite a newly added profile.
    /// </summary>
    private void MergeProfilesFromDisk()
    {
        try
        {
            var result = ConfigStore.Load();
            if (result.FileExisted && !result.RecoveredFromError)
                _config.Profile = result.Config.Profile;
        }
        catch (Exception ex)
        {
            Log.Warn($"profile merge from disk failed: {ex.Message}");
        }
    }

    private void RememberLast(string? monitorName, ScalingMode mode)
    {
        if (!_config.General.RememberLast) return;
        MergeProfilesFromDisk();
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
        var nested = _nestedUpscale;
        _nestedUpscale = null;
        var nestedShell = _nestedShell;
        _nestedShell = null;
        _nestedMonitorName = null;
        _nestedStarting = false;
        if (nested is not null)
        {
            nested.Message -= OnSessionMessage;
            AppendLog($"stopping nested session ({status ?? "requested"})");
            try { nested.Stop(); } catch (Exception ex) { AppendLog($"nested stop error: {ex.Message}"); }
            nested.Dispose();
        }
        if (nestedShell is not null)
        {
            try { nestedShell.Stop(); } catch (Exception ex) { AppendLog($"nested shell stop error: {ex.Message}"); }
            nestedShell.Dispose();
        }

        var portal = _portalSession;
        _portalSession = null;
        if (portal is not null)
        {
            portal.Message -= OnSessionMessage;
            AppendLog("stopping portal session");
            try { portal.Stop(); } catch (Exception ex) { AppendLog($"portal stop error: {ex.Message}"); }
            portal.Dispose();
        }

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
        PortalButton.IsEnabled = true;
        NestedStartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        UpdatePauseButton();
        if (StatusText is not null) StatusText.Text = status ?? "已停止";
        UpdateTrayState();
    }

    private void WatchSession()
    {
        var nested = _nestedUpscale;
        if (nested is not null)
        {
            if (_nestedStarting) return;
            if (nested.IsRunning)
            {
                string text = $"运行中：嵌套（{_nestedMonitorName ?? "?"}，{nested.EffectName}）";
                if (!string.Equals(StatusText.Text, text, StringComparison.Ordinal))
                    StatusText.Text = text;
                UpdatePauseButton();
                return;
            }
            string? nestedReason = nested.ExitReason;
            if (nestedReason is not null &&
                nestedReason.Contains("command exited", StringComparison.Ordinal) &&
                !nested.HasHostWindows)
            {
                if (!nested.HasEverHadHostWindow)
                {
                    const string message = "嵌套启动失败：命令已退出，但没有检测到游戏窗口。" +
                                           "请确认 Lutris 已完全退出（包括托盘），或命令本身会保持运行。";
                    ErrorBus.Functional("nested", message);
                    StopSession(message);
                }
                else
                {
                    StopSession("游戏已退出");
                }
                return;
            }
            StopSession(string.IsNullOrWhiteSpace(nestedReason) ? "嵌套会话已停止"
                : $"已停止：{ChineseReason(nestedReason)}");
            return;
        }
        if (_nestedStarting) return;

        var portal = _portalSession;
        if (portal is not null)
        {
            if (portal.IsRunning)
            {
                string text = $"运行中：Wayland 门户（{portal.OverlayDescription}）";
                if (!string.Equals(StatusText.Text, text, StringComparison.Ordinal))
                    StatusText.Text = text;
                UpdatePauseButton();
                return;
            }
            string portalReason = portal.ExitReason;
            StopSession(string.IsNullOrWhiteSpace(portalReason) ? "已停止"
                : $"已停止：{ChineseReason(portalReason)}");
            return;
        }

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
            UpdatePauseButton();
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

    private void ReloadProfilesFromDisk()
    {
        var result = ConfigStore.Load();
        if (result.RecoveredFromError && !string.IsNullOrWhiteSpace(result.Error))
            ErrorBus.Functional("config", result.Error);

        _config.Profile = result.Config.Profile;
        RefreshProfiles();
        ProfileStatusText.Text = $"已从磁盘重新加载 {_config.Profile.Count} 个 Profile";
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
