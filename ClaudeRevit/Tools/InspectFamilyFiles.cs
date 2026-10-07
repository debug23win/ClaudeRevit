using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
namespace ClaudeRevit.Tools;

public sealed class InspectFamilyFiles : IRevitTool
{
    public string Name => "inspect_family_files";
    public string Description => "Inspect 1..10 local RFA reference files in temporary background documents, closing each without saving. Returns real parameter GUIDs/formulas/types, nested dependency tree and optional independent flex scenarios. Does not load files into the project or upgrade the originals. Rejects files already open by the user. Compare actual geometry after size/variant changes; a catalog card alone cannot prove constraints.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["files"] = NativeToolUtil.Any("[{path:absolute RFA path,scenarios?:[{name,type_name?,values:{actual parameter name or GUID:value}}],require_geometry_change?:bool}]. Length in mm, area m2, volume m3, angle degrees. Inspect first to discover names before flexing. No scenarios means analysis only."),
        ["max_depth"] = NativeToolUtil.Field("integer", "Default 4, 0..6.")
    }, "files");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input, UIApplication app)
    {
        var files = ToolInput.RequiredArray(input, "files").EnumerateArray().ToArray(); if (files.Length is <1 or >10) throw new ToolInputException("Supply 1..10 RFA files.");
        var rows = new List<object>();
        for (int i = 0; i < files.Length; i++)
        {
            ToolContext.ReportProgress(i, files.Length, "inspecting reference families");
            var item = files[i]; var path = item.GetProperty("path").GetString() ?? "";
            var args = new Dictionary<string,JsonElement> { ["file_path"] = JsonSerializer.SerializeToElement(path), ["max_depth"] = input.TryGetValue("max_depth", out var d) ? d : JsonSerializer.SerializeToElement(4) };
            try
            {
                if(new FileInfo(path).Length>100_000_000)throw new ToolInputException("RFA exceeds the 100 MB reference inspection limit.");
                var structure = JsonSerializer.Deserialize<JsonElement>(new AnalyzeFamilyStructure().Execute(args, app));
                JsonElement? flex = null;
                if (item.TryGetProperty("scenarios", out var scenarios))
                {
                    args["scenarios"] = scenarios;
                    if (item.TryGetProperty("require_geometry_change", out var change)) args["require_geometry_change"] = change;
                    flex = JsonSerializer.Deserialize<JsonElement>(new FlexFamily().Execute(args, app));
                }
                rows.Add(new { path, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), structure, flex, original_saved = false });
            }
            catch (OperationCanceledException) { throw; }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex) { rows.Add(new { path, error = ex.Message }); }
        }
        return Services.Json.Serialize(new { files = rows, scope = "Independent native inspection; passing supplied scenarios only certifies those dimensions/variants." });
    }
}
