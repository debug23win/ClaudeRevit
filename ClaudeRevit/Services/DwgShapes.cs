using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeRevit.Services;

// Shapes in a CAD underlay (mm, plan): column outlines and grid naming. Pure logic, unit-tested;
// the Revit side reads the DWG geometry by layer and builds elements from these results.
public sealed record RectShape(Pt Center, double Width, double Depth, double AngleRad);

public static class DwgShapes
{
    // Closed outlines of four segments meeting at right angles (columns drawn as rectangles, from
    // closed polylines or loose lines). Endpoints are matched within `tol`.
    public static List<RectShape> Rectangles(IReadOnlyList<Seg> segs, double tol = 1, double minSide = 50, double maxSide = 3000)
    {
        var key = new Dictionary<(long, long), int>();
        var nodes = new List<Pt>();
        int Node(Pt p)
        {
            var k = ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol));
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (key.TryGetValue((k.Item1 + dx, k.Item2 + dy), out var n) && (nodes[n] - p).Length <= tol) return n;
            nodes.Add(p); key[k] = nodes.Count - 1; return nodes.Count - 1;
        }
        var edges = segs.Where(s => s.Length >= minSide && s.Length <= maxSide).Select(s => (A: Node(s.A), B: Node(s.B))).Where(e => e.A != e.B).Distinct().ToList();
        var adj = new Dictionary<int, List<int>>();
        foreach (var (a, b) in edges) { (adj.TryGetValue(a, out var la) ? la : adj[a] = new()).Add(b); (adj.TryGetValue(b, out var lb) ? lb : adj[b] = new()).Add(a); }
        var found = new HashSet<string>(); var result = new List<RectShape>();
        foreach (var start in adj.Keys)
            foreach (var n1 in adj[start])
                foreach (var n2 in adj[n1].Where(x => x != start))
                    foreach (var n3 in adj[n2].Where(x => x != n1 && x != start))
                    {
                        if (!adj[n3].Contains(start)) continue;
                        var loop = new[] { start, n1, n2, n3 };
                        var id = string.Join(",", loop.OrderBy(x => x));
                        if (!found.Add(id)) continue;
                        var p = loop.Select(i => nodes[i]).ToArray();
                        var e1 = p[1] - p[0]; var e2 = p[2] - p[1]; var e3 = p[3] - p[2]; var e4 = p[0] - p[3];
                        bool Right(Pt u, Pt v) => Math.Abs(u.Unit.Dot(v.Unit)) < 0.02;
                        if (!Right(e1, e2) || !Right(e2, e3) || !Right(e3, e4)) continue;
                        if (Math.Abs(e1.Length - e3.Length) > tol * 2 || Math.Abs(e2.Length - e4.Length) > tol * 2) continue;
                        var center = new Pt(p.Average(q => q.X), p.Average(q => q.Y));
                        // Width along the side closest to X, so an unrotated column reads b × h as drawn.
                        var angle = Math.Atan2(e1.Y, e1.X);
                        double w = e1.Length, d = e2.Length;
                        while (angle <= -Math.PI / 4) { angle += Math.PI / 2; (w, d) = (d, w); }
                        while (angle > Math.PI / 4) { angle -= Math.PI / 2; (w, d) = (d, w); }
                        result.Add(new(center, Math.Round(w, 1), Math.Round(d, 1), Math.Abs(angle) < 1e-6 ? 0 : angle));
                    }
        return result;
    }

    // Russian grid naming (ГОСТ 21.101 / СПДС practice): axes across the building's length —
    // drawn vertical in plan — are numbered 1, 2, 3… left to right; the others take Cyrillic
    // capitals bottom to top, skipping Ё, З, Й, О, Х, Ц, Ч, Щ, Ъ, Ы, Ь (and doubling after Я).
    private static readonly string Letters = new("АБВГДЕЖИКЛМНПРСТУФШЭЮЯ".ToCharArray());

    public static string Letter(int index)
    {
        var n = Letters.Length;
        return index < n ? Letters[index].ToString() : Letters[index / n - 1].ToString() + Letters[index % n];
    }

    // Splits grid lines into "numbered" (closer to vertical) and "lettered", ordered, and names them.
    public static List<(Seg Line, string Name)> NameGrids(IReadOnlyList<Seg> grids)
    {
        bool Vertical(Seg s) => Math.Abs(s.Dir.Y) >= Math.Abs(s.Dir.X);
        double MidX(Seg s) => (s.A.X + s.B.X) / 2; double MidY(Seg s) => (s.A.Y + s.B.Y) / 2;
        var numbered = grids.Where(Vertical).OrderBy(MidX).Select((s, i) => (s, (i + 1).ToString()));
        var lettered = grids.Where(s => !Vertical(s)).OrderBy(MidY).Select((s, i) => (s, Letter(i)));
        return numbered.Concat(lettered).ToList();
    }
}
