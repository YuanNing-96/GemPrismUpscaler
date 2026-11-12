namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Splits a command line entered in the GUI into argv without going through a
/// shell. Supports single/double quotes and backslash escaping; this mirrors
/// the way <c>--nested -- &lt;command...&gt;</c> passes argv directly.
/// </summary>
public static class NestedCommandLine
{
    public static string[] Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingle = false;
        bool inDouble = false;
        bool hasToken = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inSingle)
            {
                if (c == '\'') { inSingle = false; continue; }
                current.Append(c);
                hasToken = true;
                continue;
            }
            if (inDouble)
            {
                if (c == '"') { inDouble = false; continue; }
                if (c == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    if (next is '"' or '\\')
                    {
                        current.Append(next);
                        i++;
                        hasToken = true;
                        continue;
                    }
                }
                current.Append(c);
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    hasToken = true;
                    break;
                case '"':
                    inDouble = true;
                    hasToken = true;
                    break;
                case '\\' when i + 1 < text.Length:
                    current.Append(text[i + 1]);
                    i++;
                    hasToken = true;
                    break;
                default:
                    current.Append(c);
                    hasToken = true;
                    break;
            }
        }

        if (inSingle || inDouble)
            throw new FormatException("命令里的引号没有闭合");

        if (hasToken)
            args.Add(current.ToString());
        return args.ToArray();
    }
}