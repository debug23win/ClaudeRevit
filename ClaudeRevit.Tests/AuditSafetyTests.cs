using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeRevit.Services;
using ClaudeRevit.Tools;
using ModPlus.Headless;
using Xunit;

namespace ClaudeRevit.Tests;

public class AuditSafetyTests
{
    [Theory]
    [InlineData("{\"succeeded\":false,\"traceback\":\"boom\"}")]
    [InlineData("{\"error\":\"Python host error\"}")]
    [InlineData("{\"ok\":true,\"succeeded\":false}")]
    [InlineData("{\"ok\":false}")]
    [InlineData("{\"ok\":0,\"failed\":2}")]
    [InlineData("{\"status\":\"failed\"}")]
    public void LegacyFailuresStayFailuresAcrossCompletionAndCap(string raw)
    {
        var source = JsonNode.Parse(raw)!.AsObject(); source["payload"] = new string('Я', 45_000);
        var result = ToolResultCap.Apply(ToolResult.Complete(source.ToJsonString()), "test");
        Assert.False(ToolResult.LooksSuccessful(result));
        using var parsed = JsonDocument.Parse(result);
        Assert.False(ToolResult.LooksSuccessful(parsed.RootElement.GetRawText()));
        Assert.True(result.Length <= ToolResultCap.MaxChars);
    }

    [Fact]
    public void LargePreviewKeepsControlAndErrorFields()
    {
        var source = new JsonObject { ["ok"] = false, ["preview"] = true, ["committed"] = false,
            ["rolled_back"] = true, ["plan_token"] = "token", ["error"] = "failed", ["payload"] = new string('x', 45_000) };
        using var parsed = JsonDocument.Parse(ToolResultCap.Apply(source.ToJsonString(), "preview"));
        var r = parsed.RootElement;
        Assert.True(r.GetProperty("preview").GetBoolean());
        Assert.True(r.GetProperty("rolled_back").GetBoolean());
        Assert.False(r.GetProperty("committed").GetBoolean());
        Assert.Equal("token", r.GetProperty("plan_token").GetString());
        Assert.Equal("failed", r.GetProperty("error").GetString());
    }

    [Fact]
    public void LargeDiagnosticsStayBoundedAndKeepArrayContracts()
    {
        var source = new JsonObject { ["ok"] = false, ["error"] = new string('\0', 40_000),
            ["warnings"] = new JsonArray(new string('Я', 40_000)), ["errors"] = new JsonArray(new string('\n', 40_000)) };
        var result = ToolResultCap.Apply(source.ToJsonString(), "failure");
        Assert.True(result.Length <= ToolResultCap.MaxChars);
        using var parsed = JsonDocument.Parse(result);
        Assert.Equal(JsonValueKind.Array, parsed.RootElement.GetProperty("errors").ValueKind);
        Assert.Equal(JsonValueKind.Array, parsed.RootElement.GetProperty("warnings").ValueKind);
        Assert.False(parsed.RootElement.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("Я\"\\\n")]
    [InlineData("😀")]
    public void PagesRoundTripBeyondOldThirtyThousandLimit(string pattern)
    {
        var text = string.Concat(Enumerable.Repeat(pattern, 40_000));
        var restored = new StringBuilder(); var offset = 0;
        do
        {
            var page = ToolResultCap.Page("fixture", text, offset, 30_000);
            // Pages must survive the production dispatcher cap unchanged.
            Assert.Equal(page, ToolResultCap.Apply(page, "get_full_result"));
            using var parsed = JsonDocument.Parse(page); var r = parsed.RootElement;
            restored.Append(r.GetProperty("content").GetString());
            if (!r.GetProperty("has_more").GetBoolean()) break;
            var next = r.GetProperty("next_offset").GetInt32(); Assert.True(next > offset); offset = next;
        } while (true);
        Assert.Equal(text, restored.ToString());
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(4, 100)]
    [InlineData(0, 0)]
    [InlineData(0, 30001)]
    public void InvalidPageBoundsFailExplicitly(int offset, int limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ToolResultCap.Page("x", "abc", offset, limit));

    [Fact]
    public void UnicodePageRequestsCannotStallOrSplitPairs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolResultCap.Page("x", "😀", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolResultCap.Page("x", "😀", 1, 2));
    }

    [Fact]
    public void AgingRetainsFailureAndRollbackAsOneJsonValue()
    {
        var raw = new JsonObject { ["ok"] = false, ["preview"] = true, ["committed"] = false,
            ["payload"] = new string('x', 2000) }.ToJsonString();
        var turn = new ApiTurn { Blocks = { new ChatToolResultBlock("test", raw, true) } };
        Assert.Equal(1, ToolResultAging.AgeAll(new[] { turn }));
        var result = Assert.IsType<ChatToolResultBlock>(turn.Blocks[0]);
        using var parsed = JsonDocument.Parse(result.Content);
        Assert.False(parsed.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(parsed.RootElement.GetProperty("preview").GetBoolean());
        Assert.False(parsed.RootElement.GetProperty("committed").GetBoolean());
        Assert.Equal(0, ToolResultAging.AgeAll(new[] { turn }));
    }

    [Fact]
    public void ArchiveRetainsMoreThanTheFormerHundredThousandCharacters()
    {
        var text = new string('x', 120_000) + "THE_END";
        var id = "test_" + Guid.NewGuid().ToString("N"); ToolResultArchive.Record(id, text);
        Assert.Equal(text, ToolResultArchive.Lookup(id));
    }

    [Fact]
    public void PreviewAndPartialStatusesAreDistinctFromCommittedSuccess()
    {
        using var preview = JsonDocument.Parse(ToolResult.Execution("{\"ok\":true,\"preview\":true}", true, false));
        Assert.False(preview.RootElement.GetProperty("committed").GetBoolean());
        Assert.Equal("preview", preview.RootElement.GetProperty("execution_status").GetString());
        using var partial = JsonDocument.Parse(ToolResult.Complete("{\"ok\":3,\"failed\":1}"));
        Assert.Equal(3, partial.RootElement.GetProperty("ok").GetInt32());
        Assert.Equal("partial", partial.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void CyrillicFitsHeadlessBudgetWithoutAsciiExpansion()
    {
        var raw = new JsonObject { ["ok"] = true, ["text"] = new string('Я', 40_000) }.ToJsonString(ToolResult.Options);
        var result = HeadlessResultLimits.Compact(raw);
        Assert.True(result.Length <= 30_000);
        Assert.DoesNotContain("\\u042F", result);
        Assert.Contains(new string('Я', 9000), result);
    }

    [Theory]
    [InlineData(7850)]
    [InlineData(2500)]
    [InlineData(20)]
    public void DensityUsesKilogramsRatherThanPounds(double expected)
    {
        // Independently defined cubic-foot conversion; do not reuse Units constants.
        var internalDensity = expected * Math.Pow(0.3048, 3);
        Assert.Equal(expected, Units.DensityToKgM3(internalDensity), 8);
    }

    [Theory]
    [InlineData("1.0", 1)]
    [InlineData("\"1\"", 1)]
    [InlineData("0", 0)]
    public void IntegralJsonNumbersUseTheValidatedReader(string raw, int expected)
    {
        using var parsed = JsonDocument.Parse(raw);
        Assert.Equal(expected, ToolInput.NonnegativeInteger(parsed.RootElement, "steps"));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("true")]
    public void InvalidIntegersAreValidationErrors(string raw)
    {
        using var parsed = JsonDocument.Parse(raw);
        Assert.Throws<ToolInputException>(() => ToolInput.NonnegativeInteger(parsed.RootElement, "steps"));
    }

    [Fact]
    public void AxisGridAndSectionSizeDoNotLoadRebarOrFamilyAuthoring()
        => Assert.Empty(ToolSearchLogic.Prewarm("Создай колонны сечением 400х400 по сетке осей"));

    [Fact]
    public void ExactDiscoveryLoadsOneSchemaAndVagueVerbLoadsNone()
    {
        var catalog = Enumerable.Range(0, 50).Select(i => new ToolSearchLogic.ToolInfo("create_rebar_" + i, "Creates rebar.", "Rebar", false)).ToArray();
        Assert.Single(ToolSearchLogic.Search(catalog, "create_rebar_24").ToolNames);
        Assert.Empty(ToolSearchLogic.Search(catalog, "create").ToolNames);
        Assert.InRange(ToolSearchLogic.Search(catalog, "rebar").ToolNames.Count, 1, 12);
    }

    [Fact]
    public void CatalogPagingKeepsCountsAndDoesNotDiscardLateEntries()
    {
        using var catalog = JsonDocument.Parse("{\"materials\":[\"A\",\"B\",\"C\"],\"levels\":[1,2]}");
        var page = CatalogPage.Select(catalog.RootElement, new[] { "materials" }, null, 1, 1);
        Assert.False(page["catalog"]!.AsObject().ContainsKey("levels"));
        Assert.Equal("B", page["catalog"]!["materials"]![0]!.GetValue<string>());
        Assert.Equal(3, page["paging"]!["materials"]!["total"]!.GetValue<int>());
        Assert.Equal(2, page["paging"]!["materials"]!["next_offset"]!.GetValue<int>());
    }

    [Fact]
    public void CatalogFiltersCategoriesAndPreservesQualifiedTypeNames()
    {
        using var catalog = JsonDocument.Parse("{\"family_types_by_category\":{\"Columns\":[\"Family A: Type 1\"],\"Doors\":[\"Door: Type 1\"]}}");
        var page = CatalogPage.Select(catalog.RootElement, null, "Family A", 0, 40, "Columns");
        var categories = page["catalog"]!["family_types_by_category"]!.AsObject();
        Assert.Single(categories); Assert.Equal("Family A: Type 1", categories["Columns"]![0]!.GetValue<string>());
    }

    [Fact]
    public void PhaseMeasurementsAreScopedAndDisposeOnlyOnce()
    {
        using var task = TaskJournal.Start("audit-test");
        var phase = TaskJournal.MeasurePhase("verification"); phase.Dispose(); phase.Dispose();
        var metrics = TaskJournal.ReadPhases(TaskJournal.CurrentId);
        Assert.Equal(1, metrics["verification"].Count);
        Assert.True(metrics["verification"].Seconds >= 0);
    }
}
