using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Batch sheet issue. Exporting a drawing set one sheet at a time is the classic end-of-stage
// time sink, and the part everyone gets wrong is the FILE NAMING — the recipient needs
// "AR-101 - Ground Floor Plan.pdf", not "Sheet1.pdf". This selects a sheet set by number prefix,
// name or revision, and names each file from a template of sheet fields.
public class BatchExportSheets : IRevitTool
{
    public string Name => "batch_export_sheets";

    public string Description =>
        "Export many sheets at once to PDF or DWG with a file-naming template. Select sheets by explicit ids, " +
        "a sheet-number prefix (e.g. 'AR-'), a name filter, or all placed sheets. " +
        "filename_template supports {sheet_number}, {sheet_name}, {revision}, {discipline}, {project} — " +
        "default \"{sheet_number} - {sheet_name}\". Set combined=true for ONE multi-page PDF. " +
        "Returns the files written. Use list_sheets/get_sheet_views first if you need to check the set.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["output_folder"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Folder to write into. Created if missing."
            }),
            ["format"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", @enum = new[] { "pdf", "dwg" }, description = "Export format. Default 'pdf'."
            }),
            ["sheet_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", description = "Explicit sheet element ids.", items = new { type = "integer" }
            }),
            ["number_prefix"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Only sheets whose number starts with this, e.g. 'AR-'."
            }),
            ["name_contains"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Only sheets whose name contains this text."
            }),
            ["filename_template"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Default \"{sheet_number} - {sheet_name}\"."
            }),
            ["combined"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean", description = "PDF only: one combined multi-page file instead of one per sheet. Default false."
            })
        },
        Required = ["output_folder"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var folder = input["output_folder"].GetString()
            ?? throw new InvalidOperationException("output_folder is required.");
        Directory.CreateDirectory(folder);

        var format = input.TryGetValue("format", out var f) ? (f.GetString() ?? "pdf").ToLowerInvariant() : "pdf";
        var template = input.TryGetValue("filename_template", out var ft) ? ft.GetString() : null;
        if (string.IsNullOrWhiteSpace(template)) template = "{sheet_number} - {sheet_name}";
        var combined = input.TryGetValue("combined", out var cb) && cb.ValueKind == JsonValueKind.True;

        var sheets = SelectSheets(doc, input);
        if (sheets.Count == 0)
            return Services.Json.Serialize(new { exported = 0, note = "No sheets matched the filter." });

        var written = new List<object>();
        var failures = new List<object>();

        if (format == "dwg")
        {
            var opts = new DWGExportOptions();
            foreach (var sh in sheets)
            {
                var fileName = Sanitize(Render(template!, doc, sh));
                try
                {
                    doc.Export(folder, fileName, new List<ElementId> { sh.Id }, opts);
                    written.Add(new { sheet = sh.SheetNumber, file = fileName + ".dwg" });
                }
                catch (Exception ex) { failures.Add(new { sheet = sh.SheetNumber, reason = Short(ex.Message) }); }
            }
        }
        else
        {
            var opts = new PDFExportOptions
            {
                Combine = combined,
                // Revit otherwise invents its own name from the view/sheet — we want the template.
                FileName = combined ? Sanitize(Render(template!, doc, sheets[0])) : null,
                HideCropBoundaries = true,
                HideScopeBoxes = true,
                HideReferencePlane = true,
                HideUnreferencedViewTags = true,
                MaskCoincidentLines = true
            };

            if (combined)
            {
                try
                {
                    doc.Export(folder, sheets.Select(s => s.Id).ToList(), opts);
                    written.Add(new { sheets = sheets.Count, file = opts.FileName + ".pdf" });
                }
                catch (Exception ex) { failures.Add(new { sheet = "(combined)", reason = Short(ex.Message) }); }
            }
            else
            {
                foreach (var sh in sheets)
                {
                    var fileName = Sanitize(Render(template!, doc, sh));
                    try
                    {
                        opts.FileName = fileName;
                        doc.Export(folder, new List<ElementId> { sh.Id }, opts);
                        written.Add(new { sheet = sh.SheetNumber, file = fileName + ".pdf" });
                    }
                    catch (Exception ex) { failures.Add(new { sheet = sh.SheetNumber, reason = Short(ex.Message) }); }
                }
            }
        }

        return Services.Json.Serialize(new
        {
            format,
            folder,
            sheets_selected = sheets.Count,
            exported = written.Count,
            failed = failures.Count,
            files = written.Take(200),
            failures = failures.Take(25)
        });
    }

    private static List<ViewSheet> SelectSheets(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.TryGetValue("sheet_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray()
                .Select(e => doc.GetElement(new ElementId(e.GetInt64())) as ViewSheet)
                .Where(s => s != null).ToList()!;

        var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsTemplate).ToList();

        if (input.TryGetValue("number_prefix", out var np) && np.ValueKind == JsonValueKind.String)
        {
            var pfx = np.GetString() ?? "";
            all = all.Where(s => s.SheetNumber.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (input.TryGetValue("name_contains", out var nc) && nc.ValueKind == JsonValueKind.String)
        {
            var needle = nc.GetString() ?? "";
            all = all.Where(s => s.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        return all.OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Render(string template, Document doc, ViewSheet sh)
    {
        string Val(string key) => key switch
        {
            "sheet_number" => sh.SheetNumber,
            "sheet_name" => sh.Name,
            "revision" => sh.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "",
            // No built-in enum for sheet discipline — it's a normal (often project) parameter.
            "discipline" => sh.LookupParameter("Discipline")?.AsValueString()
                            ?? sh.LookupParameter("Discipline")?.AsString() ?? "",
            "project" => doc.ProjectInformation?.Name ?? "",
            _ => sh.LookupParameter(key)?.AsString() ?? ""
        };

        var sb = new StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') { sb.Append(template[i]); continue; }
            var close = template.IndexOf('}', i + 1);
            if (close < 0) { sb.Append(template[i]); continue; }
            sb.Append(Val(template.Substring(i + 1, close - i - 1).Trim()));
            i = close;
        }
        return sb.ToString();
    }

    // Revit happily accepts a sheet name with a slash or colon; the file system does not.
    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim();
        return s.Length == 0 ? "sheet" : s.Length > 180 ? s.Substring(0, 180) : s;
    }

    private static string Short(string s) => s.Length <= 140 ? s : s.Substring(0, 140) + "…";
}
