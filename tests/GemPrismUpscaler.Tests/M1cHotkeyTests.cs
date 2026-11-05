using GemPrismUpscaler.Core.Configuration;
using Xunit;

namespace GemPrismUpscaler.Tests;

public sealed class M1cHotkeyTests
{
    [Theory]
    [InlineData("Alt+Shift+A", HotkeyModifiers.Alt | HotkeyModifiers.Shift, "A", "Alt+Shift+A")]
    [InlineData("Ctrl+Alt+F1", HotkeyModifiers.Control | HotkeyModifiers.Alt, "F1", "Ctrl+Alt+F1")]
    [InlineData("shift+super+pageDown", HotkeyModifiers.Shift | HotkeyModifiers.Super, "Next", "Shift+Super+Next")]
    [InlineData("Control+Alt+Space", HotkeyModifiers.Control | HotkeyModifiers.Alt, "space", "Ctrl+Alt+space")]
    [InlineData("F12", HotkeyModifiers.None, "F12", "F12")]
    public void Parses_ValidHotkeys(string text, HotkeyModifiers modifiers, string key, string canonical)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding, out string? error), error);
        Assert.Equal(modifiers, binding.Modifiers);
        Assert.Equal(key, binding.Key);
        Assert.Equal(canonical, binding.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alt")]
    [InlineData("Alt+Shift")]
    [InlineData("A")]
    [InlineData("Alt+Shift+NotAKey")]
    [InlineData("Alt+Alt+A")]
    [InlineData("Alt+A+B")]
    public void Rejects_InvalidHotkeys(string text)
    {
        Assert.False(HotkeyBinding.TryParse(text, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Default_IsAltShiftA()
    {
        Assert.Equal("Alt+Shift+A", HotkeyBinding.Default.ToString());
        Assert.Equal(HotkeyModifiers.Alt | HotkeyModifiers.Shift, HotkeyBinding.Default.Modifiers);
    }
}
