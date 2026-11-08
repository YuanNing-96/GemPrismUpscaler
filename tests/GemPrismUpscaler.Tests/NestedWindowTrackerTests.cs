using GemPrismUpscaler.Core.Platform.Linux.Nested;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class NestedWindowTrackerTests
{
    private const string Tree = """
        {
          "type": "root",
          "nodes": [
            {
              "type": "con", "id": 7, "name": "Game", "app_id": null,
              "pid": 123, "shell": "xwayland", "visible": true,
              "focused": true, "fullscreen_mode": 0, "floating": "auto_off",
              "rect": {"x":0,"y":0,"width":1280,"height":720},
              "window_rect": {"x":0,"y":0,"width":1280,"height":720},
              "geometry": {"x":0,"y":0,"width":1024,"height":768},
              "window_properties": {
                "title":"Game", "class":"game", "transient_for": null,
                "window_type": null
              }
            },
            {
              "type": "floating_con", "id": 8, "name": "Dialog", "app_id": null,
              "pid": 123, "shell": "xwayland", "visible": true,
              "focused": false, "fullscreen_mode": 0, "floating": "user_on",
              "rect": {"x":100,"y":100,"width":320,"height":200},
              "window_rect": {"x":0,"y":0,"width":320,"height":200},
              "geometry": {"x":100,"y":100,"width":320,"height":200},
              "window_properties": {
                "title":"Dialog", "transient_for": 7, "window_type":"dialog"
              }
            }
          ]
        }
        """;

    [Fact]
    public void ParseWindows_HandlesNullTransientAndDialog()
    {
        var windows = NestedWindowTracker.ParseWindows(Tree);
        Assert.Equal(2, windows.Count);
        var game = windows[0];
        Assert.Equal(1024, game.RequestedWidth);
        Assert.Equal(768, game.RequestedHeight);
        Assert.False(game.LooksLikeDialog);
        Assert.True(windows[1].LooksLikeDialog);
        Assert.Equal(7, windows[1].TransientFor);
    }

    [Fact]
    public void ParseWindows_EmptyAndMalformedShape_IsSafe()
    {
        var windows = NestedWindowTracker.ParseWindows("""{"type":"root","nodes":[]}""");
        Assert.Empty(windows);
    }
}

public class NestedWindowSelectionTests
{
    private const string Tree = """
        {
          "type": "root",
          "nodes": [
            {
              "type": "con", "id": 7, "name": "Game", "app_id": null,
              "pid": 123, "shell": "xwayland", "visible": true,
              "focused": true, "fullscreen_mode": 0, "floating": "auto_off",
              "rect": {"x":0,"y":0,"width":1280,"height":720},
              "window_rect": {"x":0,"y":0,"width":1280,"height":720},
              "geometry": {"x":0,"y":0,"width":1280,"height":720},
              "window_properties": {"title":"Game","class":"game","transient_for":null,"window_type":null}
            },
            {
              "type": "floating_con", "id": 8, "name": "Exit Dialog", "app_id": null,
              "pid": 123, "shell": "xwayland", "visible": true,
              "focused": false, "fullscreen_mode": 0, "floating": "user_on",
              "rect": {"x":100,"y":100,"width":320,"height":200},
              "window_rect": {"x":0,"y":0,"width":320,"height":200},
              "geometry": {"x":100,"y":100,"width":320,"height":200},
              "window_properties": {"title":"Exit Dialog","transient_for":7,"window_type":"dialog"}
            },
            {
              "type": "floating_con", "id": 9, "name": "Popup", "app_id": null,
              "pid": 123, "shell": "xwayland", "visible": true,
              "focused": false, "fullscreen_mode": 0, "floating": "user_on",
              "rect": {"x":150,"y":150,"width":100,"height":80},
              "window_rect": {"x":0,"y":0,"width":100,"height":80},
              "geometry": {"x":150,"y":150,"width":100,"height":80},
              "window_properties": {"title":"Popup","transient_for":7,"window_type":"popup_menu"}
            },
            {
              "type": "con", "id": 10, "name": "Unrelated", "app_id": null,
              "pid": 999, "shell": "xwayland", "visible": true,
              "focused": false, "fullscreen_mode": 0, "floating": "auto_off",
              "rect": {"x":0,"y":0,"width":640,"height":480},
              "window_rect": {"x":0,"y":0,"width":640,"height":480},
              "geometry": {"x":0,"y":0,"width":640,"height":480},
              "window_properties": {"title":"Unrelated","transient_for":null,"window_type":null}
            }
          ]
        }
        """;

    [Fact]
    public void SelectAllWindows_KeepsMainAndTransientDialog_ExcludesPopupAndUnrelated()
    {
        var candidates = NestedWindowTracker.ParseWindows(Tree).ToList();
        var selected = NestedWindowTracker.SelectAllWindows(
            candidates, candidates[0], _ => false, new HashSet<int>());

        Assert.Equal(new long[] { 7, 8 }, selected.Select(w => w.Id).ToArray());
    }

    [Fact]
    public void SelectAllWindows_TokenCanIncludeAnotherProcessWindow()
    {
        var candidates = NestedWindowTracker.ParseWindows(Tree).ToList();
        var selected = NestedWindowTracker.SelectAllWindows(
            candidates, candidates[0], pid => pid == 999, new HashSet<int>());

        Assert.Contains(selected, w => w.Id == 10);
        Assert.DoesNotContain(selected, w => w.Id == 9);
    }
}
