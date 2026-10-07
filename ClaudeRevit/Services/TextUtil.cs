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

// Accumulates a streamed tool-call name across deltas. Providers stream it three ways: in
// fragments ("get_ful", "l", "_result"), repeated whole in every delta ("create_wall" twice), or
// cumulatively ("crea", "create_wall"). Plain concatenation broke the second ("create_wallcreate_wall");
// skipping any piece the name already ENDS with broke the first ("get_ful" + "l" was dropped,
// giving "get_ful_result"). Only an exact repeat or a cumulative prefix is not appended.
public static class ToolCallName
{
    public static string Merge(string current, string? piece)
    {
        if (string.IsNullOrEmpty(piece)) return current;
        if (current.Length == 0) return piece;
        if (piece == current) return current;
        if (piece.Length > current.Length && piece.StartsWith(current, System.StringComparison.Ordinal)) return piece;
        return current + piece;
    }
}

