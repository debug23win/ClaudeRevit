using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class ModPlusTests
{
    private static JsonElement Catalog()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return ModPlusCatalog.Parse(File.ReadAllText(Path.Combine(root, "ClaudeRevit", "Standards", "ModPlus2027.json")));
    }
    [Fact]
    public void SnapshotIncludesEveryMappedModuleAndKeepsUnavailableVersionsExplicit()
    {
        var catalog = Catalog();
        var modules = catalog.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(86, modules.Length);
        Assert.Equal(84, modules.Count(m => m.GetProperty("available2027").GetBoolean()));
        Assert.Equal(215, modules.Sum(m => m.GetProperty("externalCommands").GetArrayLength()));
        Assert.False(ModPlusCatalog.Module(catalog, "mprCopyParameterValues").GetProperty("available2027").GetBoolean());
        var sum = ModPlusCatalog.Module(catalog, "mprSumParameters");
        Assert.Equal("2e58211f115267ae5bc629c4bf9669ec9dccb0879c0f04100d6830250fe3ebfd", sum.GetProperty("sha256").GetString());
        Assert.Contains(sum.GetProperty("algorithmClasses").EnumerateArray(), c => c.GetProperty("class").GetString() == "mprSumParameters.Services");
    }
    [Theory]
    [InlineData("сумма параметров", "mprSumParameters")]
    [InlineData("mprsumparameters", "mprSumParameters")]
    [InlineData("армирование балок", "mprBeamsReinforcement")]
    public void SearchFindsRussianTitlesAndModuleIds(string query, string expected) =>
        Assert.Contains(ModPlusCatalog.Search(Catalog(), query), m => m.GetProperty("module").GetString() == expected);
    [Fact]
    public void UnknownModuleCannotTurnIntoAFilePath() => Assert.Throws<ArgumentException>(() => ModPlusCatalog.Module(Catalog(), "../outside"));
    [Theory]
    [InlineData("../secret.cs")]
    [InlineData("source/../secret.cs")]
    [InlineData("source\\..\\secret.cs")]
    [InlineData("C:\\secret.cs")]
    [InlineData("source/file.cs:stream")]
    [InlineData("source/file.dll")]
    public void SourcePathsCannotEscapeTheLocalBundle(string relative) =>
        Assert.Throws<InvalidDataException>(() => ModPlusCatalog.SourcePath(Path.GetTempPath(), relative));
    [Fact]
    public void SourceExcerptHasExactLineNumbersAndBoundedSize()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs");
        try
        {
            File.WriteAllLines(file, ["one", "two", new string('x', 30000)]);
            Assert.Equal("2: two", ModPlusCatalog.Excerpt(file, 2, 1));
            Assert.True(ModPlusCatalog.Excerpt(file, 3, 1).Length < 12100);
            Assert.Throws<ArgumentException>(() => ModPlusCatalog.Excerpt(file, 4, 1));
            Assert.Throws<ArgumentException>(() => ModPlusCatalog.Excerpt(file, 0, 1));
            Assert.Throws<ArgumentException>(() => ModPlusCatalog.Excerpt(file, 1, 301));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void WrongCatalogVersionAndDuplicateModulesAreRejected()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(Catalog().GetRawText())!;
        root["targetRevit"] = 2026;
        Assert.Throws<InvalidDataException>(() => ModPlusCatalog.Parse(root.ToJsonString()));
        root["targetRevit"] = 2027;
        root["modules"]!.AsArray().Add(root["modules"]![0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => ModPlusCatalog.Parse(root.ToJsonString()));
    }
    [Fact]
    public void CommandIdMustMatchTheActualTabPanelAndButton()
    {
        var id = ModPlusCatalog.CommandId("Utilities", "Sum");
        Assert.True(ModPlusCatalog.MatchesCommandId(id, "Utilities", "Sum"));
        Assert.False(ModPlusCatalog.MatchesCommandId(id, "Other", "Sum"));
        Assert.False(ModPlusCatalog.MatchesCommandId(id.Replace("ModPlus", "BIM-STARTER"), "Utilities", "Sum"));
    }
    [Fact]
    public void OnlyTheExactPreparedExtensionIsSuperseded()
    {
        const string legacy = "f3ce15434c9a20fbecbda059f6800345e0c52d329245fba1b7a5ff101ce64edb";
        Assert.True(ModPlusCatalog.IsPreparedExtension("ModPlusTools.cs", legacy.ToUpperInvariant()));
        Assert.False(ModPlusCatalog.IsPreparedExtension("EditedModPlus.cs", legacy));
        Assert.False(ModPlusCatalog.IsPreparedExtension("ModPlusTools.cs", new string('0', 64)));
    }
    public sealed class Item
    {
        public string Name { get; init; } = "Length";
        public string StorageType => "Double";
        public bool IsText { get; init; }
        public string Unit => "m";
        public string DisplayValue { get; init; } = "";
        public double SumRoundedValue { get; init; }
    }
    [Fact]
    public void ServiceRowsKeepRawTotalsAndApplyRoundingAfterTheCoefficient()
    {
        var page = ModPlusTotals.Read(new[] { new Item { SumRoundedValue = 1.23456 }, new Item { Name = "Mark", IsText = true, DisplayValue = "A, B" } }, "", 0, 25, 3, 2);
        Assert.Equal(1.23456, page.Rows[0].Sum);
        Assert.Equal(1.235, page.Rows[0].Rounded);
        Assert.Equal(2.469, page.Rows[0].ScaledRounded);
        Assert.Equal("1.23456", page.Rows[0].SumExact);
        Assert.Equal("1.235", page.Rows[0].RoundedExact);
        Assert.Equal("2.469", page.Rows[0].ScaledRoundedExact);
        Assert.Null(page.Rows[1].Sum);
        Assert.Equal("A, B", page.Rows[1].DisplayValue);
        Assert.Null(page.NextOffset);
    }
    [Fact]
    public void FilteredPagingCountsAllMatchesAndBoundsModelText()
    {
        var items = Enumerable.Range(0, 5).Select(i => new Item { Name = i == 0 ? "Mark" : "Length " + i, DisplayValue = new string('x', 10000) }).ToArray();
        var page = ModPlusTotals.Read(items, "length", 1, 2, 3, 1);
        Assert.Equal(4, page.Total);
        Assert.Equal(3, page.NextOffset);
        Assert.Equal("Length 2", page.Rows[0].Name);
        Assert.All(page.Rows, r => { Assert.True(r.TextTruncated); Assert.True(r.DisplayValue.Length <= 257); });
    }
    [Fact]
    public void ChangedServiceContractNonfiniteResultsAndCancellationDoNotBecomeSuccess()
    {
        Assert.Throws<InvalidOperationException>(() => ModPlusTotals.Read(new[] { new object() }, "", 0, 25, 3, 1));
        Assert.Throws<InvalidOperationException>(() => ModPlusTotals.Read(new[] { new Item { SumRoundedValue = double.NaN } }, "", 0, 25, 3, 1));
        Assert.Throws<ArgumentException>(() => ModPlusTotals.Read(Array.Empty<Item>(), "", 0, 25, 3, double.PositiveInfinity));
        Assert.Throws<OperationCanceledException>(() => ModPlusTotals.Read(new[] { new Item() }, "", 0, 25, 3, 1, () => throw new OperationCanceledException()));
    }
    [Fact]
    public void RussianModPlusRequestsPrewarmItsDeferredGroup() => Assert.Contains("ModPlus", ToolSearchLogic.Prewarm("Модплюс: сумма параметров"));
    // The router is deferred with its group: it must not ride along on every request.
    [Fact]
    public void HeadlessRouterIsNotACoreTool() => Assert.DoesNotContain("run_modplus_headless", ToolSearchLogic.CoreToolNames);
    [Fact]
    public void EmbeddedSnapshotsAreReadGzippedOrPlain()
    {
        const string json = "{\"schemaVersion\":1}";
        var gz = new MemoryStream();
        using (var zip = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zip.Write(System.Text.Encoding.UTF8.GetBytes(json));
        var gzBytes = gz.ToArray();
        Assert.Equal(json, ModPlusCatalog.ReadEmbedded(n => n == "data.json.gz" ? new MemoryStream(gzBytes) : null, "data.json"));
        Assert.Equal(json, ModPlusCatalog.ReadEmbedded(n => n == "data.json" ? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)) : null, "data.json"));
        Assert.Null(ModPlusCatalog.ReadEmbedded(_ => null, "data.json"));
    }
}
