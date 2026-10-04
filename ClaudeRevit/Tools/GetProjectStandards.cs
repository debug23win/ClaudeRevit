using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public class GetProjectStandards : IRevitTool
{
    public string Name => "get_project_standards";
    public string Description => "Inspect the live project's or family's standards: shared GUIDs, source matches, " +
        "actual Forge data types, instance/type category bindings, unit formats, view templates, browser " +
        "organization and schedules. For a schedule_id also return fields, hidden columns, shared GUIDs, " +
        "filters and sorting. Works with upgraded BIMStarter/ADSK templates; never infers an edition from names alone.";
    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional GUID/name substring for parameter bindings." }),
            ["offset"] = JsonSerializer.SerializeToElement(new { type = "integer", minimum = 0 }),
            ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer", minimum = 1, maximum = 300, description = "Default 100." }),
            ["schedule_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Optional schedule to inspect in detail, from the returned schedule list." })
        }, Required = []
    };
    public bool RequiresTransaction => false;

    internal sealed record LiveParameter(long Id, string Name, Guid? Guid, string Spec, string Binding,
        IReadOnlyList<object> Categories, object? UnitFormat);

    private static string Spec(Definition definition)
    { try { return definition.GetDataType().TypeId; } catch { return ""; } }
    private static object? UnitFormat(Document doc, Definition definition)
    {
        try
        {
            var spec = definition.GetDataType();
            if (!UnitUtils.IsMeasurableSpec(spec)) return null;
            var format = doc.GetUnits().GetFormatOptions(spec);
            return new { unit = format.GetUnitTypeId().TypeId, accuracy = format.Accuracy };
        }
        catch { return null; }
    }
    private static List<LiveParameter> Parameters(Document doc)
    {
        if (doc.IsFamilyDocument)
            return doc.FamilyManager.Parameters.Cast<FamilyParameter>().Select(p => new LiveParameter(
                p.Id.Value, p.Definition.Name, p.IsShared ? p.GUID : null, Spec(p.Definition),
                p.IsInstance ? "instance" : "type", Array.Empty<object>(), UnitFormat(doc, p.Definition))).ToList();
        var shared = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement))
            .Cast<SharedParameterElement>().ToDictionary(p => p.Id.Value);
        var result = new List<LiveParameter>();
        var bound = new HashSet<long>();
        var iterator = doc.ParameterBindings.ForwardIterator();
        iterator.Reset();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition definition || iterator.Current is not ElementBinding binding) continue;
            long id = definition.Id.Value;
            bound.Add(id);
            result.Add(new(id, definition.Name, shared.TryGetValue(id, out var parameter) ? parameter.GuidValue : null,
                Spec(definition), binding is InstanceBinding ? "instance" : "type",
                binding.Categories.Cast<Autodesk.Revit.DB.Category>().Select(c => (object)new { id = c.Id.Value, name = c.Name }).ToList(),
                UnitFormat(doc, definition)));
        }
        foreach (var parameter in shared.Values.Where(p => !bound.Contains(p.Id.Value)))
        {
            var definition = parameter.GetDefinition();
            result.Add(new(parameter.Id.Value, definition.Name, parameter.GuidValue, Spec(definition),
                "shared-definition-without-project-binding", Array.Empty<object>(), UnitFormat(doc, definition)));
        }
        return result.OrderBy(p => p.Name).ToList();
    }
    private static (IReadOnlyList<StandardParameterMatching.Evidence> Evidence, string? Warning) Evidence(
        Document doc, List<LiveParameter> parameters)
    {
        var reference = StandardKnowledge.Definitions.ToList();
        string? warning = null;
        try
        {
            var configured = StandardKnowledge.Configured(doc.Application.SharedParametersFilename);
            if (configured != null) reference.AddRange(configured.Definitions);
        }
        catch (Exception ex) { warning = "Configured FOP could not be read: " + ex.Message; }
        return (StandardParameterMatching.Detect(parameters.Select(p => (p.Guid, p.Name)), reference), warning);
    }

    // Sent with every pane prompt; deliberately bounded and fresh (no stale binding cache).
    public static object ContextSummary(Document doc)
    {
        try
        {
            var parameters = Parameters(doc);
            var (evidence, warning) = Evidence(doc, parameters);
            return new { is_family = doc.IsFamilyDocument, profiles = evidence, shared_parameter_count = parameters.Count(p => p.Guid.HasValue),
                warning, inspect = "get_project_standards; get_shared_parameter_catalog" };
        }
        catch (Exception ex) { return new { warning = "Standard inspection failed: " + ex.Message, inspect = "get_project_standards" }; }
    }

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document ?? throw new InvalidOperationException("No document is open.");
        var parameters = Parameters(doc);
        var (evidence, warning) = Evidence(doc, parameters);
        var query = input.TryGetValue("query", out var q) ? q.GetString() ?? "" : "";
        var matching = parameters.Where(p => string.IsNullOrEmpty(query) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (p.Guid?.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        int offset = input.TryGetValue("offset", out var o) ? Math.Max(0, o.GetInt32()) : 0;
        int limit = input.TryGetValue("limit", out var l) ? Math.Clamp(l.GetInt32(), 1, 300) : 100;
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
        var schedules = views.OfType<ViewSchedule>().OrderBy(v => v.Name).ToList();
        object? detail = null;
        if (input.TryGetValue("schedule_id", out var s))
            detail = ScheduleDetail(doc, doc.GetElement(new ElementId(s.GetInt64())) as ViewSchedule
                ?? throw new InvalidOperationException("schedule_id does not identify a ViewSchedule."));
        string? Browser(Func<BrowserOrganization?> read)
        { try { return doc.IsFamilyDocument ? null : read()?.Name; } catch { return null; } }
        return Services.Json.Serialize(new
        {
            document = doc.Title, is_family_document = doc.IsFamilyDocument, profiles = evidence, warning,
            binding_count = parameters.Count, matching_count = matching.Count, offset,
            parameters = matching.Skip(offset).Take(limit), next_offset = offset + limit < matching.Count ? (int?)(offset + limit) : null,
            view_template_count = views.Count(v => v.IsTemplate),
            view_templates = views.Where(v => v.IsTemplate).Take(120).Select(v => new { id = v.Id.Value, name = v.Name, kind = v.ViewType.ToString() }),
            schedule_count = schedules.Count, schedules = schedules.Take(120).Select(v => new { id = v.Id.Value, name = v.Name }),
            browser = new { views = Browser(() => BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc)),
                sheets = Browser(() => BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc)) },
            schedule_detail = detail,
            guidance = StandardKnowledge.AgentRules,
            note = "Source GUID matches do not identify an exact RTE revision. Lists of templates/schedules are capped at 120; query_elements retrieves remaining ids."
        });
    }

    private static object ScheduleDetail(Document doc, ViewSchedule schedule)
    {
        var definition = schedule.Definition;
        Guid? GuidFor(ElementId id) => (doc.GetElement(id) as SharedParameterElement)?.GuidValue;
        return new
        {
            id = schedule.Id.Value, name = schedule.Name, is_itemized = definition.IsItemized,
            fields = definition.GetFieldOrder().Select(id =>
            {
                var field = definition.GetField(id);
                return new { field_id = id.IntegerValue, name = field.GetName(), heading = field.ColumnHeading,
                    parameter_id = field.ParameterId.Value, shared_guid = GuidFor(field.ParameterId),
                    kind = field.FieldType.ToString(), hidden = field.IsHidden, spec = field.GetSpecTypeId().TypeId,
                    calculated = field.IsCalculatedField };
            }).ToList(),
            filters = definition.GetFilters().Select(f => new { field_id = f.FieldId.IntegerValue,
                operation = f.FilterType.ToString(), value = FilterValue(f) }).ToList(),
            sorting = definition.GetSortGroupFields().Select(f => new { field_id = f.FieldId.IntegerValue,
                direction = f.SortOrder.ToString(), header = f.ShowHeader, footer = f.ShowFooter }).ToList(),
            note = "Calculated/combined fields are identified; this API does not expose every formula as text."
        };
    }
    private static object? FilterValue(ScheduleFilter filter)
    {
        if (filter.IsStringValue) return filter.GetStringValue();
        if (filter.IsDoubleValue) return filter.GetDoubleValue();
        if (filter.IsIntegerValue) return filter.GetIntegerValue();
        if (filter.IsElementIdValue) return filter.GetElementIdValue().Value;
        return null;
    }
}
