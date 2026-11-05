namespace GemPrismUpscaler.Core.Configuration;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Super = 8
}

/// <summary>
/// Parsed global hotkey such as "Alt+Shift+A". The parser is deliberately
/// platform-neutral; X11 maps <see cref="Key"/> through XStringToKeysym.
/// </summary>
public sealed record HotkeyBinding(HotkeyModifiers Modifiers, string Key)
{
    public static HotkeyBinding Default { get; } = new(
        HotkeyModifiers.Alt | HotkeyModifiers.Shift, "A");

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Super)) parts.Add("Super");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out HotkeyBinding binding, out string? error)
    {
        binding = Default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键不能为空";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "热键不能为空";
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (string raw in parts)
        {
            string token = raw.Trim();
            if (TryModifier(token, out var modifier))
            {
                if (modifiers.HasFlag(modifier))
                {
                    error = $"修饰键重复：{token}";
                    return false;
                }
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                error = $"只能有一个主键：{key} / {token}";
                return false;
            }
            key = token;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            error = "缺少主键";
            return false;
        }

        if (!TryNormalizeKey(key, out string normalized, out bool isFunctionKey))
        {
            error = $"不认识的主键：{key}";
            return false;
        }

        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            error = "至少需要一个修饰键（F1–F24 除外）";
            return false;
        }

        binding = new HotkeyBinding(modifiers, normalized);
        return true;
    }

    public static HotkeyBinding Parse(string? text)
    {
        if (TryParse(text, out var binding, out string? error))
            return binding;
        throw new ArgumentException(error ?? "invalid hotkey", nameof(text));
    }

    private static bool TryModifier(string token, out HotkeyModifiers modifier)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                modifier = HotkeyModifiers.Control;
                return true;
            case "alt":
            case "mod1":
                modifier = HotkeyModifiers.Alt;
                return true;
            case "shift":
                modifier = HotkeyModifiers.Shift;
                return true;
            case "super":
            case "meta":
            case "win":
            case "cmd":
                modifier = HotkeyModifiers.Super;
                return true;
            default:
                modifier = HotkeyModifiers.None;
                return false;
        }
    }

    private static bool TryNormalizeKey(string token, out string normalized, out bool isFunctionKey)
    {
        isFunctionKey = false;
        normalized = token;

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                normalized = c.ToString();
                return true;
            }
            if (TryPunctuation(token[0], out normalized))
                return true;
            return false;
        }

        if (token.Length is >= 2 and <= 3 &&
            (token[0] is 'F' or 'f') &&
            int.TryParse(token[1..], out int function) &&
            function is >= 1 and <= 24)
        {
            normalized = $"F{function}";
            isFunctionKey = true;
            return true;
        }

        if (KeyNames.TryGetValue(token.ToLowerInvariant(), out string? name))
        {
            normalized = name;
            isFunctionKey = token.StartsWith("f", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(token[1..], out _);
            return true;
        }

        return false;
    }

    private static bool TryPunctuation(char c, out string name)
    {
        name = c switch
        {
            '-' => "minus",
            '=' => "equal",
            ',' => "comma",
            '.' => "period",
            '/' => "slash",
            ';' => "semicolon",
            '\'' => "apostrophe",
            '[' => "bracketleft",
            ']' => "bracketright",
            '\\' => "backslash",
            '`' => "grave",
            _ => string.Empty
        };
        return name.Length > 0;
    }

    private static readonly Dictionary<string, string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "space",
        ["tab"] = "Tab",
        ["enter"] = "Return",
        ["return"] = "Return",
        ["escape"] = "Escape",
        ["esc"] = "Escape",
        ["backspace"] = "BackSpace",
        ["delete"] = "Delete",
        ["del"] = "Delete",
        ["insert"] = "Insert",
        ["ins"] = "Insert",
        ["home"] = "Home",
        ["end"] = "End",
        ["pageup"] = "Prior",
        ["pgup"] = "Prior",
        ["pagedown"] = "Next",
        ["pgdn"] = "Next",
        ["left"] = "Left",
        ["right"] = "Right",
        ["up"] = "Up",
        ["down"] = "Down",
        ["minus"] = "minus",
        ["equal"] = "equal",
        ["comma"] = "comma",
        ["period"] = "period",
        ["slash"] = "slash",
        ["semicolon"] = "semicolon",
        ["apostrophe"] = "apostrophe",
        ["bracketleft"] = "bracketleft",
        ["bracketright"] = "bracketright",
        ["backslash"] = "backslash",
        ["grave"] = "grave",
        ["print"] = "Print",
        ["pause"] = "Pause",
        ["scrolllock"] = "Scroll_Lock",
        ["numlock"] = "Num_Lock",
        ["capslock"] = "Caps_Lock"
    };
}
