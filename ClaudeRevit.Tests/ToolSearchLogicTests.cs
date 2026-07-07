using System.Collections.Generic;
using System.Linq;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

// Progressive tool loading — the pure search/prewarm core. Verifies the model can always reach a
// deferred group (via prewarm keywords or find_tools) and that the core set stays tight.
public class ToolSearchLogicTests
{
    // A small stand-in catalogue spanning core + several deferred groups.
    private static List<ToolSearchLogic.ToolInfo> Catalog() => new()
    {
        new("create_wall", "Creates a straight wall.", "Modeling", true),
        new("get_selection", "Returns selected elements.", "Query", true),
        new("execute_csharp", "Runs C#.", "Code & learning", true),
        new("find_tools", "Loads specialised tools.", "Code & learning", true),
        new("create_rebar", "Creates rebar inside a structural host.", "Rebar", false),
        new("create_rebar_batch", "Creates several straight rebars.", "Rebar", false),
        new("get_rebar_in_host", "Lists reinforcement in a host.", "Rebar", false),
        new("create_section", "Creates a building section view.", "Views", false),
        new("create_elevation", "Creates an elevation marker.", "Views", false),
        new("create_schedule", "Creates a quantities schedule.", "Schedules", false),
        new("create_duct", "Creates a straight duct.", "MEP", false),
    };

    [Fact]
    public void Search_by_group_word_reveals_whole_group()
    {
        var r = ToolSearchLogic.Search(Catalog(), "place rebar stirrups");
        Assert.Contains("Rebar", r.Categories);
        // All three rebar tools travel together, not just the one keyword hit.
        Assert.Contains("create_rebar", r.Message);
        Assert.Contains("create_rebar_batch", r.Message);
        Assert.Contains("get_rebar_in_host", r.Message);
    }

    [Fact]
    public void Search_never_reveals_core_or_other_groups()
    {
        var r = ToolSearchLogic.Search(Catalog(), "rebar");
        Assert.DoesNotContain("Views", r.Categories);
        Assert.DoesNotContain("create_wall", r.Message);   // core stays out of results
        Assert.DoesNotContain("create_section", r.Message); // unrelated group stays out
    }

    [Fact]
    public void Search_matches_on_description_not_just_name()
    {
        // "quantities" appears only in the schedule tool's description.
        var r = ToolSearchLogic.Search(Catalog(), "quantities");
        Assert.Contains("Schedules", r.Categories);
        Assert.Contains("create_schedule", r.Message);
    }

    [Fact]
    public void Search_with_no_match_returns_group_menu_and_no_reveal()
    {
        var r = ToolSearchLogic.Search(Catalog(), "zxqwv");
        Assert.Empty(r.Categories);
        Assert.Contains("on-demand groups", r.Message);
    }

    [Theory]
    [InlineData("добавь арматуру в колонну", "Rebar")]
    [InlineData("make a section through the building", "Views")]
    [InlineData("create a quantities schedule of walls", "Schedules")]
    [InlineData("export the sheets to pdf", "Export")]
    [InlineData("run the duct along the corridor", "MEP")]
    public void Prewarm_loads_the_obvious_group(string prompt, string expected)
    {
        Assert.Contains(expected, ToolSearchLogic.Prewarm(prompt));
    }

    [Fact]
    public void Prewarm_stays_quiet_on_a_plain_modelling_request()
    {
        // A bare wall request must NOT drag in any specialised group.
        Assert.Empty(ToolSearchLogic.Prewarm("build a wall from 0,0 to 10,0 on Level 1"));
    }

    [Fact]
    public void Core_set_is_tight_and_contains_find_tools()
    {
        Assert.Contains("find_tools", ToolSearchLogic.CoreToolNames);
        Assert.Contains("create_wall", ToolSearchLogic.CoreToolNames);
        // Deferred tools must not be in core, or they'd defeat the point.
        Assert.DoesNotContain("create_rebar", ToolSearchLogic.CoreToolNames);
        Assert.DoesNotContain("create_section", ToolSearchLogic.CoreToolNames);
        // Keep it genuinely small — a curated core, not "most of the catalogue".
        Assert.True(ToolSearchLogic.CoreToolNames.Count < 70,
            $"core grew to {ToolSearchLogic.CoreToolNames.Count}; keep it tight");
    }
}
