using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RPoint = Autodesk.Revit.DB.XYZ;
using Point = System.Windows.Point;
using FormattedText = System.Windows.Media.FormattedText;

namespace ClaudeRevit.Tools;

public sealed class CreateRebarScheduleImages : IRevitTool
{
    public string Name => "create_rebar_schedule_images";
    public string Description => "Render native rebar centerline sketches to PNG, import real Revit ImageTypes and optionally assign an existing IMAGE shared parameter by GUID for schedules. Labels show curve lengths and total length in mm. Default preview commits/rolls back imports and deletes temporary new PNGs. This is a projection sketch, not a certified fabrication drawing or automatic GOST bend chart; nonplanar bars are explicitly reported.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "1..200 native Rebar IDs."),
        ["output_directory"] = NativeToolUtil.Field("string", "Existing absolute directory. New unique PNGs only, no overwrites."),
        ["parameter_guid"] = NativeToolUtil.Field("string", "Optional existing writable IMAGE instance parameter GUID to assign. No parameter is added."),
        ["plane_normal"] = NativeToolUtil.Array("number", "Optional projection normal [x,y,z]. Otherwise derive a plane from centerlines."),
        ["width"] = NativeToolUtil.Field("integer", "PNG width 400..2000, default 1000; height is half width."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true. False retains new PNGs and ImageTypes.")
    }, "element_ids", "output_directory");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app); var preview = NativeToolUtil.Preview(input);
        var directory = ToolInput.RequiredString(input, "output_directory");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new ToolInputException("Supply an existing absolute output directory.");
        directory = Path.GetFullPath(directory);
        var width = Math.Clamp(ToolInput.OptionalInt(input, "width") ?? 1000, 400, 2000); var height = width / 2;
        var parameterGuid = NativeToolUtil.Text(input, "parameter_guid");
        var files = new List<string>(); var rows = new List<object>(); var applied = false;
        try
        {
            var data = new List<(Rebar Bar, string Path, bool NonPlanar, double Length)>();
            foreach (var id in NativeToolUtil.Ids(input["element_ids"], 200))
            {
                ToolContext.ThrowIfCancelled();
                var bar = NativeToolUtil.Element(doc, id.Value) as Rebar ?? throw new ToolInputException("Images require native Rebar, not IFC families.");
                var position = Enumerable.Range(0, bar.NumberOfBarPositions).First(bar.DoesBarExistAtPosition);
                var curves = bar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, position);
                var points = curves.SelectMany(c => c.Tessellate()).ToArray();
                if (points.Length < 2) throw new ToolInputException("Rebar has no drawable centerline.");
                var origin = points[0];
                var x = points.Skip(1).Select(p => p - origin).First(v => v.GetLength() > 1e-8).Normalize();
                RPoint? normal = input.TryGetValue("plane_normal", out var n) ? NativeToolUtil.Point(n, false) : null;
                normal ??= points.Skip(1).Select(p => x.CrossProduct(p - origin)).FirstOrDefault(v => v.GetLength() > 1e-8);
                normal ??= x.CrossProduct(Math.Abs(x.Z) < .9 ? RPoint.BasisZ : RPoint.BasisY);
                if (normal.GetLength() < 1e-8) throw new ToolInputException("Projection normal must be nonzero.");
                normal = normal.Normalize(); x = x - normal * x.DotProduct(normal);
                if (x.GetLength() < 1e-8) x = normal.CrossProduct(Math.Abs(normal.Z) < .9 ? RPoint.BasisZ : RPoint.BasisY);
                x = x.Normalize(); var y = normal.CrossProduct(x).Normalize();
                var nonPlanar = points.Any(p => Math.Abs((p - origin).DotProduct(normal)) > 1e-5);
                var path = Path.Combine(directory, $"rebar-{id.Value}-{Guid.NewGuid():N}.png");
                Render(path, curves, origin, x, y, width, height, id.Value, nonPlanar);
                files.Add(path); data.Add((bar, path, nonPlanar, curves.Sum(c => c.Length) * Units.MmPerFoot));
            }
            var (_, warnings) = NativeToolUtil.Commit(doc, "Claude: rebar schedule images", preview, () =>
            {
                foreach (var item in data)
                {
                    ToolContext.ThrowIfCancelled();
                    using var options = new ImageTypeOptions(item.Path, false, ImageTypeSource.Import);
                    var image = ImageType.Create(doc, options);
                    if (parameterGuid.Length > 0)
                    {
                        var p = NativeToolUtil.Parameter(item.Bar, "", parameterGuid) ?? throw new ToolInputException("Image shared parameter is missing on rebar.");
                        if (p.IsReadOnly || p.Definition.GetDataType() != SpecTypeId.Reference.Image) throw new ToolInputException("Parameter must be a writable IMAGE instance parameter.");
                        if (!p.Set(image.Id)) throw new ToolInputException("Revit refused the image parameter value.");
                    }
                    rows.Add(new { rebar_id = item.Bar.Id.Value, image_type_id = preview ? (long?)null : image.Id.Value, file = preview ? null : item.Path,
                        nonplanar_projection = item.NonPlanar, centerline_length_mm = item.Length });
                }
                return true;
            });
            applied = !preview;
            return Services.Json.Serialize(new { preview, images = rows, warnings, note = "Lengths are native curve lengths; image geometry is projected and is not a manufacturing approval." });
        }
        finally { if (!applied) foreach (var file in files) try { File.Delete(file); } catch { } }
    }
    private static void Render(string path, IList<Curve> curves, RPoint origin, RPoint x, RPoint y, int width, int height, long id, bool nonPlanar)
    {
        Point Project(RPoint p) => new((p - origin).DotProduct(x), (p - origin).DotProduct(y));
        var raw = curves.Select(c => c.Tessellate().Select(Project).ToArray()).ToArray();
        var all = raw.SelectMany(a => a).ToArray();
        var minX = all.Min(p => p.X); var maxX = all.Max(p => p.X); var minY = all.Min(p => p.Y); var maxY = all.Max(p => p.Y);
        var scale = Math.Min((width - 120) / Math.Max(maxX - minX, .01), (height - 140) / Math.Max(maxY - minY, .01));
        Point Screen(Point p) => new(60 + (p.X - minX) * scale, height - 70 - (p.Y - minY) * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            void Label(string text, Point at) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Black, 1), at);
            Label($"Rebar {id} | L = {curves.Sum(c => c.Length) * Units.MmPerFoot:0.##} mm" + (nonPlanar ? " | NONPLANAR PROJECTION" : ""), new Point(20, 15));
            var pen = new Pen(Brushes.Black, 3);
            for (var i = 0; i < raw.Length; i++)
            {
                for (var j = 1; j < raw[i].Length; j++) dc.DrawLine(pen, Screen(raw[i][j - 1]), Screen(raw[i][j]));
                var mid = Screen(Project(curves[i].Evaluate(.5, true)));
                Label((curves[i].Length * Units.MmPerFoot).ToString("0.##", CultureInfo.InvariantCulture), new Point(mid.X + 7, mid.Y + 7));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { encoder.Save(file); }
        catch { file.Dispose(); File.Delete(path); throw; }
    }
}
