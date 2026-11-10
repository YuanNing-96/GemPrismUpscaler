using System.Text.Json;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

public sealed record SwayOutputInfo(
    string Name,
    RectI Rect,
    bool Active,
    int ModeWidth,
    int ModeHeight)
{
    public int Width => Rect.Width > 0 ? Rect.Width : ModeWidth;
    public int Height => Rect.Height > 0 ? Rect.Height : ModeHeight;
}

/// <summary>
/// Owns the headless outputs used for per-window capture. There is exactly one
/// spare output while a game is running, so a newly created toplevel never has
/// to share an output with an existing window:
///
///   outputs = used outputs + 1 spare
///
/// Every output uses the physical monitor resolution. Sway lays the outputs out
/// from left to right; callers must use the real rect from get_outputs instead
/// of assuming an origin.
/// </summary>
public sealed class SwayOutputPool
{
    private const int MaxTransientOutputs = 512;

    private readonly SwayIpc _ipc;
    private readonly string _baseOutput;
    private readonly int _width;
    private readonly int _height;
    private readonly int _refreshMilliHz;
    private readonly List<SwayOutputInfo> _outputs = new();
    private int _nextIndex;

    public SwayOutputPool(SwayIpc ipc, string baseOutput, int width, int height,
        int refreshMilliHz = 0)
    {
        if (width is < 320 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 240 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (refreshMilliHz is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(refreshMilliHz));
        _ipc = ipc;
        _baseOutput = string.IsNullOrWhiteSpace(baseOutput) ? SwayConfig.DefaultOutput : baseOutput;
        _width = width;
        _height = height;
        _refreshMilliHz = refreshMilliHz;
    }

    public string BaseOutput => _baseOutput;
    public int Width => _width;
    public int Height => _height;
    public int RefreshMilliHz => _refreshMilliHz;
    public IReadOnlyList<SwayOutputInfo> Outputs => _outputs;

    public RectI LayoutBounds
    {
        get
        {
            if (_outputs.Count == 0) return new RectI(0, 0, _width, _height);
            int x = _outputs[0].Rect.X;
            int y = _outputs[0].Rect.Y;
            int right = _outputs[0].Rect.Right;
            int bottom = _outputs[0].Rect.Bottom;
            foreach (var output in _outputs)
            {
                x = Math.Min(x, output.Rect.X);
                y = Math.Min(y, output.Rect.Y);
                right = Math.Max(right, output.Rect.Right);
                bottom = Math.Max(bottom, output.Rect.Bottom);
            }
            return new RectI(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
        }
    }

    /// <summary>
    /// Validate the configured base output and make sure it has the monitor
    /// resolution. The base output is always kept; it can later become the
    /// spare output while another output is used by a window.
    /// </summary>
    public void Initialize()
    {
        Refresh();
        var baseInfo = Find(_baseOutput);
        if (baseInfo is null)
            throw new InvalidOperationException(
                $"nested base output '{_baseOutput}' is missing");
        if (baseInfo.Width != _width || baseInfo.Height != _height)
            SetResolution(_baseOutput, _width, _height);
        Refresh();
        _nextIndex = Math.Max(2, NextIndex());
        Log.Info($"nested outputs: pool initialized base='{_baseOutput}' " +
                 $"{_width}x{_height} outputs=[{Describe()}]");
    }

    /// <summary>Pick a free output for a newly appeared window.</summary>
    public SwayOutputInfo Acquire(ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        Refresh();
        var free = _outputs
            .Where(o => o.Active && !used.Contains(o.Name))
            .OrderBy(o => o.Name == _baseOutput ? 0 : 1)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .ToList();
        SwayOutputInfo info;
        if (free.Count > 0)
        {
            info = free[0];
        }
        else
        {
            info = Create(NextName());
        }

        used.Add(info.Name);
        Log.Info($"nested outputs: acquired '{info.Name}' origin={info.Rect} used={used.Count}");
        return info;
    }

    /// <summary>
    /// Keep exactly one unassigned output. Extra free transient outputs are
    /// removed, and a new output is created when every output is in use.
    /// </summary>
    public void EnsureSpare(ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        Refresh();
        var free = _outputs
            .Where(o => o.Active && !used.Contains(o.Name))
            .OrderBy(o => o.Name == _baseOutput ? 0 : 1)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

        if (free.Count == 0)
        {
            var created = Create(NextName());
            Log.Info($"nested outputs: spare created '{created.Name}' " +
                     $"total={_outputs.Count} used={used.Count}");
            return;
        }

        // Prefer keeping the base output as the spare; unplug extra transient
        // outputs first. A spare transient output is fine when base is in use.
        foreach (var output in free.Skip(1))
        {
            if (output.Name == _baseOutput) continue;
            Unplug(output.Name);
        }
        Refresh();
    }

    /// <summary>
    /// Release one window's output. When other windows remain, the released
    /// output is kept as the spare instead of immediately unplugging and
    /// creating another output. That avoids an unnecessary XWayland RandR
    /// hotplug cycle on every dialog close.
    /// </summary>
    public void Release(string name, ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        used.Remove(name);
        if (used.Count > 0) EnsureSpare(used);
        else TrimToBase();
    }

    /// <summary>No game window remains: only the base output is needed.</summary>
    public void TrimToBase()
    {
        Refresh();
        foreach (var output in _outputs.ToList())
        {
            if (output.Name == _baseOutput) continue;
            Unplug(output.Name);
        }
        Refresh();
    }

    public SwayOutputInfo? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _outputs.FirstOrDefault(o =>
            string.Equals(o.Name, name, StringComparison.Ordinal));
    }

    public void Refresh()
    {
        using var document = _ipc.GetOutputs();
        var parsed = ParseOutputs(document.RootElement.GetRawText());
        _outputs.Clear();
        _outputs.AddRange(parsed);
        // Headless output names are allocated monotonically by the backend.
        // Reusing a previously unplugged name is not reliable: creating
        // "HEADLESS-2" after unplugging it can yield "HEADLESS-3". Keep a
        // high-water mark and always use the name that actually appeared.
        _nextIndex = Math.Max(_nextIndex, Math.Max(2, NextIndex()));
    }

    public static IReadOnlyList<SwayOutputInfo> ParseOutputs(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<SwayOutputInfo>();

        var result = new List<SwayOutputInfo>();
        foreach (var node in document.RootElement.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            string? name = node.TryGetProperty("name", out var nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var rect = GetRect(node, "rect");
            var mode = GetRect(node, "current_mode");
            bool active = !node.TryGetProperty("active", out var activeElement) ||
                          activeElement.ValueKind != JsonValueKind.False;
            if (rect.Width <= 0 && mode.Width > 0)
                rect = new RectI(rect.X, rect.Y, mode.Width, mode.Height);
            result.Add(new SwayOutputInfo(name, rect, active, mode.Width, mode.Height));
        }
        return result;
    }

    private SwayOutputInfo Create(string requestedName)
    {
        if (_outputs.Count >= MaxTransientOutputs)
            throw new InvalidOperationException(
                $"nested output pool limit reached ({MaxTransientOutputs})");
        if (_nextIndex >= MaxTransientOutputs)
            throw new InvalidOperationException(
                $"nested output name limit reached ({MaxTransientOutputs})");

        Refresh();
        var before = _outputs.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        if (!_ipc.RunCommand($"create_output {requestedName}"))
            throw new InvalidOperationException($"sway create_output '{requestedName}' failed");

        // The headless backend chooses the actual name. A previously unplugged
        // name may be skipped entirely, so wait for any new output rather than
        // looking for the requested one.
        string? actual = null;
        long deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            Refresh();
            actual = _outputs.Select(o => o.Name).FirstOrDefault(n => !before.Contains(n));
            if (actual is not null) break;
            Thread.Sleep(20);
        }
        if (actual is null)
            throw new InvalidOperationException(
                $"sway create_output '{requestedName}' produced no new output");

        SetResolution(actual, _width, _height);
        if (!WaitForSize(actual, _width, _height, TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException(
                $"sway output '{actual}' did not reach {_width}x{_height}");

        Refresh();
        var info = Find(actual) ?? throw new InvalidOperationException(
            $"sway output '{actual}' disappeared after creation");
        _nextIndex = Math.Max(_nextIndex, ParseIndex(actual) + 1);
        Log.Info($"nested outputs: created requested='{requestedName}' actual='{actual}' " +
                 $"rect={info.Rect} total={_outputs.Count}");
        return info;
    }

    private void Unplug(string name)
    {
        if (string.Equals(name, _baseOutput, StringComparison.Ordinal)) return;
        if (Find(name) is null) return;
        if (!_ipc.RunCommand($"output {name} unplug"))
        {
            Log.Warn($"nested outputs: unplug '{name}' failed");
            return;
        }
        long deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            Refresh();
            if (Find(name) is null)
            {
                Log.Info($"nested outputs: unplugged '{name}' total={_outputs.Count}");
                return;
            }
            Thread.Sleep(20);
        }
        Log.Warn($"nested outputs: '{name}' still present after unplug");
    }

    private void SetResolution(string name, int width, int height)
    {
        string mode = SwayConfig.Mode(width, height, _refreshMilliHz);
        if (!_ipc.RunCommand($"output {name} resolution {mode}"))
            throw new InvalidOperationException(
                $"sway output '{name}' resolution {mode} failed");
    }

    private bool WaitForSize(string name, int width, int height, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Refresh();
            var info = Find(name);
            if (info is not null && info.Width == width && info.Height == height)
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private string NextName()
    {
        for (int i = Math.Max(2, _nextIndex); i < MaxTransientOutputs; i++)
        {
            string candidate = $"HEADLESS-{i}";
            if (Find(candidate) is null)
            {
                _nextIndex = i + 1;
                return candidate;
            }
        }
        throw new InvalidOperationException("no free nested output name");
    }

    private int NextIndex()
    {
        int max = 1;
        foreach (var output in _outputs)
        {
            string name = output.Name;
            if (!name.StartsWith("HEADLESS-", StringComparison.Ordinal)) continue;
            if (int.TryParse(name.AsSpan("HEADLESS-".Length), out int index))
                max = Math.Max(max, index);
        }
        return max + 1;
    }

    private static int ParseIndex(string name)
    {
        if (name.StartsWith("HEADLESS-", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan("HEADLESS-".Length), out int index))
            return index;
        return 1;
    }

    private string Describe()
        => string.Join(", ", _outputs.Select(o => $"{o.Name}:{o.Rect}"));

    private static RectI GetRect(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var rect) ||
            rect.ValueKind != JsonValueKind.Object)
            return default;
        return new RectI(
            GetInt(rect, "x"),
            GetInt(rect, "y"),
            GetInt(rect, "width"),
            GetInt(rect, "height"));
    }

    private static int GetInt(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out int result)
            ? result
            : 0;
}