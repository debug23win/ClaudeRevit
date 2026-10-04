using System.Text.Json;

namespace ClaudeRevit.Services;

public static class BenchmarkEligibility
{
    public static string? SkipReason(BenchmarkTask task, string probe)
    {
        using var doc = JsonDocument.Parse(probe);
        var root = doc.RootElement;
        if (root.TryGetProperty("probe_error", out var error)) throw new InvalidOperationException("Objective probe unavailable: " + error);
        if (!root.TryGetProperty("is_family_document", out var family)) throw new InvalidOperationException("Document kind probe unavailable.");
        if (task.FamilyDocument != family.GetBoolean()) return task.FamilyDocument
            ? "Requires an already open ordinary RFA in the Family Editor." : "Requires an RVT project.";
        if (task.RequiresNestedSeed && (!root.TryGetProperty("nested_seed_types", out var seed) || seed.GetArrayLength() == 0))
            return "Load editable nested child families in the scratch RFA before this task.";
        if (task.Id == "F3")
        {
            if (!root.TryGetProperty("family_structure", out var graph) || !graph.TryGetProperty("nodes", out var nodes) ||
                !nodes.EnumerateArray().Any(n => n.GetProperty("depth").GetInt32() >= 2))
                return "Requires an existing nested assembly with at least two child levels.";
        }
        if(task.Id is "R1" or "R3" && root.TryGetProperty("resources",out var resources) &&
            resources.GetProperty("concrete_column_type_ids").GetArrayLength()==0 && resources.GetProperty("valid_rebar_column_ids").GetArrayLength()==0)
            return "Load a concrete structural column family/type into the seed RVT (or a valid rebar column host). Steel-only column seeds cannot run R1/R3; no points assigned.";
        return null;
    }
}
