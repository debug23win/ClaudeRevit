using System.Text;
namespace ClaudeRevit.Services;

public static class CsvEvidence
{
    public static IReadOnlyList<string[]> Parse(string text)
    {
        var rows = new List<string[]>(); var cells = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else if (!quoted && c is '\r' or '\n')
            { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; cells.Add(cell.ToString()); rows.Add(cells.ToArray()); cells.Clear(); cell.Clear(); }
            else cell.Append(c);
        }
        if (quoted) throw new FormatException("Unterminated quoted CSV cell.");
        if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString()); rows.Add(cells.ToArray()); }
        return rows;
    }
    public static bool ContainsRows(IReadOnlyList<string[]> exported, IReadOnlyList<string[]> expected)
    {
        int index = 0;
        foreach (var row in expected.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))))
        {
            while (index < exported.Count && !row.Select(Normalize).SequenceEqual(exported[index].Select(Normalize))) index++;
            if (index == exported.Count) return false;
            index++;
        }
        return expected.Any(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
    }
    private static string Normalize(string value) => value.Trim().TrimStart('\uFEFF').Replace("\r\n", "\n");
}
