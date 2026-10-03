using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class CompactMcpToolsTests
{
    private static readonly ToolSearchLogic.ToolInfo[] Tools =
    {
        new("create_rebar", "Create bars in a host", "Rebar", false),
        new("set_rebar_constraints", "Set bar constraints", "Rebar", false),
        new("create_family_form", "Create a parametric form", "Family editor", false),
        new("create_wall", "Create a wall", "Modeling", true)
    };
    [Fact]
    public void ExactNativeNameOutranksPartialMatches() => Assert.Equal("create_rebar", CompactMcpTools.Search(Tools, "create_rebar")[0]);
    [Theory]
    [InlineData("сложное армирование", "create_rebar")]
    [InlineData("параметрическое семейство", "create_family_form")]
    [InlineData("create wall", "create_wall")]
    public void FindsEnabledToolsAcrossAllGroupsAndCore(string query, string expected) => Assert.Contains(expected, CompactMcpTools.Search(Tools, query));
    [Theory]
    [InlineData("")]
    [InlineData("xyznoexist")]
    public void UnknownQueryReturnsNoUnrelatedSchemas(string query) => Assert.Empty(CompactMcpTools.Search(Tools, query));
}
