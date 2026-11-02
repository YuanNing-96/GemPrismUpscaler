using GemPrismUpscaler.Core.Engine;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class OverlayVisibilityTests
{
    [Fact]
    public void DefaultIsVisible()
    {
        var v = new OverlayVisibility();
        Assert.True(v.IsVisible);
        Assert.Equal(OverlayHideReason.None, v.HideReason);
    }

    [Theory]
    [InlineData(nameof(OverlayVisibility.UserHidden), OverlayHideReason.User)]
    [InlineData(nameof(OverlayVisibility.SourceSuspended), OverlayHideReason.SourceUnmapped)]
    [InlineData(nameof(OverlayVisibility.PopupHidden), OverlayHideReason.Popup)]
    [InlineData(nameof(OverlayVisibility.FocusLost), OverlayHideReason.FocusLost)]
    public void EachConditionHidesOverlay(string property, OverlayHideReason expected)
    {
        var v = new OverlayVisibility();
        typeof(OverlayVisibility).GetProperty(property)!.SetValue(v, true);
        Assert.False(v.IsVisible);
        Assert.Equal(expected, v.HideReason);
    }

    [Fact]
    public void UserHiddenWinsAndClearingItRestoresOnlyWhenOthersClear()
    {
        var v = new OverlayVisibility { UserHidden = true, PopupHidden = true };
        Assert.Equal(OverlayHideReason.User, v.HideReason);

        v.UserHidden = false;
        Assert.False(v.IsVisible);
        Assert.Equal(OverlayHideReason.Popup, v.HideReason);

        v.PopupHidden = false;
        Assert.True(v.IsVisible);
    }

    [Fact]
    public void SourceMinimizeHidesEvenWhenPopupClosed()
    {
        var v = new OverlayVisibility { PopupHidden = false, SourceSuspended = true, FocusLost = true };
        Assert.False(v.IsVisible);
        Assert.Equal(OverlayHideReason.SourceUnmapped, v.HideReason);
    }

    [Fact]
    public void ResetClearsAllReasons()
    {
        var v = new OverlayVisibility { UserHidden = true, SourceSuspended = true, PopupHidden = true, FocusLost = true };
        v.Reset();
        Assert.True(v.IsVisible);
        Assert.Equal(OverlayHideReason.None, v.HideReason);
    }
}
