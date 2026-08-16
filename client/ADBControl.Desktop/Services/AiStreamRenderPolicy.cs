using System.Text;

namespace ADBControl.Desktop.Services;

public static class AiStreamRenderPolicy
{
    public static TimeSpan FlushDelay { get; } = TimeSpan.FromMilliseconds(120);

    public const int DefaultThinkingPreviewCharacters = 2400;

    public static string BuildThinkingPreview(
        string? thinkingText,
        int maximumCharacters = DefaultThinkingPreviewCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        if (string.IsNullOrEmpty(thinkingText) || thinkingText.Length <= maximumCharacters)
            return thinkingText ?? string.Empty;

        return "…\n" + thinkingText[^maximumCharacters..];
    }

    public static string BuildThinkingPreview(
        StringBuilder thinkingText,
        int maximumCharacters = DefaultThinkingPreviewCharacters)
    {
        ArgumentNullException.ThrowIfNull(thinkingText);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        if (thinkingText.Length <= maximumCharacters)
            return thinkingText.ToString();

        return "…\n" + thinkingText.ToString(
            thinkingText.Length - maximumCharacters,
            maximumCharacters);
    }}