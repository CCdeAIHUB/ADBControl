using System.Text.RegularExpressions;

namespace ADBControl.Desktop.Services;

public enum TerminalTokenKind
{
    Plain,
    Command,
    Option,
    String,
    Operator,
}

public sealed record TerminalToken(string Text, TerminalTokenKind Kind);

public static class TerminalSyntaxHighlighter
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "am", "cat", "cmd", "dumpsys", "getprop", "grep", "input", "logcat", "ls", "pm", "settings", "svc", "wm",
    };

    public static IReadOnlyList<TerminalToken> Tokenize(string command)
    {
        var tokens = new List<TerminalToken>();
        var commandSeen = false;
        foreach (Match match in Regex.Matches(command ?? string.Empty, """'[^']*'|"[^"]*"|&&|\|\||[|;]|\S+"""))
        {
            var text = match.Value;
            var kind = text is "|" or "||" or "&&" or ";"
                ? TerminalTokenKind.Operator
                : text.StartsWith("-", StringComparison.Ordinal)
                    ? TerminalTokenKind.Option
                    : text.StartsWith("\"", StringComparison.Ordinal) || text.StartsWith("'", StringComparison.Ordinal)
                        ? TerminalTokenKind.String
                        : !commandSeen && Commands.Contains(text)
                            ? TerminalTokenKind.Command
                            : TerminalTokenKind.Plain;
            if (kind == TerminalTokenKind.Command)
                commandSeen = true;
            else if (!commandSeen && kind == TerminalTokenKind.Plain)
                commandSeen = true;
            tokens.Add(new TerminalToken(text, kind));
        }

        return tokens;
    }

    public static bool IsErrorLine(string line)
    {
        return line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("exception", StringComparison.OrdinalIgnoreCase);
    }
}
