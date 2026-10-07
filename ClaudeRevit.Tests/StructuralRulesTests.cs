using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class StructuralRulesTests
{
    [Fact]
    public void SupportZonesKeepTheirSpacingAndTheMiddleFillsEvenly()
    {
        var runs = StirrupZones.Layout(6000, [new(900, 100), new(null, 200), new(900, 100)], 50, 50);
        Assert.Equal(3, runs.Count);
        Assert.Equal((50.0, 10, 100.0), (runs[0].StartMm, runs[0].Count, runs[0].SpacingMm));
        Assert.Equal(950, runs[0].EndMm, 6);
        Assert.Equal((5050.0, 10, 100.0), (runs[2].StartMm, runs[2].Count, runs[2].SpacingMm));
        Assert.Equal(5950, runs[2].EndMm, 6);
        // 4100 mm between 950 and 5050 at ≤ 200 → 21 intervals of 195.24, 20 stirrups inside.
        Assert.Equal(20, runs[1].Count);
        Assert.True(runs[1].SpacingMm <= 200);
        Assert.Equal(950 + runs[1].SpacingMm, runs[1].StartMm, 6);
        Assert.Equal(5050 - runs[1].SpacingMm, runs[1].EndMm, 6);
    }

    [Fact]
    public void SingleOpenZoneSpansTheWholeMemberIncludingBothEnds()
    {
        var run = Assert.Single(StirrupZones.Layout(3000, [new(null, 150)], 50, 50));
        Assert.Equal(50, run.StartMm, 6); Assert.Equal(2950, run.EndMm, 6); Assert.True(run.SpacingMm <= 150);
    }

    [Fact]
    public void ZonesWithoutAnOpenZoneRunForwardWithoutDuplicates()
    {
        var runs = StirrupZones.Layout(2000, [new(500, 100), new(1400, 200)], 50, 50);
        Assert.Equal(50, runs[0].StartMm); Assert.Equal(6, runs[0].Count);        // 50..550
        Assert.Equal(750, runs[1].StartMm, 6);                                      // next at +200
        Assert.True(runs[1].EndMm <= 1950 + 1e-6);
    }

    [Fact]
    public void RejectsImpossibleZones()
    {
        Assert.Throws<ArgumentException>(() => StirrupZones.Layout(1000, [new(null, 100), new(null, 200)], 0, 0));
        Assert.Throws<ArgumentException>(() => StirrupZones.Layout(1000, [new(800, 100), new(800, 100)], 0, 0));
        Assert.Throws<ArgumentException>(() => StirrupZones.Layout(1000, [new(500, 0)], 0, 0));
    }

    [Fact]
    public void BasicCombinationsFollowSp20Coefficients()
    {
        var cases = new List<LoadCaseSpec> { new("СВ", LoadKind.Dead, 1.1), new("Полезная", LoadKind.Short, 1.2), new("Снег", LoadKind.Short, 1.4), new("Ветер", LoadKind.Short, 1.4) };
        var uls = LoadCombinations.Basic(cases, ultimate: true, "РСН");
        Assert.Equal(6, uls.Count);                                // 3 × 2 orderings of the two leading short loads
        var first = uls[0].Components.ToDictionary(c => c.Case, c => c.Factor);
        Assert.Equal(1.1, first["СВ"]);
        Assert.Equal(1.2, first["Полезная"]);                      // ψt1 = 1
        Assert.Equal(Math.Round(1.4 * 0.9, 4), first["Снег"]);     // ψt2 = 0.9
        Assert.Equal(Math.Round(1.4 * 0.7, 4), first["Ветер"]);    // ψt3 = 0.7
        var sls = LoadCombinations.Basic(cases, ultimate: false, "НС");
        Assert.Equal(0.7, sls[0].Components.Single(c => c.Case == "Ветер").Factor);
    }

    [Fact]
    public void LongTermLoadsUseLeadingAndAccompanyingFactors()
    {
        var cases = new List<LoadCaseSpec> { new("G", LoadKind.Dead, 1.1), new("L1", LoadKind.Long, 1.2), new("L2", LoadKind.Long, 1.2) };
        var c = LoadCombinations.Basic(cases, true, "C");
        Assert.Equal(2, c.Count);
        Assert.Equal(Math.Round(1.2 * 0.95, 4), c[0].Components.Single(x => x.Case == "L2").Factor);
    }

    [Theory]
    [InlineData("4С 5Вр1-100/5Вр1-100 230×500 25/25", 5, 100, 5, 100, 2300.0, 5000.0)]
    [InlineData("12A500С-200/8A240-600", 12, 200, 8, 600, null, null)]
    [InlineData("8 А500 - 150 / 8 А500 - 150 150x300", 8, 150, 8, 150, 1500.0, 3000.0)]
    public void ParsesMeshDesignations(string text, double d1, double s1, double d2, double s2, double? w, double? l)
    {
        var m = MeshDesignations.Parse(text);
        Assert.Equal((d1, s1, d2, s2, w, l), (m.LongDiameterMm, m.LongSpacingMm, m.CrossDiameterMm, m.CrossSpacingMm, m.WidthMm, m.LengthMm));
    }

    [Fact]
    public void BendingProfileIsDetectedFromModelParameters()
    {
        Assert.Equal("adsk", BendingScheduleProfiles.Detect(n => n.StartsWith("ADSK_")));
        Assert.Equal("bimstarter", BendingScheduleProfiles.Detect(n => n.StartsWith("Мрк.") || n.StartsWith("Рзм.")));
        Assert.Equal("native", BendingScheduleProfiles.Detect(_ => false));
    }

    [Fact]
    public void RowCentresSkipHeaderRows()
    {
        var c = BendingScheduleProfiles.RowCentres(100, [10], [8, 20, 20], firstDataRow: 1);
        Assert.Equal(new[] { 100 - 10 - 8 - 10.0, 100 - 10 - 8 - 20 - 10.0 }, c.ToArray());
    }
}
