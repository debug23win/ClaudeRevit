using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Setting-out coordinate export — piles, columns, foundations, survey points. The subtlety that
// makes this useful (and that hand-rolled scripts usually get wrong) is SHARED coordinates: the
// surveyor needs values in the project's shared/site system, not Revit's internal origin. This
// applies the shared-coordinate transform, and can emit engineering N/E ordering.
public class ExportElementCoordinates : IRevitTool
{
    public string Name => "export_element_coordinates";

    public string Description =>
        "Export setting-out coordinates for elements (piles, columns, foundations…): id, mark/name, type, level " +
        "and X/Y/Z in millimetres. Uses SHARED (site) coordinates by default — what a surveyor needs — or " +
        "internal project coordinates. Optionally writes a CSV file and/or orders columns as Northing/Easting. " +
        "Sorted by mark so the list matches the drawing.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["category"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Category to export, e.g. 'Structural Foundations' (piles), 'Structural Columns'."
            }),
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", description = "Explicit elements instead of a category.", items = new { type = "integer" }
            }),
            ["on_level"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Restrict to this level." }),
            ["coordinate_system"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "shared", "internal" },
                description = "'shared' (default) = site/survey coordinates. 'internal' = Revit project origin."
            }),
            ["northing_easting"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "Report as Northing (Y) / Easting (X) in engineering order instead of X/Y. Default false."
            }),
            ["mark_parameter"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Parameter used as the label. Default 'Mark'."
            }),
            ["csv_path"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Optional full path to write a CSV file (UTF-8 with BOM, Excel-friendly)."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var elements = Collect(doc, input);
        if (elements.Count == 0)
            return Services.Json.Serialize(new { count = 0, note = "No elements matched." });

        var shared = !input.TryGetValue("coordinate_system", out var cs) ||
                     !string.Equals(cs.GetString(), "internal", StringComparison.OrdinalIgnoreCase);
        var ne = input.TryGetValue("northing_easting", out var nev) && nev.ValueKind == JsonValueKind.True;
        var markParam = input.TryGetValue("mark_parameter", out var mp) ? mp.GetString() : null;
        if (string.IsNullOrWhiteSpace(markParam)) markParam = "Mark";

        // Shared coordinates = internal point transformed by the active survey point's transform.
        Transform? toShared = null;
        if (shared)
        {
            try
            {
                var pbp = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ProjectBasePoint).FirstElement() as BasePoint;
                toShared = doc.ActiveProjectLocation?.GetTotalTransform().Inverse;
                _ = pbp; // presence check only; the transform above carries the survey offset
            }
            catch { toShared = null; }
        }

        var rows = new List<Row>();
        foreach (var el in elements)
        {
            var p = PointOf(el);
            if (p == null) continue;
            var q = toShared != null ? toShared.OfPoint(p) : p;

            rows.Add(new Row
            {
                Id = el.Id.Value,
                Mark = el.LookupParameter(markParam)?.AsString() ?? el.Name ?? "",
                Type = doc.GetElement(el.GetTypeId())?.Name ?? "",
                Level = el.LevelId != ElementId.InvalidElementId ? doc.GetElement(el.LevelId)?.Name ?? "" : "",
                X = Math.Round(Units.FeetToMm(q.X), 1),
                Y = Math.Round(Units.FeetToMm(q.Y), 1),
                Z = Math.Round(Units.FeetToMm(q.Z), 1)
            });
        }

        // Natural sort on the mark so "P2" precedes "P10" the way the drawing reads.
        rows = rows.OrderBy(r => r.Mark, new NaturalComparer()).ToList();

        string? csvWritten = null;
        if (input.TryGetValue("csv_path", out var cp) && cp.ValueKind == JsonValueKind.String)
        {
            var path = cp.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(ne ? "Id;Mark;Type;Level;Northing_mm;Easting_mm;Elevation_mm"
                                     : "Id;Mark;Type;Level;X_mm;Y_mm;Z_mm");
                    foreach (var r in rows)
                        sb.AppendLine(ne
                            ? $"{r.Id};{Csv(r.Mark)};{Csv(r.Type)};{Csv(r.Level)};{F(r.Y)};{F(r.X)};{F(r.Z)}"
                            : $"{r.Id};{Csv(r.Mark)};{Csv(r.Type)};{Csv(r.Level)};{F(r.X)};{F(r.Y)};{F(r.Z)}");
                    Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                    File.WriteAllText(path!, sb.ToString(), new UTF8Encoding(true));
                    csvWritten = path;
                }
                catch (Exception ex) { csvWritten = "ERROR: " + ex.Message; }
            }
        }

        return Services.Json.Serialize(new
        {
            count = rows.Count,
            coordinate_system = shared && toShared != null ? "shared" : "internal",
            shared_unavailable = shared && toShared == null,
            csv = csvWritten,
            points = rows.Take(300).Select(r => ne
                ? (object)new { id = r.Id, mark = r.Mark, type = r.Type, level = r.Level, northing_mm = r.Y, easting_mm = r.X, elevation_mm = r.Z }
                : new { id = r.Id, mark = r.Mark, type = r.Type, level = r.Level, x_mm = r.X, y_mm = r.Y, z_mm = r.Z })
        });
    }

    private sealed class Row
    {
        public long Id; public string Mark = ""; public string Type = ""; public string Level = "";
        public double X, Y, Z;
    }

    private static string F(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Csv(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static XYZ? PointOf(Element el)
    {
        try
        {
            switch (el.Location)
            {
                case LocationPoint lp: return lp.Point;
                case LocationCurve lc: return lc.Curve.Evaluate(0.5, true);
            }
            var bb = el.get_BoundingBox(null);
            return bb != null ? (bb.Min + bb.Max) * 0.5 : null;
        }
        catch { return null; }
    }

    private static List<Element> Collect(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.TryGetValue("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray().Select(e => doc.GetElement(new ElementId(e.GetInt64())))
                      .Where(e => e != null).ToList()!;

        if (!input.TryGetValue("category", out var cat) || cat.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Provide either element_ids or category.");

        var bic = CategoryResolve.Parse(cat.GetString());
        var q = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();

        if (input.TryGetValue("on_level", out var lv) && lv.ValueKind == JsonValueKind.String)
        {
            var name = lv.GetString();
            var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Level '{name}' not found.");
            q = q.Where(e => e.LevelId == lvl.Id).ToList();
        }
        return q;
    }

    // "P2" before "P10": compare digit runs numerically, everything else as text.
    private sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = long.TryParse(a.Substring(si, i - si), out var x) ? x : 0;
                    var nb = long.TryParse(b.Substring(sj, j - sj), out var y) ? y : 0;
                    if (na != nb) return na.CompareTo(nb);
                }
                else
                {
                    var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
