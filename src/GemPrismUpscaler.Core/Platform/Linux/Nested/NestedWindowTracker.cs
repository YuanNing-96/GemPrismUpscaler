using System.Text;
using System.Text.Json;
using GemPrismUpscaler.Core.Geometry;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

public sealed record NestedWindowInfo(
    long Id,
    string? Name,
    string? AppId,
    int Pid,
    string Shell,
    RectI Rect,
    RectI Geometry,
    RectI WindowRect,
    bool Floating,
    int FullscreenMode,
    bool Focused,
    int? TransientFor,
    string? WindowType,
    bool Visible,
    ulong X11Window)
{
    public int RequestedWidth => Geometry.Width > 0 ? Geometry.Width : WindowRect.Width > 0 ? WindowRect.Width : Rect.Width;
    public int RequestedHeight => Geometry.Height > 0 ? Geometry.Height : WindowRect.Height > 0 ? WindowRect.Height : Rect.Height;

    public bool LooksLikeDialog =>
        TransientFor is not null ||
        (!string.IsNullOrWhiteSpace(WindowType) &&
         WindowType.Contains("dialog", StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(Name) &&
         (Name.Contains("dialog", StringComparison.OrdinalIgnoreCase) ||
          Name.Contains("popup", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Popup menus, tooltips, notifications, docks and other non-toplevel
    /// surfaces cannot be moved to their own output. They stay on their parent
    /// output and are captured as part of the parent's window area.
    /// </summary>
    public bool LooksLikePopup
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WindowType)) return false;
            string type = WindowType;
            return type.Contains("popup", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("menu", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("tooltip", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("notification", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("combo", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("dnd", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("dock", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("desktop", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsIndependentWindow =>
        Visible && Id > 0 && Rect.Width > 0 && Rect.Height > 0 && !LooksLikePopup;
}

/// <summary>
/// Polls sway's tree and selects the game's main window. Candidate priority is
/// explicit token, then process/PID, then the largest non-dialog surface; the
/// geometry field is the client's requested size even when sway has tiled the
/// window to the output size.
/// </summary>
public sealed class NestedWindowTracker : IDisposable
{
    private readonly SwayIpc _ipc;
    private readonly string _outputName;
    private readonly string _token;
    private readonly int _launcherPid;
    private readonly string? _preferredMain;
    private readonly HashSet<int> _launcherTree;
    private readonly bool _ownsIpc;
    private NestedWindowInfo? _main;
    private List<NestedWindowInfo> _dialogs = new();
    private List<NestedWindowInfo> _allWindows = new();
    private int _stableGeometryWidth;
    private int _stableGeometryHeight;
    private int _stableCount;

    public NestedWindowInfo? MainWindow => _main;
    public long Generation { get; private set; }
    public int LastCandidateCount { get; private set; }
    public RectI? GameBounds { get; private set; }
    public string? GameShell { get; private set; }
    public IReadOnlyList<NestedWindowInfo> Dialogs => _dialogs;
    public bool HasDialog => _dialogs.Count > 0;
    public bool HasGameWindows => _allWindows.Count > 0;

    /// <summary>
    /// Every game toplevel that can own an independent virtual output. The
    /// selection starts from the main window and follows token, PID, process
    /// tree and transient-for relationships. Popup surfaces are excluded.
    /// </summary>
    public IReadOnlyList<NestedWindowInfo> AllWindows => _allWindows;

    public NestedWindowTracker(SwayIpc ipc, string outputName, string token, int launcherPid,
        string? preferredMain = null, bool ownsIpc = true)
    {
        _ipc = ipc;
        _outputName = outputName;
        _token = token;
        _launcherPid = launcherPid;
        _preferredMain = preferredMain;
        _ownsIpc = ownsIpc;
        _launcherTree = CollectProcessTree(launcherPid);
    }

    /// <summary>Parse a raw sway get_tree response (used by tests and callers).</summary>
    public static IReadOnlyList<NestedWindowInfo> ParseWindows(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<NestedWindowInfo>();
        Walk(document.RootElement, result);
        return result;
    }

    /// <summary>
    /// Refresh the tracked toplevel set. No window is moved or focused here;
    /// the presentation layer owns output assignment and focus semantics.
    /// </summary>
    public NestedWindowInfo? Poll(bool allowFullscreen = true)
    {
        _ = allowFullscreen;
        using var tree = _ipc.GetTree();
        var candidates = new List<NestedWindowInfo>();
        Walk(tree.RootElement, candidates);
        LastCandidateCount = candidates.Count;
        var next = Select(candidates);
        bool changed = !SameWindow(_main, next) ||
                       !SameWindowSet(_allWindows, next, candidates);
        _main = next;
        _allWindows = BuildAllWindows(candidates, next);
        _dialogs = _allWindows
            .Where(w => w.Id != next?.Id && (w.LooksLikeDialog || w.TransientFor is not null))
            .ToList();

        if (_allWindows.Count > 0)
        {
            var first = _allWindows[0];
            GameBounds = Union(first.Rect, _allWindows.Skip(1).ToList());
            GameShell = next?.Shell ?? first.Shell;
        }
        else
        {
            GameBounds = null;
            GameShell = next?.Shell;
        }

        if (changed) Generation++;
        return next;
    }

    public bool FocusWindow(long conId)
        => conId > 0 && _ipc.RunCommand($"[con_id={conId}] focus");

    public bool MoveToOutput(long conId, string outputName)
        => conId > 0 && !string.IsNullOrWhiteSpace(outputName) &&
           _ipc.RunCommand($"[con_id={conId}] move container to output {outputName}");

    public bool FloatWindow(long conId)
        => conId > 0 && _ipc.RunCommand($"[con_id={conId}] floating enable");

    /// <summary>Ask sway to put the main window back at an exact size.</summary>
    public bool ResizeMainWindow(int width, int height)
    {
        if (_main is null || width < 320 || height < 240) return false;
        _ipc.RunCommand($"[con_id={_main.Id}] floating enable");
        _ipc.RunCommand($"[con_id={_main.Id}] resize set {width} {height}");
        return true;
    }

    /// <summary>
    /// True when the requested client size has been stable for two polls. The
    /// caller should resize the nested output to this size and then reset.
    /// </summary>
    public bool TryTakeStableGeometry(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (_main is null) return false;
        int w = _main.RequestedWidth;
        int h = _main.RequestedHeight;
        if (w < 320 || h < 240) return false;

        if (w == _stableGeometryWidth && h == _stableGeometryHeight)
        {
            _stableCount++;
        }
        else
        {
            _stableGeometryWidth = w;
            _stableGeometryHeight = h;
            _stableCount = 0;
        }

        if (_stableCount < 1) return false;
        width = w;
        height = h;
        _stableCount = 0;
        return true;
    }

    public bool ResizeOutput(int width, int height)
    {
        bool ok = _ipc.RunCommand($"output {_outputName} resolution {width}x{height}");
        return ok;
    }

    private List<NestedWindowInfo> BuildAllWindows(
        List<NestedWindowInfo> candidates, NestedWindowInfo? main)
        => SelectAllWindows(candidates, main, pid => HasToken(pid, _token), _launcherTree);

    internal static List<NestedWindowInfo> SelectAllWindows(
        List<NestedWindowInfo> candidates,
        NestedWindowInfo? main,
        Func<int, bool> hasToken,
        HashSet<int> launcherTree)
    {
        var selected = new Dictionary<long, NestedWindowInfo>();
        if (main is not null) selected[main.Id] = main;

        bool changed = true;
        for (int pass = 0; pass < 8 && changed; pass++)
        {
            changed = false;
            foreach (var candidate in candidates)
            {
                if (!candidate.IsIndependentWindow || selected.ContainsKey(candidate.Id))
                    continue;

                bool related = false;
                if (main is not null)
                {
                    related =
                        candidate.TransientFor == main.Id ||
                        (candidate.Pid > 0 && main.Pid > 0 && candidate.Pid == main.Pid) ||
                        hasToken(candidate.Pid) ||
                        launcherTree.Contains(candidate.Pid) ||
                        (candidate.Pid == 0 && main.Pid == 0 &&
                         string.Equals(candidate.Shell, main.Shell, StringComparison.Ordinal));
                }
                else
                {
                    related = hasToken(candidate.Pid) ||
                              launcherTree.Contains(candidate.Pid) ||
                              candidate.LooksLikeDialog;
                }

                if (!related)
                {
                    foreach (var known in selected.Values)
                    {
                        if (candidate.TransientFor == known.Id ||
                            known.TransientFor == candidate.Id ||
                            (candidate.Pid > 0 && candidate.Pid == known.Pid))
                        {
                            related = true;
                            break;
                        }
                    }
                }

                if (related)
                {
                    selected[candidate.Id] = candidate;
                    changed = true;
                }
            }
        }

        if (main is null)
            return selected.Values.OrderBy(w => w.Id).ToList();

        var result = new List<NestedWindowInfo>(selected.Count) { main };
        result.AddRange(selected.Values
            .Where(w => w.Id != main.Id)
            .OrderBy(w => w.LooksLikeDialog ? 1 : 0)
            .ThenBy(w => w.Id));
        return result;
    }

    private static bool SameWindowSet(
        IReadOnlyList<NestedWindowInfo> previous,
        NestedWindowInfo? main,
        List<NestedWindowInfo> candidates)
    {
        if (previous.Count == 0)
            return main is null;
        if (main is null || previous[0].Id != main.Id)
            return false;
        var previousIds = previous.Select(w => w.Id).OrderBy(id => id);
        var candidateIds = candidates
            .Where(w => w.IsIndependentWindow)
            .Select(w => w.Id)
            .OrderBy(id => id);
        return previousIds.SequenceEqual(candidateIds);
    }

    private static RectI Union(RectI first, List<NestedWindowInfo> others)
    {
        int x = first.X;
        int y = first.Y;
        int right = first.Right;
        int bottom = first.Bottom;
        foreach (var other in others)
        {
            x = Math.Min(x, other.Rect.X);
            y = Math.Min(y, other.Rect.Y);
            right = Math.Max(right, other.Rect.Right);
            bottom = Math.Max(bottom, other.Rect.Bottom);
        }
        return new RectI(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private NestedWindowInfo? Select(List<NestedWindowInfo> candidates)
    {
        var usable = candidates
            .Where(c => !c.LooksLikeDialog && c.IsIndependentWindow)
            .ToList();
        if (usable.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(_preferredMain))
        {
            var preferred = usable.Where(c =>
                (c.AppId?.Contains(_preferredMain, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (c.Name?.Contains(_preferredMain, StringComparison.OrdinalIgnoreCase) ?? false) ||
                c.Pid.ToString() == _preferredMain).ToList();
            if (preferred.Count > 0)
                return preferred.OrderByDescending(c => c.Focused)
                    .ThenByDescending(c => c.RequestedWidth * (long)c.RequestedHeight).First();
        }

        var tokenMatches = usable.Where(c => HasToken(c.Pid, _token)).ToList();
        if (tokenMatches.Count > 0)
            return tokenMatches.OrderByDescending(c => c.RequestedWidth * (long)c.RequestedHeight).First();

        var treeMatches = usable.Where(c => _launcherTree.Contains(c.Pid)).ToList();
        if (treeMatches.Count > 0)
            return treeMatches.OrderByDescending(c => c.RequestedWidth * (long)c.RequestedHeight).First();

        return usable
            .OrderByDescending(c => c.Focused)
            .ThenByDescending(c => (long)c.RequestedWidth * c.RequestedHeight)
            .First();
    }

    private static bool HasToken(int pid, string token)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            byte[] data = File.ReadAllBytes($"/proc/{pid}/environ");
            string text = Encoding.UTF8.GetString(data);
            foreach (string entry in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (entry.StartsWith("GEMPRISM_NESTED_TOKEN=", StringComparison.Ordinal) &&
                    entry["GEMPRISM_NESTED_TOKEN=".Length..] == token)
                    return true;
            }
        }
        catch
        {
            // The process may exit between the tree snapshot and /proc read.
        }
        return false;
    }

    private static HashSet<int> CollectProcessTree(int root)
    {
        var result = new HashSet<int>();
        if (root <= 0) return result;
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (!result.Add(pid)) continue;
            try
            {
                string children = File.ReadAllText($"/proc/{pid}/task/{pid}/children");
                foreach (string token in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(token, out int child)) queue.Enqueue(child);
            }
            catch { }
        }
        return result;
    }

    private static void Walk(JsonElement node, List<NestedWindowInfo> output)
    {
        string type = GetString(node, "type") ?? string.Empty;
        if (type is "con" or "floating_con")
        {
            var info = ParseWindow(node);
            if (info is not null) output.Add(info);
        }

        if (node.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            foreach (var child in nodes.EnumerateArray()) Walk(child, output);
        if (node.TryGetProperty("floating_nodes", out var floating) && floating.ValueKind == JsonValueKind.Array)
            foreach (var child in floating.EnumerateArray()) Walk(child, output);
    }

    private static NestedWindowInfo? ParseWindow(JsonElement node)
    {
        long id = GetLong(node, "id") ?? 0;
        if (id == 0) return null;
        string shell = GetString(node, "shell") ?? string.Empty;
        int pid = (int)(GetLong(node, "pid") ?? 0);
        var rect = GetRect(node, "rect");
        var geometry = GetRect(node, "geometry");
        var windowRect = GetRect(node, "window_rect");
        var properties = node.TryGetProperty("window_properties", out var p) &&
                         p.ValueKind == JsonValueKind.Object ? p : default;
        string? windowType = properties.ValueKind == JsonValueKind.Object
            ? GetString(properties, "window_type") : null;
        int? transientFor = properties.ValueKind == JsonValueKind.Object
            ? (int?)(GetLong(properties, "transient_for")) : null;

        return new NestedWindowInfo(
            id,
            GetString(node, "name"),
            GetString(node, "app_id"),
            pid,
            shell,
            rect,
            geometry,
            windowRect,
            GetBool(node, "floating"),
            (int)(GetLong(node, "fullscreen_mode") ?? 0),
            GetBool(node, "focused"),
            transientFor,
            windowType,
            node.TryGetProperty("visible", out var visible) ? visible.GetBoolean() : true,
            (ulong)(GetLong(node, "window") ?? 0));
    }

    private static bool SameWindow(NestedWindowInfo? a, NestedWindowInfo? b)
        => a?.Id == b?.Id &&
           a?.RequestedWidth == b?.RequestedWidth &&
           a?.RequestedHeight == b?.RequestedHeight &&
           a?.FullscreenMode == b?.FullscreenMode;

    private static RectI GetRect(JsonElement node, string name)
    {
        if (node.TryGetProperty(name, out var rect) && rect.ValueKind == JsonValueKind.Object)
            return new RectI(
                (int)(GetLong(rect, "x") ?? 0),
                (int)(GetLong(rect, "y") ?? 0),
                (int)(GetLong(rect, "width") ?? 0),
                (int)(GetLong(rect, "height") ?? 0));
        return default;
    }

    private static string? GetString(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? GetLong(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out long result) ? result : null;

    private static bool GetBool(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public void Dispose()
    {
        if (_ownsIpc) _ipc.Dispose();
    }
}