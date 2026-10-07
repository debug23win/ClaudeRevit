using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class NormCatalogTests
{
    [Fact]
    public void BuiltInCatalogKeepsBaseRulesAndOptionalSections()
    {
        var c = NormRules.Load(null);
        Assert.Equal(15, c.Rules.Count(r => r.Section == "base"));
        Assert.Contains(c.Rules, r => r.Id == "mgn_door_width" && r.Section == "sp59" && r.Value == 900);
        Assert.Contains(c.Rules, r => r.Id == "fire_distance" && r.Section == "fire_distance");
        Assert.Empty(c.Overrides); Assert.Empty(c.Disabled);
    }

    [Fact]
    public void OfficeFileOverridesDisablesAndAddsRules()
    {
        const string json = """
        {
          // comments allowed
          "overrides": { "corridor_width": 1400 },
          "disable": ["area_kitchen"],
          "rules": [
            { "id": "office_lobby", "document": "СТО бюро", "clause": "п. 3.1", "check": "room_width", "value": 2400, "kinds": ["Lobby"] },
            { "id": "evac_exit_width", "document": "СП 1.13130.2020", "clause": "п. 4.2.19", "check": "door_clear_width", "value": 900, "door_scope": "all", "section": "base" },
          ]
        }
        """;
        var c = NormRules.Load(json, "office.json");
        Assert.Equal(1400, c.Overrides["corridor_width"]);
        Assert.Contains("area_kitchen", c.Disabled);
        var lobby = c.Rules.Single(r => r.Id == "office_lobby");
        Assert.Equal(("office", "room_width", RoomKind.Lobby), (lobby.Section, lobby.Check, lobby.Kinds!.Single()));
        var evac = c.Rules.Single(r => r.Id == "evac_exit_width");
        Assert.Equal((900.0, "all"), (evac.Value, evac.DoorScope));
        Assert.Equal("office.json", c.Source);
    }

    [Theory]
    [InlineData("""{"rules":[{"id":"x","check":"nonsense","value":1}]}""")]
    [InlineData("""{"rules":[{"id":"x","check":"room_area","value":1}]}""")]
    [InlineData("""{"rules":[{"id":"x","check":"room_area","value":1,"kinds":["Garage"]}]}""")]
    [InlineData("""{"overrides":{"no_such_rule":1}}""")]
    [InlineData("""{"rules":[{"id":"x","check":"door_clear_width"}]}""")]
    public void RejectsBrokenOfficeFiles(string json) => Assert.Throws<ArgumentException>(() => NormRules.Load(json));

    [Theory]
    [InlineData("II C0", "II C0", 6000)]
    [InlineData("I С0", "III С1", 8000)]
    [InlineData("IV C1", "IV C0", 10000)]
    [InlineData("V", "II C0", 10000)]
    [InlineData("IV C3", "V", 15000)]
    public void FireDistancesFollowTable1(string a, string b, double mm) => Assert.Equal(mm, NormRules.FireDistanceMm(a, b));

    [Fact]
    public void FireGroupRejectsCombinationsOutsideTheTable() => Assert.Throws<ArgumentException>(() => NormRules.FireGroup("I C2"));

    [Fact]
    public void FootprintDistanceBetweenHulls()
    {
        var a = NormRules.Hull([(0, 0), (10000, 0), (10000, 10000), (0, 10000), (5000, 5000)]);
        Assert.Equal(4, a.Count);
        var b = NormRules.Hull([(16000, 2000), (20000, 2000), (20000, 8000), (16000, 8000)]);
        Assert.Equal(6000, NormRules.FootprintDistance(a, b), 6);
        var overlapping = NormRules.Hull([(9000, 9000), (12000, 9000), (12000, 12000), (9000, 12000)]);
        Assert.Equal(0, NormRules.FootprintDistance(a, overlapping));
    }
}

public class NormRulesExampleFileTests
{
    [Fact]
    public void TheDocumentedExampleFileLoads()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "docs", "examples", "norm-rules.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var c = NormRules.Load(File.ReadAllText(Path.Combine(dir!, "docs", "examples", "norm-rules.json")));
        Assert.Contains(c.Rules, r => r.Id == "office_office_area" && r.NamePattern == "кабинет");
        Assert.Equal(1400, c.Overrides["corridor_width"]);
    }
}
