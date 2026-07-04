namespace ClaudeRevit.Services;

// The single Truncate implementation (review found four drifting copies). The suffix
// reports how much was cut so log readers know the size of what they didn't see.
internal static class TextUtil
{
    public static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + $"… ({s.Length - max} more chars)";

    public static string? TruncateOrNull(string? s, int max) =>
        s == null ? null : Truncate(s, max);
}
