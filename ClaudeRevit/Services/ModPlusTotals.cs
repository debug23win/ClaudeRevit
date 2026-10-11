using System.Collections;
using System.Globalization;
using System.Reflection;

namespace ClaudeRevit.Services;

// Reads the fixed public DTO returned by the inspected ModPlus service. No user-selected type,
// method, DLL path or executable source is accepted. Archive I/O and Revit context stay outside.
public static class ModPlusTotals
{
    public sealed record Row(string Name, string StorageType, bool IsText, string Unit, string DisplayValue,
        double? Sum, double? Rounded, double? ScaledRounded, string? SumExact, string? RoundedExact, string? ScaledRoundedExact, bool TextTruncated);
    public sealed record Page(int Total, int Offset, List<Row> Rows, int? NextOffset);

    public static Page Read(IEnumerable items, string query, int offset, int limit, int precision, double coefficient, Action? checkCancelled = null)
    {
        if (offset < 0 || limit is < 1 or > 50 || precision is < 0 or > 15 || !double.IsFinite(coefficient))
            throw new ArgumentException("Totals require offset >= 0, limit 1..50, precision 0..15 and a finite coefficient.");
        var rows = new List<Row>(); var matching = 0;
        foreach (var item in items)
        {
            checkCancelled?.Invoke();
            if (item == null) throw new InvalidOperationException("ModPlus returned a null parameter row.");
            object? Read(string name) => (item.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"ModPlus DTO contract changed: missing {name}.")).GetValue(item);
            var name = Convert.ToString(Read("Name"), CultureInfo.InvariantCulture) ?? "";
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var position = matching++;
            if (position < offset || rows.Count >= limit) continue;
            var text = Convert.ToBoolean(Read("IsText"), CultureInfo.InvariantCulture);
            double? sum = text ? null : Convert.ToDouble(Read("SumRoundedValue"), CultureInfo.InvariantCulture);
            if (sum is { } value && (!double.IsFinite(value) || !double.IsFinite(value * coefficient)))
                throw new InvalidOperationException("Non-finite parameter total: " + name);
            var display = Convert.ToString(Read("DisplayValue"), CultureInfo.InvariantCulture) ?? "";
            var unit = Convert.ToString(Read("Unit"), CultureInfo.InvariantCulture) ?? "";
            var storage = Convert.ToString(Read("StorageType"), CultureInfo.InvariantCulture) ?? "";
            string Bound(string s, int n) => s.Length <= n ? s : s[..n] + "…";
            var rounded = sum.HasValue ? (double?)Math.Round(sum.Value, precision) : null;
            var scaled = sum.HasValue ? (double?)Math.Round(sum.Value * coefficient, precision) : null;
            rows.Add(new(Bound(name, 160), Bound(storage, 40), text, Bound(unit, 160), Bound(display, 256), sum,
                rounded, scaled, sum?.ToString("R", CultureInfo.InvariantCulture), rounded?.ToString("R", CultureInfo.InvariantCulture), scaled?.ToString("R", CultureInfo.InvariantCulture),
                name.Length > 160 || display.Length > 256 || unit.Length > 160 || storage.Length > 40));
        }
        return new(matching, offset, rows, offset + rows.Count < matching ? offset + rows.Count : null);
    }
}
