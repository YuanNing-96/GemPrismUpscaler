using GemPrismUpscaler.Core.Configuration;
using Xunit;

namespace GemPrismUpscaler.Tests;

public sealed class M1cConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "gemprism-m1c-tests", Guid.NewGuid().ToString("N"));

    public M1cConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Defaults_AreUsable()
    {
        var config = AppConfig.CreateDefault();
        var warnings = new List<string>();
        config.Normalize(warnings);

        Assert.Equal(AppConfigSchema.Current, config.Schema);
        Assert.Equal("notify", config.General.SecondInstance);
        Assert.Equal("exit", config.General.CloseAction);
        Assert.Equal("Alt+Shift+A", config.Hotkey.ToggleOverlay);
        Assert.Equal("anime4k-s", config.Effect.Default);
        Assert.Equal("fit", config.Effect.Scaling);
        Assert.True(config.Effect.DirtyRect);
        Assert.True(config.Tray.Enabled);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Toml_RoundTrips_WithSnakeCaseKeys()
    {
        string path = Path.Combine(_dir, "config.toml");
        var config = AppConfig.CreateDefault();
        config.General.SecondInstance = "activate";
        config.General.CloseAction = "tray";
        config.General.AutoRefreshWindows = true;
        config.Hotkey.ToggleOverlay = "Ctrl+Alt+F1";
        config.Profile.Add(new ProfileConfig
        {
            Name = "galgame",
            Class = ["game.exe", "Launcher.exe"],
            Exe = ["*/game.exe"],
            Effect = "anime4k-l",
            Scaling = "fit",
            FollowFocus = false
        });

        ConfigStore.Save(config, path);
        string text = File.ReadAllText(path);
        Assert.Contains("[general]", text);
        Assert.Contains("second_instance = \"activate\"", text);
        Assert.Contains("[[profile]]", text);
        Assert.Contains("class = [", text);

        var loaded = ConfigStore.Load(path);
        Assert.False(loaded.RecoveredFromError);
        Assert.Null(loaded.Error);
        Assert.Equal("activate", loaded.Config.General.SecondInstance);
        Assert.Equal("tray", loaded.Config.General.CloseAction);
        Assert.True(loaded.Config.General.AutoRefreshWindows);
        Assert.Equal("Ctrl+Alt+F1", loaded.Config.Hotkey.ToggleOverlay);
        var profile = Assert.Single(loaded.Config.Profile);
        Assert.Equal("galgame", profile.Name);
        Assert.Equal(["game.exe", "Launcher.exe"], profile.Class);
        Assert.Equal("anime4k-l", profile.Effect);
        Assert.False(profile.FollowFocus);
    }

    [Fact]
    public void HandWritten_Toml_Parses()
    {
        string path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, """
            schema = 1

            [general]
            second_instance = "notify"
            close_action = "exit"
            follow_focus = false
            remember_last = false
            auto_refresh_windows = true
            refresh_interval_ms = 500

            [hotkey]
            toggle_overlay = "Alt+Shift+A"

            [logging]
            path = ""
            level = "debug"

            [display]
            monitor = "HDMI-A-1"

            [effect]
            default = "anime4k-m"
            scaling = "stretch"
            dirty_rect = false

            [tray]
            enabled = false

            [input]
            mode = "2d"

            [diagnostics]
            developer_mode = true

            [last]
            effect = "anime4k-l"
            scaling = "fit"
            monitor = "auto"

            [[profile]]
            name = "test"
            class = ["testpattern"]
            effect = "anime4k-s"
            """);

        var loaded = ConfigStore.Load(path);
        Assert.False(loaded.RecoveredFromError);
        Assert.False(loaded.Config.General.FollowFocus);
        Assert.False(loaded.Config.General.RememberLast);
        Assert.True(loaded.Config.General.AutoRefreshWindows);
        Assert.Equal(500, loaded.Config.General.RefreshIntervalMs);
        Assert.Equal("debug", loaded.Config.Logging.Level);
        Assert.Equal("HDMI-A-1", loaded.Config.Display.Monitor);
        Assert.Equal("anime4k-m", loaded.Config.Effect.Default);
        Assert.Equal("stretch", loaded.Config.Effect.Scaling);
        Assert.False(loaded.Config.Effect.DirtyRect);
        Assert.False(loaded.Config.Tray.Enabled);
        Assert.True(loaded.Config.Diagnostics.DeveloperMode);
        Assert.Equal("anime4k-l", loaded.Config.Last.Effect);
        Assert.Single(loaded.Config.Profile);
    }

    [Fact]
    public void BrokenFile_IsBackedUp_AndDefaultsAreUsed()
    {
        string path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "this is not = valid toml [[[");

        var loaded = ConfigStore.Load(path);

        Assert.True(loaded.RecoveredFromError);
        Assert.NotNull(loaded.Error);
        Assert.Equal("notify", loaded.Config.General.SecondInstance);
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains(".bad-"));
    }

    [Fact]
    public void InvalidValues_FallBack_WithWarnings()
    {
        var config = AppConfig.CreateDefault();
        config.General.SecondInstance = "explode";
        config.General.CloseAction = "tray";
        config.Effect.Scaling = "stretch";
        config.Logging.Level = "loud";
        var warnings = new List<string>();
        config.Normalize(warnings);

        Assert.Equal("notify", config.General.SecondInstance);
        Assert.Equal("tray", config.General.CloseAction);
        Assert.Equal("stretch", config.Effect.Scaling);
        Assert.Equal("info", config.Logging.Level);
        Assert.Contains(warnings, w => w.Contains("second_instance"));
        Assert.Contains(warnings, w => w.Contains("logging.level"));
    }

    [Fact]
    public void MissingFile_ReturnsDefaults_WithoutError()
    {
        string path = Path.Combine(_dir, "missing.toml");
        var loaded = ConfigStore.Load(path);
        Assert.False(loaded.FileExisted);
        Assert.False(loaded.RecoveredFromError);
        Assert.Null(loaded.Error);
        Assert.Equal("anime4k-s", loaded.Config.Effect.Default);
    }
}
