using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Tags that sit on top of each other in a view. Idea from vietnguyen0603's fork of
// mcp-servers-for-revit (find_tag_overlaps / nearby-first shifting, MIT); native implementation.
internal static class TagLayout
{
    public sealed record Box(ElementId Id, double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Overlaps(Box o, double gap) =>
            MinX < o.MaxX + gap && o.MinX < MaxX + gap && MinY < o.MaxY + gap && o.MinY < MaxY + gap;
        public Box Shift(double dx, double dy) => this with { MinX = MinX + dx, MaxX = MaxX + dx, MinY = MinY + dy, MaxY = MaxY + dy };
    }

    public static List<Box> Boxes(Document doc, View view)
    {
        var boxes = new List<Box>();
        foreach (var e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                     .Where(e => e is IndependentTag or SpatialElementTag))
        {
            var bb = e.get_BoundingBox(view);
            if (bb == null) continue;
            boxes.Add(new Box(e.Id, bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y));
        }
        return boxes;
    }

    public static List<(Box A, Box B)> Pairs(List<Box> boxes, double gap)
    {
        var pairs = new List<(Box, Box)>();
        var sorted = boxes.OrderBy(b => b.MinX).ToList();
        for (var i = 0; i < sorted.Count; i++)
            for (var j = i + 1; j < sorted.Count && sorted[j].MinX < sorted[i].MaxX + gap; j++)
                if (sorted[i].Overlaps(sorted[j], gap)) pairs.Add((sorted[i], sorted[j]));
        return pairs;
    }

    // Nearest free spot first: rings of growing radius in 8 directions, so a tag moves as little
    // as possible from the element it labels. Tags move rigidly, so a shifted box is computed
    // instead of regenerating the view after every trial.
    public static (double Dx, double Dy)? FreeSpot(Box box, IReadOnlyList<Box> others, double gap, double stepFt, int rings)
    {
        for (var r = 1; r <= rings; r++)
            foreach (var (ux, uy) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (-1, 1), (1, -1), (-1, -1) })
            {
                var dx = ux * r * stepFt; var dy = uy * r * stepFt;
                var moved = box.Shift(dx, dy);
                if (!others.Any(o => o.Id != box.Id && moved.Overlaps(o, gap))) return (dx, dy);
            }
        return null;
    }

    public static View View(Document doc, UIApplication app, IReadOnlyDictionary<string, JsonElement> input) =>
        input.TryGetValue("view_id", out var v) && v.ValueKind == JsonValueKind.Number
            ? NativeToolUtil.Element(doc, v.GetInt64()) as View ?? throw new ToolInputException("view_id is not a view.")
            : ToolContext.UiDocument(app)?.ActiveView ?? throw new ToolInputException("No active view.");
}

public sealed class FindTagOverlaps : IRevitTool
{
    public string Name => "find_tag_overlaps";
    public string Description => "List tags (element tags and room tags) whose extents overlap in a view. Read-only. Then resolve_tag_overlaps moves them apart.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["view_id"] = NativeToolUtil.Field("integer", "View to check (default: active view)."),
        ["gap_mm"] = NativeToolUtil.Field("number", "Treat tags closer than this paper distance as overlapping (default 0).")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var view = TagLayout.View(doc, app, input);
        var gap = (ToolInput.OptionalDouble(input, "gap_mm") ?? 0) / Units.MmPerFoot * Math.Max(1, view.Scale);
        var boxes = TagLayout.Boxes(doc, view);
        var pairs = TagLayout.Pairs(boxes, gap);
        return Services.Json.Serialize(new
        {
            view = view.Name, tags = boxes.Count, overlapping_pairs = pairs.Count,
            pairs = pairs.Take(300).Select(p => new[] { p.A.Id.Value, p.B.Id.Value }),
            truncated = pairs.Count > 300
        });
    }
}

public sealed class ResolveTagOverlaps : IRevitTool
{
    public string Name => "resolve_tag_overlaps";
    public string Description =>
        "Move overlapping tags in a view to the nearest free position (smallest move first; tags without a leader get " +
        "one so they still point at their element). preview defaults true; preview=false applies. Reports tags that " +
        "found no free spot within the search radius.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["view_id"] = NativeToolUtil.Field("integer", "View (default: active view)."),
        ["gap_mm"] = NativeToolUtil.Field("number", "Paper clearance to keep between tags (default 1)."),
        ["step_mm"] = NativeToolUtil.Field("number", "Paper distance of each search step (default 3)."),
        ["max_rings"] = NativeToolUtil.Field("integer", "Search radius in steps (default 8)."),
        ["add_leaders"] = NativeToolUtil.Field("boolean", "Give moved element tags a leader (default true)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var view = TagLayout.View(doc, app, input);
        var preview = NativeToolUtil.Preview(input);
        var scale = Math.Max(1, view.Scale);
        double Paper(string key, double fallback) => (ToolInput.OptionalDouble(input, key) ?? fallback) / Units.MmPerFoot * scale;
        var gap = Paper("gap_mm", 1); var step = Paper("step_mm", 3);
        var rings = Math.Clamp(ToolInput.OptionalInt(input, "max_rings") ?? 8, 1, 40);
        var leaders = !input.ContainsKey("add_leaders") || ToolInput.Flag(input, "add_leaders");
        var boxes = TagLayout.Boxes(doc, view);
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: resolve tag overlaps", preview, () =>
        {
            var moved = new List<object>(); var stuck = new List<long>();
            var current = boxes.ToList();
            // Larger conflicts first: the tag overlapping the most others moves first.
            foreach (var box in boxes.OrderByDescending(b => boxes.Count(o => o.Id != b.Id && b.Overlaps(o, gap))))
            {
                ToolContext.ThrowIfCancelled();
                var mine = current.First(b => b.Id == box.Id);
                if (!current.Any(o => o.Id != mine.Id && mine.Overlaps(o, gap))) continue;
                if (TagLayout.FreeSpot(mine, current, gap, step, rings) is not { } d) { stuck.Add(mine.Id.Value); continue; }
                var e = doc.GetElement(mine.Id);
                var delta = new XYZ(d.Dx, d.Dy, 0);
                if (e is IndependentTag t)
                {
                    if (leaders && !t.HasLeader) t.HasLeader = true;
                    t.TagHeadPosition = t.TagHeadPosition + delta;
                }
                else if (e is SpatialElementTag s)
                {
                    if (leaders && !s.HasLeader) s.HasLeader = true;
                    s.TagHeadPosition = s.TagHeadPosition + delta;
                }
                else continue;
                current[current.FindIndex(b => b.Id == mine.Id)] = mine.Shift(d.Dx, d.Dy);
                moved.Add(new { id = mine.Id.Value, moved_mm = Math.Round(Math.Sqrt(d.Dx * d.Dx + d.Dy * d.Dy) * Units.MmPerFoot / scale, 1) });
            }
            return new { moved, stuck };
        });
        return Services.Json.Serialize(new
        {
            preview, view = view.Name, moved_count = result.moved.Count, result.moved,
            no_free_spot = result.stuck, revit_warnings = warnings
        });
    }
}
