using System.Text.Json;
using System.IO;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class AddSharedFamilyParameter : IRevitTool
{
    public string Name => "add_shared_family_parameter";
    public string Description => "Add an actual shared family parameter from a Revit FOP by its GUID, preserving its definition/spec. file_path defaults to configured FOP. Temporarily changes SharedParametersFilename and always restores it; does not edit the FOP. Rejects same-name/different-GUID collisions.";
    public bool RequiresTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["parameter_guid"] = NativeToolUtil.Field("string", "GUID present in source FOP."),
        ["file_path"] = NativeToolUtil.Field("string", "Optional actual Revit shared-parameter TXT."),
        ["is_instance"] = NativeToolUtil.Field("boolean", "Default false (type)."),
        ["group"] = NativeToolUtil.Field("string", "dimensions/data/identity/materials/construction/text; default data.")
    }, "parameter_guid");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app); var fm = FamilyEditorUtil.Manager(doc);
        if (!Guid.TryParse(ToolInput.RequiredString(input, "parameter_guid"), out var guid) || guid == Guid.Empty) throw new ToolInputException("Invalid GUID.");
        var path = NativeToolUtil.Text(input, "file_path", app.Application.SharedParametersFilename);
        if (!File.Exists(path)) throw new ToolInputException("Source FOP not found. A bundled reference catalog cannot replace the actual external definition file.");
        var original = app.Application.SharedParametersFilename;
        try
        {
            app.Application.SharedParametersFilename = path;
            var file = app.Application.OpenSharedParameterFile() ?? throw new ToolInputException("File is not a valid Revit FOP.");
            var matches = file.Groups.Cast<DefinitionGroup>().SelectMany(g => g.Definitions.Cast<Definition>()).OfType<ExternalDefinition>().Where(d => d.GUID == guid).ToList();
            if (matches.Count != 1) throw new ToolInputException("GUID is missing/duplicated in the FOP.");
            var definition = matches[0];
            var sameGuid = fm.Parameters.Cast<FamilyParameter>().SingleOrDefault(p => p.IsShared && p.GUID == guid);
            if (sameGuid != null)
            {
                if (sameGuid.IsInstance != ToolInput.Flag(input, "is_instance") || sameGuid.Definition.GetDataType() != definition.GetDataType())
                    throw new ToolInputException("Existing shared parameter has different instance/type or data type; inspect before changing.");
                return Services.Json.Serialize(new { created = false, name = sameGuid.Definition.Name, guid });
            }
            if (fm.Parameters.Cast<FamilyParameter>().Any(p => p.Definition.Name == definition.Name)) throw new ToolInputException("Same-name parameter with another identity already exists. Do not replace it automatically.");
            var added = fm.AddParameter(definition, FamilyEditorUtil.GroupFor(NativeToolUtil.Text(input, "group", "data")), ToolInput.Flag(input, "is_instance"));
            return Services.Json.Serialize(new { created = true, name = added.Definition.Name, guid = added.GUID, instance = added.IsInstance, spec = added.Definition.GetDataType().TypeId });
        }
        finally { app.Application.SharedParametersFilename = original; }
    }
}
