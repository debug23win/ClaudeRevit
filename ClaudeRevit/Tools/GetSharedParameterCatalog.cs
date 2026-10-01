using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public class GetSharedParameterCatalog : IRevitTool
{
    public string Name => "get_shared_parameter_catalog";
    public string Description => "Search shared parameter definitions by GUID/name/group. Includes the verified " +
        "BIMStarter/Weandrevit 2020 RU/ENG reference and the configured Revit FOP (including ADSK if configured). " +
        "file_path reads a specific local FOP instead. Reports original data-type tokens and source hashes; " +
        "definitions do not imply project bindings. Does not modify the file or Revit's FOP setting.";
    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "GUID or substring in name/group/description; RU or ENG." }),
            ["file_path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional full path to a Revit shared parameter TXT file." }),
            ["offset"] = JsonSerializer.SerializeToElement(new { type = "integer", minimum = 0 }),
            ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer", minimum = 1, maximum = 300, description = "Default 60." })
        }, Required = []
    };
    public bool RequiresTransaction => false;
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var query = input.TryGetValue("query", out var q) ? q.GetString() ?? "" : "";
        var explicitPath = input.TryGetValue("file_path", out var p) ? p.GetString() : null;
        var definitions = new List<SharedParameterDefinition>();
        var sources = new List<object>();
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var catalog = SharedParameterCatalog.Read(explicitPath);
            definitions.AddRange(catalog.Definitions);
            sources.Add(new { catalog.Source, catalog.Sha256, count = catalog.Definitions.Count });
            warnings.AddRange(catalog.Warnings);
        }
        else
        {
            definitions.AddRange(StandardKnowledge.Definitions);
            sources.Add(new { source = "Bundled BIMStarter 2020 RU/ENG GUID reference", count = definitions.Count });
            try
            {
                var catalog = StandardKnowledge.Configured(app.Application.SharedParametersFilename);
                if (catalog != null)
                {
                    definitions.AddRange(catalog.Definitions);
                    sources.Add(new { catalog.Source, catalog.Sha256, count = catalog.Definitions.Count });
                    warnings.AddRange(catalog.Warnings);
                }
                else warnings.Add("No FOP is configured in Revit. The bundled catalog is not the full official ADSK FOP2021.");
            }
            catch (Exception ex) { warnings.Add("Configured FOP could not be read: " + ex.Message); }
        }
        var matches = definitions.Where(d => string.IsNullOrEmpty(query) ||
            d.Guid.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            d.Group.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            d.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        int offset = input.TryGetValue("offset", out var o) ? Math.Max(0, o.GetInt32()) : 0;
        int limit = input.TryGetValue("limit", out var l) ? Math.Clamp(l.GetInt32(), 1, 300) : 60;
        return Services.Json.Serialize(new
        {
            sources, warnings, total = matches.Count, offset,
            definitions = matches.Skip(offset).Take(limit).ToList(),
            next_offset = offset + limit < matches.Count ? (int?)(offset + limit) : null,
            note = "Resolve by GUID. Verify live spec/binding with get_project_standards before changing values. RU and ENG are distinct source snapshots."
        });
    }
}
