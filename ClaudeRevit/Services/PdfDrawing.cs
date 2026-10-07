using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace ClaudeRevit.Services;

// Vector PDF drawing → model geometry. Idea from the vietnguyen0603 fork of mcp-servers-for-revit
// (pdf_extract: calibration by two points or by grid labels, then grids/walls); this is our own
// implementation on PdfPig. Pure logic — no Revit types — so extraction, calibration and wall
// detection are unit-tested on generated PDFs. All model coordinates are millimetres.
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(Pt b) => X * b.X + Y * b.Y;
    public double Cross(Pt b) => X * b.Y - Y * b.X;
    public Pt Unit => Length > 0 ? this * (1 / Length) : this;
}

public sealed record Seg(Pt A, Pt B, double Width)
{
    public double Length => (B - A).Length;
    public Pt Dir => (B - A).Unit;
    // Undirected angle in [0, π).
    public double Angle { get { var a = Math.Atan2(B.Y - A.Y, B.X - A.X); if (a < 0) a += Math.PI; return a >= Math.PI - 1e-12 ? 0 : a; } }
}

public sealed record PdfLabel(string Text, Pt Center, double Height);
public sealed record PdfVectorPage(int Page, int PageCount, double WidthPt, double HeightPt, int Rotation,
    List<Seg> Segments, List<PdfLabel> Labels, int Curves, bool Truncated);
public sealed record WallCandidate(Pt A, Pt B, double Thickness);
public sealed record GridCandidate(string Label, Pt A, Pt B);

// Model = Scale · R(Rotation) · pdf + T. A similarity: drawings are plotted to scale, not skewed.
public sealed record Similarity(double Scale, double Rotation, double Tx, double Ty)
{
    public const double MmPerPoint = 25.4 / 72;
    public Pt Apply(Pt p)
    {
        var c = Math.Cos(Rotation) * Scale; var s = Math.Sin(Rotation) * Scale;
        return new(c * p.X - s * p.Y + Tx, s * p.X + c * p.Y + Ty);
    }
    public Seg Apply(Seg g) => new(Apply(g.A), Apply(g.B), g.Width * Scale);

    public static Similarity FromPairs(Pt pdf1, Pt model1, Pt pdf2, Pt model2)
    {
        var v = pdf2 - pdf1; var w = model2 - model1;
        if (v.Length < 1e-6 || w.Length < 1e-6) throw new ArgumentException("Calibration points must be distinct.");
        var scale = w.Length / v.Length;
        var rot = Math.Atan2(w.Y, w.X) - Math.Atan2(v.Y, v.X);
        var c = Math.Cos(rot) * scale; var s = Math.Sin(rot) * scale;
        return new(scale, rot, model1.X - (c * pdf1.X - s * pdf1.Y), model1.Y - (s * pdf1.X + c * pdf1.Y));
    }

    // Plotted at 1:denominator: one PDF point is 25.4/72 paper mm, i.e. that × denominator model mm.
    public static Similarity FromScale(double denominator, Pt pdfOrigin, Pt modelOrigin, double rotationDeg = 0)
    {
        if (!(denominator > 0)) throw new ArgumentException("Scale denominator must be positive.");
        var scale = MmPerPoint * denominator; var rot = rotationDeg * Math.PI / 180;
        var c = Math.Cos(rot) * scale; var s = Math.Sin(rot) * scale;
        return new(scale, rot, modelOrigin.X - (c * pdfOrigin.X - s * pdfOrigin.Y), modelOrigin.Y - (s * pdfOrigin.X + c * pdfOrigin.Y));
    }

    // Nominal plot scale this calibration implies (1:N), for a sanity check against the title block.
    public double ImpliedDenominator => Scale / MmPerPoint;
}

public static class PdfDrawing
{
    public static PdfVectorPage Read(string path, int pageNumber, int maxSegments = 100_000)
    {
        using var pdf = PdfDocument.Open(path);
        if (pageNumber < 1 || pageNumber > pdf.NumberOfPages)
            throw new ArgumentException($"Page {pageNumber} is out of range; the PDF has {pdf.NumberOfPages} page(s).");
        var page = pdf.GetPage(pageNumber);
        var segments = new List<Seg>(); var seen = new HashSet<(long, long, long, long)>();
        int curves = 0; bool truncated = false;
        void Add(PdfPoint a, PdfPoint b, double width)
        {
            var pa = new Pt(a.X, a.Y); var q = new Pt(b.X, b.Y);
            if ((q - pa).Length < 1e-3) return;
            // The same edge is often painted twice (fill then stroke); keep one, direction-free.
            var k1 = (R(pa.X), R(pa.Y)); var k2 = (R(q.X), R(q.Y));
            var key = k1.CompareTo(k2) <= 0 ? (k1.Item1, k1.Item2, k2.Item1, k2.Item2) : (k2.Item1, k2.Item2, k1.Item1, k1.Item2);
            if (!seen.Add(key)) return;
            if (segments.Count >= maxSegments) { truncated = true; return; }
            segments.Add(new(pa, q, width));
        }
        foreach (var p in page.Paths)
        {
            if (p.IsClipping || !(p.IsStroked || p.IsFilled)) continue;
            foreach (var sub in p)
            {
                PdfPoint? start = null, current = null;
                foreach (var cmd in sub.Commands)
                {
                    switch (cmd)
                    {
                        case PdfSubpath.Move m: start = current = m.Location; break;
                        case PdfSubpath.Line l: Add(l.From, l.To, p.LineWidth); current = l.To; start ??= l.From; break;
                        case PdfSubpath.BezierCurve c: curves++; current = c.EndPoint; start ??= c.StartPoint; break;
                        case PdfSubpath.Close when start is { } s && current is { } cur: Add(cur, s, p.LineWidth); current = s; break;
                    }
                }
            }
        }
        var labels = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .Select(w => new PdfLabel(w.Text.Trim(), new Pt(w.BoundingBox.Centroid.X, w.BoundingBox.Centroid.Y), w.BoundingBox.Height)).ToList();
        return new(pageNumber, pdf.NumberOfPages, page.Width, page.Height, (int)page.Rotation.Value, segments, labels, curves, truncated);
    }

    private static long R(double v) => (long)Math.Round(v * 100);

    // Joins pieces of one straight line split by dashes or by the exporter: same direction, on the
    // same line within `lineTol`, separated by at most `maxGap`. Grid axes are dash-dot and arrive
    // as dozens of short segments; walls arrive split at every junction.
    public static List<Seg> MergeCollinear(IReadOnlyList<Seg> segs, double maxGap, double lineTol = 0.5, double angleTolDeg = 0.5)
    {
        var tol = angleTolDeg * Math.PI / 180;
        var groups = new List<(Pt Origin, Pt Dir, List<(double S, double E, double W)> Spans)>();
        // Index groups by (direction bucket, signed offset of the line from the origin) so lookup
        // stays linear on drawings with tens of thousands of segments.
        var index = new Dictionary<(int, long), List<int>>();
        foreach (var s in segs)
        {
            var d = s.Dir; if (d.X < -1e-12 || (Math.Abs(d.X) <= 1e-12 && d.Y < 0)) d = d * -1;
            var ab = (int)Math.Floor(Math.Atan2(d.Y, d.X) / tol); var off = (long)Math.Floor(d.Cross(s.A) / lineTol);
            var g = -1;
            for (int da = -1; da <= 1 && g < 0; da++)
                for (long dof = -1; dof <= 1 && g < 0; dof++)
                    if (index.TryGetValue((ab + da, off + dof), out var cands))
                        foreach (var c in cands)
                        {
                            var gr = groups[c];
                            if (Math.Abs(gr.Dir.Cross(d)) < Math.Sin(tol) && Math.Abs(gr.Dir.Cross(s.A - gr.Origin)) < lineTol &&
                                Math.Abs(gr.Dir.Cross(s.B - gr.Origin)) < lineTol) { g = c; break; }
                        }
            if (g < 0)
            {
                groups.Add((s.A, d, new())); g = groups.Count - 1;
                if (!index.TryGetValue((ab, off), out var l)) index[(ab, off)] = l = new();
                l.Add(g);
            }
            var (o, dir, spans) = groups[g];
            var a = dir.Dot(s.A - o); var b = dir.Dot(s.B - o);
            spans.Add((Math.Min(a, b), Math.Max(a, b), s.Width));
        }
        var result = new List<Seg>();
        foreach (var (o, dir, spans) in groups)
        {
            spans.Sort((x, y) => x.S.CompareTo(y.S));
            var (cs, ce, cw) = spans[0];
            foreach (var (s, e, w) in spans.Skip(1))
            {
                if (s <= ce + maxGap) { ce = Math.Max(ce, e); cw = Math.Max(cw, w); continue; }
                result.Add(new(o + dir * cs, o + dir * ce, cw)); (cs, ce, cw) = (s, e, w);
            }
            result.Add(new(o + dir * cs, o + dir * ce, cw));
        }
        return result;
    }

    // Walls drawn as two parallel face lines: pairs whose spacing is a plausible wall thickness and
    // whose projections overlap by at least minLength become a centreline + thickness. Greedy by
    // overlap so a long face pairs with its true opposite face, not with a short window jamb.
    public static List<WallCandidate> DetectWalls(IReadOnlyList<Seg> segs, double minThickness, double maxThickness, double minLength, double angleTolDeg = 1)
    {
        var tol = angleTolDeg * Math.PI / 180;
        var lines = segs.Where(s => s.Length >= minLength).ToList();
        var buckets = new Dictionary<int, List<int>>();
        int Bucket(double angle) => (int)Math.Floor(angle / tol);
        var nb = (int)Math.Ceiling(Math.PI / tol);
        for (int i = 0; i < lines.Count; i++)
        {
            var b = Bucket(lines[i].Angle);
            if (!buckets.TryGetValue(b, out var l)) buckets[b] = l = new();
            l.Add(i);
        }
        var candidates = new List<(WallCandidate Wall, double Overlap)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var s = lines[i]; var u = s.Dir; var b = Bucket(s.Angle);
            foreach (var nbIdx in new[] { b - 1, b, b + 1 }.Select(x => ((x % nb) + nb) % nb).Distinct())
            {
                if (!buckets.TryGetValue(nbIdx, out var list)) continue;
                foreach (var j in list)
                {
                    if (j <= i) continue;
                    var t = lines[j];
                    if (Math.Abs(u.Cross(t.Dir)) > Math.Sin(tol)) continue;
                    var dA = u.Cross(t.A - s.A); var dB = u.Cross(t.B - s.A);
                    if (Math.Sign(dA) != Math.Sign(dB)) continue;
                    var d = (Math.Abs(dA) + Math.Abs(dB)) / 2;
                    if (d < minThickness || d > maxThickness || Math.Abs(Math.Abs(dA) - Math.Abs(dB)) > 0.05 * d + 1) continue;
                    var ta = u.Dot(t.A - s.A); var tb = u.Dot(t.B - s.A);
                    var lo = Math.Max(0, Math.Min(ta, tb)); var hi = Math.Min(s.Length, Math.Max(ta, tb));
                    if (hi - lo < minLength) continue;
                    var normal = new Pt(-u.Y, u.X) * (Math.Sign(dA) * d / 2);
                    candidates.Add((new(s.A + u * lo + normal, s.A + u * hi + normal, d), hi - lo));
                }
            }
        }
        var accepted = new List<WallCandidate>();
        foreach (var (w, _) in candidates.OrderByDescending(c => c.Overlap))
        {
            var dir = (w.B - w.A).Unit; var len = (w.B - w.A).Length;
            bool Duplicate(WallCandidate a)
            {
                var ad = (a.B - a.A).Unit;
                if (Math.Abs(ad.Cross(dir)) > Math.Sin(tol)) return false;
                if (Math.Abs(ad.Cross(w.A - a.A)) > Math.Max(a.Thickness, w.Thickness) / 2) return false;
                var p = ad.Dot(w.A - a.A); var q = ad.Dot(w.B - a.A); var alen = (a.B - a.A).Length;
                var overlap = Math.Min(alen, Math.Max(p, q)) - Math.Max(0, Math.Min(p, q));
                return overlap > 0.5 * Math.Min(alen, len);
            }
            if (!accepted.Any(Duplicate)) accepted.Add(w);
        }
        return accepted;
    }

    private static readonly Regex GridLabel = new(@"^[A-ZА-Я0-9]{1,3}([/'.,][A-ZА-Я0-9]{1,2})?$", RegexOptions.Compiled);

    // Grid axes: long lines with a short alphanumeric label just past an end (the bubble). One line
    // per label — the longest — since a label can sit near several dimension or wall lines.
    public static List<GridCandidate> DetectGrids(IReadOnlyList<Seg> segs, IReadOnlyList<PdfLabel> labels, double minLength, double labelRadius)
    {
        var tags = labels.Where(l => GridLabel.IsMatch(l.Text.ToUpperInvariant())).ToList();
        var found = new Dictionary<string, (Seg Seg, double Length)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in segs.Where(s => s.Length >= minLength))
        {
            var u = s.Dir;
            PdfLabel? best = null; double bestDist = double.MaxValue;
            foreach (var l in tags)
            {
                foreach (var (end, outward) in new[] { (s.A, u * -1), (s.B, u) })
                {
                    var v = l.Center - end;
                    var along = v.Dot(outward); var across = Math.Abs(outward.Cross(v));
                    if (along < -labelRadius * 0.25 || along > labelRadius * 2.5 || across > labelRadius) continue;
                    var dist = v.Length;
                    if (dist < bestDist) { bestDist = dist; best = l; }
                }
            }
            if (best == null) continue;
            if (!found.TryGetValue(best.Text, out var prev) || s.Length > prev.Length) found[best.Text] = (s, s.Length);
        }
        return found.Select(kv => new GridCandidate(kv.Key, kv.Value.Seg.A, kv.Value.Seg.B)).OrderBy(g => g.Label).ToList();
    }

    public static Pt? Intersect(Pt a1, Pt a2, Pt b1, Pt b2)
    {
        var r = a2 - a1; var s = b2 - b1; var den = r.Cross(s);
        if (Math.Abs(den) < 1e-9 * r.Length * s.Length) return null;
        var t = (b1 - a1).Cross(s) / den;
        return a1 + r * t;
    }

    // Calibration by grid labels: grids named in both the drawing (PDF coordinates) and the model
    // (mm) give matching intersection points. The two farthest-apart intersections fix the
    // similarity; the rest measure how well it fits (rms, mm).
    public static (Similarity Transform, double RmsMm, int Points) FromGridMatches(
        IReadOnlyList<GridCandidate> pdfGrids, IReadOnlyDictionary<string, (Pt A, Pt B)> modelGrids)
    {
        var matched = pdfGrids.Where(g => modelGrids.ContainsKey(g.Label)).ToList();
        var pairs = new List<(Pt Pdf, Pt Model)>();
        for (int i = 0; i < matched.Count; i++)
            for (int j = i + 1; j < matched.Count; j++)
            {
                var p = Intersect(matched[i].A, matched[i].B, matched[j].A, matched[j].B);
                var m = modelGrids[matched[i].Label]; var n = modelGrids[matched[j].Label];
                var q = Intersect(m.A, m.B, n.A, n.B);
                if (p is { } pp && q is { } qq) pairs.Add((pp, qq));
            }
        if (pairs.Count < 2)
            throw new ArgumentException($"Grid calibration needs at least two crossing grid pairs named in both drawing and model; matched labels: {string.Join(", ", matched.Select(g => g.Label))}.");
        var (bi, bj, best) = (0, 1, -1.0);
        for (int i = 0; i < pairs.Count; i++)
            for (int j = i + 1; j < pairs.Count; j++)
            {
                var d = (pairs[i].Model - pairs[j].Model).Length;
                if (d > best) (bi, bj, best) = (i, j, d);
            }
        var t = Similarity.FromPairs(pairs[bi].Pdf, pairs[bi].Model, pairs[bj].Pdf, pairs[bj].Model);
        var rms = Math.Sqrt(pairs.Average(p => Math.Pow((t.Apply(p.Pdf) - p.Model).Length, 2)));
        return (t, rms, pairs.Count);
    }
}
