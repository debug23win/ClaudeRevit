using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text.Json;

namespace ClaudeRevit.Services;

public static class StandardKnowledge
{
    public const string AgentRules =
        "PROJECT STANDARDS: inspect get_project_standards before editing template parameters, views, " +
        "schedules or authoring families. It reports live bindings, shared GUIDs, data types, units, " +
        "templates and schedule fields. get_shared_parameter_catalog searches the bundled BIMStarter " +
        "2020 RU/ENG and ADSK 2019/2021 GUID references and the configured Revit FOP; an explicit file_path reads another FOP. " +
        "Shared parameter identity is the GUID; translated names and ADSK_ prefixes are hints only. " +
        "Use set_parameter(parameter_guid=...) for shared parameters. Never replace a standard shared " +
        "parameter with add_family_parameter: that creates a different, non-shared parameter. " +
        "Preserve instance/type bindings and actual spec/unit; legacy mass parameters may be NUMBER. " +
        "A configured FOP is a definition library, not proof that its parameters are bound in this document. " +
        "BIMStarter/Weandrevit assembly counting: verify the live schedule first; the documented 2020 " +
        "scheme uses one Орг.ГлавнаяДетальСборки per product, Орг.ИзделиеТипПодсчета=1 for cages or 4 " +
        "for embedded parts, and a common Мрк.МаркаИзделия containing a hyphen. Propagate product " +
        "parameters to shared nested components; a Revit group/assembly alone does not establish counting. " +
        "Nested IFC reinforcement families are not native Rebar objects. Check the actual element class " +
        "before applying Rebar constraints, layouts or host-mark logic. ADSK view organization often uses " +
        "ADSK_Назначение вида; a view template can lock it. Reuse existing template/type/schedule " +
        "definitions; inspect their fields, filters and sorting rather than inventing an equivalent. " +
        "Templates upgraded from 2020/2022 retain their original parameter identities; inspect the live " +
        "document and do not infer its standard edition from the running Revit version. " +
        "Use get_standard_workflows for BIMStarter, ADSK and opt-in Samolet EIR v5.0 rules. " +
        "Samolet requires the customer project FOP/UPM/PIM/naming appendices; names are not verified GUIDs. " +
        "When that profile applies, avoid CAD in any nested family, KR Parts/Paint/Steel tools, " +
        "numeric dimension overrides and permanent per-element hide/graphic overrides. Use filters/templates. " +
        "Check rebar zones, continuous length-meter runs and approved quantity multipliers without double-counting. " +
        "For complex families analyze nesting and flex multiple dimensions/types before saving; for native rebar " +
        "inspect actual constraints/couplers rather than assuming nested IFC families behave like Rebar. " +
        "get_bimstarter_tools maps all plugin commands; partial workflows are not full ports. " +
        "run_bimstarter_command only posts an interactive command: wait for the user to finish and verify changes.";

    private static readonly Lazy<IReadOnlyList<SharedParameterDefinition>> Bundled = new(() =>
    {
        var result = new List<SharedParameterDefinition>();
        foreach (var resource in new[] { "BimStarter2020.json", "ADSK2019_2021.json" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeRevit.Standards." + resource)
                ?? throw new InvalidOperationException("Bundled parameter reference is missing: " + resource);
            result.AddRange(JsonSerializer.Deserialize<List<SharedParameterDefinition>>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new());
        }
        return result;
    });
    public static IReadOnlyList<SharedParameterDefinition> Definitions => Bundled.Value;

    // One small cache. Re-read when the user's configured FOP changes, without retaining
    // Revit objects, changing SharedParametersFilename, or caching document bindings.
    private static string? _key;
    private static SharedParameterCatalog? _configured;
    public static SharedParameterCatalog? Configured(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The configured Revit FOP was not found.", path);
        var key = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        if (_key != key)
        {
            _configured = SharedParameterCatalog.Read(path);
            _key = key;
        }
        return _configured;
    }
}
