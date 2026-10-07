using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeRevit.Services;

// Room numbering order and number format, kept free of Revit types so the order (the part users
// argue about) is unit-tested. Idea from the NewLevelHub fork of mcp-servers-for-revit (room
// numbering for Russian documentation); implementation is ours.
public static class RoomNumbering
{
    // Reading order: rows from the top of the plan down, banded by `rowTolerance`, left to right
    // within a row; `snake` alternates direction on every other row (a corridor walk).
    public static List<int> ReadingOrder(IReadOnlyList<(double X, double Y)> points, double rowTolerance, bool snake)
    {
        var byY = Enumerable.Range(0, points.Count).OrderByDescending(i => points[i].Y).ToList();
        var rows = new List<List<int>>();
        double rowTop = double.NaN;
        foreach (var i in byY)
        {
            if (rows.Count == 0 || points[i].Y < rowTop - rowTolerance) { rows.Add(new()); rowTop = points[i].Y; }
            rows[^1].Add(i);
        }
        var order = new List<int>();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r].OrderBy(i => points[i].X).ToList();
            if (snake && r % 2 == 1) row.Reverse();
            order.AddRange(row);
        }
        return order;
    }

    // Order along a polyline route: each point's station (distance along the route to its nearest
    // projection), so numbering follows a corridor however it bends.
    public static List<int> PathOrder(IReadOnlyList<(double X, double Y)> points, IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count < 2) throw new ArgumentException("A numbering path needs at least two points.");
        double Station((double X, double Y) p)
        {
            double best = double.MaxValue, station = 0, run = 0;
            for (int k = 0; k + 1 < path.Count; k++)
            {
                var (ax, ay) = path[k]; var (bx, by) = path[k + 1];
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
                var t = len2 > 0 ? Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / len2, 0, 1) : 0;
                double qx = ax + t * dx - p.X, qy = ay + t * dy - p.Y, d = qx * qx + qy * qy;
                if (d < best) { best = d; station = run + t * len; }
                run += len;
            }
            return station;
        }
        return Enumerable.Range(0, points.Count).OrderBy(i => Station(points[i])).ThenBy(i => points[i].X).ToList();
    }

    private static readonly Regex Token = new(@"\{(L|N|A)(?::(0+))?\}", RegexOptions.Compiled);

    // Tokens: {L} level ordinal, {N} sequence, {A} apartment; ":00" zero-pads, e.g. "{L}{N:00}" → 101.
    public static string Format(string format, int level, int sequence, string? apartment)
    {
        if (!Token.IsMatch(format) || !format.Contains("{N")) throw new ArgumentException("The number format must contain {N} (optionally {L}, {A}; ':00' pads), e.g. '{L}{N:00}'.");
        return Token.Replace(format, m =>
        {
            var pad = m.Groups[2].Success ? m.Groups[2].Value.Length : 0;
            return m.Groups[1].Value switch
            {
                "L" => Pad(level, pad),
                "N" => Pad(sequence, pad),
                _ => apartment ?? ""
            };
        });
    }

    private static string Pad(int v, int width) => v < 0 ? "-" + Math.Abs(v).ToString().PadLeft(width, '0') : v.ToString().PadLeft(width, '0');

    // Natural sort key for room numbers ("2" < "10", "1.2" < "1.10").
    public static string NaturalKey(string s) => Regex.Replace(s ?? "", @"\d+", m => m.Value.PadLeft(10, '0'));

    // "101, 102, 103, 105" → "101–103, 105": how explications list room numbers.
    public static string CompactList(IEnumerable<string> numbers)
    {
        var list = numbers.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy(NaturalKey, StringComparer.Ordinal).ToList();
        var parts = new List<string>();
        for (int i = 0; i < list.Count;)
        {
            int j = i;
            while (j + 1 < list.Count && Successor(list[j]) == list[j + 1]) j++;
            parts.Add(j - i >= 2 ? $"{list[i]}–{list[j]}" : string.Join(", ", list.Skip(i).Take(j - i + 1)));
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static string? Successor(string s)
    {
        var m = Regex.Match(s, @"^(.*?)(\d+)$");
        if (!m.Success) return null;
        var n = long.Parse(m.Groups[2].Value) + 1;
        return m.Groups[1].Value + n.ToString().PadLeft(m.Groups[2].Value.Length, '0');
    }

    // Rough text wrapping for drafting tables: how many lines `text` takes in a cell `widthMm` wide
    // at `textHeightMm` (average glyph ≈ 0.6 × height for the GOST type A/B fonts).
    public static int Lines(string text, double widthMm, double textHeightMm)
    {
        var perLine = Math.Max(1, (int)Math.Floor((widthMm - 2) / (0.6 * textHeightMm)));
        return Math.Max(1, (text ?? "").Split('\n').Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / (double)perLine))));
    }
}
