using ClaudeRevit.Services;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace ClaudeRevit.Tests;

public class NormRuleTests
{
    [Theory]
    [InlineData("Кухня-столовая", RoomKind.KitchenDining)]
    [InlineData("Кухня", RoomKind.Kitchen)]
    [InlineData("Спальня 2", RoomKind.Bedroom)]
    [InlineData("Общая комната", RoomKind.CommonLiving)]
    [InlineData("Жилая комната", RoomKind.LivingGeneric)]
    [InlineData("Лифтовой холл", RoomKind.Lobby)]
    [InlineData("Коридор", RoomKind.Corridor)]
    [InlineData("Внутриквартирный коридор", RoomKind.ApartmentCorridor)]
    [InlineData("Прихожая", RoomKind.ApartmentHall)]
    [InlineData("Лестничная клетка", RoomKind.Stair)]
    [InlineData("Лоджия", RoomKind.Loggia)]
    [InlineData("Балкон", RoomKind.Balcony)]
    [InlineData("С/у", RoomKind.Wet)]
    [InlineData("Bedroom", RoomKind.Bedroom)]
    [InlineData("", RoomKind.Other)]
    public void ClassifiesRoomNames(string name, RoomKind kind) => Assert.Equal(kind, NormRules.Classify(name));

    [Fact]
    public void ThresholdsFollowTheClauseVariants()
    {
        var run = NormRules.Get("stair_run_width");
        Assert.Equal(1200, NormRules.Threshold(run, new()).Value);
        Assert.Equal(1050, NormRules.Threshold(run, new(FunctionalClass: "Ф1.3")).Value);
        Assert.Equal(1050, NormRules.Threshold(run, new(FunctionalClass: "f1.3")).Value);
        Assert.Equal(1350, NormRules.Threshold(run, new(FunctionalClass: "Ф2.1")).Value);
        Assert.Equal(1200, NormRules.Threshold(NormRules.Get("corridor_width"), new(CorridorOccupants: 60)).Value);
        Assert.Equal(1000, NormRules.Threshold(NormRules.Get("corridor_width"), new(CorridorOccupants: 50)).Value);
        Assert.Equal(2700, NormRules.Threshold(NormRules.Get("ceiling_height_living"), new(ClimateSubregion: "IА")).Value);
        Assert.Equal(2700, NormRules.Threshold(NormRules.Get("ceiling_height_living"), new(ClimateSubregion: "IVA")).Value);
        Assert.Equal(2500, NormRules.Threshold(NormRules.Get("ceiling_height_living"), new(ClimateSubregion: "IIВ")).Value);
        Assert.Equal(5, NormRules.Threshold(NormRules.Get("area_kitchen"), new(OneRoomApartment: true)).Value);
        var o = NormRules.Threshold(NormRules.Get("evac_exit_width"), new(Overrides: new Dictionary<string, double> { ["evac_exit_width"] = 1200 }));
        Assert.Equal((1200, "project override"), (o.Value, o.Basis));
    }

    [Fact]
    public void PassesWithMeasurementTolerance()
    {
        var w = NormRules.Threshold(NormRules.Get("evac_exit_width"), new());
        Assert.True(NormRules.Passes(w, 799.6));
        Assert.False(NormRules.Passes(w, 790));
        var riser = NormRules.Threshold(NormRules.Get("stair_riser_max"), new());
        Assert.True(NormRules.Passes(riser, 220.3));
        Assert.False(NormRules.Passes(riser, 230));
    }

    [Fact]
    public void CatalogIsConsistent()
    {
        Assert.Equal(NormRules.Ru.Count, NormRules.Ru.Select(r => r.Id).Distinct().Count());
        Assert.All(NormRules.Ru, r => Assert.True(r.Value > 0 && (r.Clause.StartsWith("п. ") || r.Clause.StartsWith("разд. ")) && r.Document.StartsWith("СП ") && NormRules.Checks.Contains(r.Check)));
    }

    [Theory]
    [InlineData(1.2, 10, 3.4, 1.2)]     // 1.2 × 10 m corridor
    [InlineData(16, 4, 4, 4)]          // square: discriminant 0
    [InlineData(10, 0, 0, 0)]
    public void EquivalentWidthOfARectangle(double w, double l, double unusedArea, double expected)
    {
        _ = unusedArea;
        var area = w * l; var perimeter = 2 * (w + l);
        Assert.Equal(expected, NormRules.EquivalentWidth(area, perimeter), 6);
    }

    [Fact]
    public void TepCountsApartmentsAndSummerSpaces()
    {
        var rooms = new List<TepRoom>
        {
            new("1 этаж", 0, 18, RoomKind.CommonLiving, "1"), new("1 этаж", 0, 9, RoomKind.Kitchen, "1"), new("1 этаж", 0, 4, RoomKind.Loggia, "1"),
            new("1 этаж", 0, 16, RoomKind.CommonLiving, "2"), new("1 этаж", 0, 12, RoomKind.Bedroom, "2"), new("1 этаж", 0, 3, RoomKind.Balcony, "2"),
            new("1 этаж", 0, 20, RoomKind.Corridor, null), new("Подвал", -3000, 50, RoomKind.Technical, null),
        };
        var levels = new List<TepLevel> { new("Подвал", -3000, 300, 3000), new("1 этаж", 0, 320, 3000), new("Кровля", 3000, 330, 0) };
        var tep = TepCalculator.Compute(rooms, levels).ToDictionary(t => t.Name, t => t.Value);
        Assert.Equal(2, tep["Количество квартир"]);
        Assert.Equal(1, tep["1-комнатные квартиры"]);
        Assert.Equal(1, tep["2-комнатные квартиры"]);
        Assert.Equal(46, tep["Жилая площадь"]);
        Assert.Equal(55, tep["Площадь квартир"]);
        Assert.Equal(55 + 4 * 0.5 + 3 * 0.3, tep["Общая площадь квартир с летними помещениями"], 6);
        Assert.Equal(1, tep["Этажность (надземные этажи)"]);
        Assert.Equal(2, tep["Количество этажей (всего)"]);
        Assert.Equal(330, tep["Площадь застройки"]);
        Assert.Equal(900, tep["Строительный объём подземной части"]);
    }
}

public class PdfDrawingTests
{
    [Fact]
    public void SimilarityFromTwoPointsMapsBothExactly()
    {
        var t = Similarity.FromPairs(new(10, 10), new(0, 0), new(110, 10), new(0, 5000));
        var a = t.Apply(new Pt(10, 10)); var b = t.Apply(new Pt(110, 10));
        Assert.Equal(0, a.X, 6); Assert.Equal(0, a.Y, 6);
        Assert.Equal(0, b.X, 6); Assert.Equal(5000, b.Y, 6);
        Assert.Equal(50, t.Scale, 6);
    }

    [Fact]
    public void ScaleCalibrationUsesPaperMillimetres()
    {
        var t = Similarity.FromScale(100, new(0, 0), new(0, 0));
        Assert.Equal(1000, t.Apply(new Pt(72 / 25.4 * 10, 0)).X, 6);   // 10 paper mm at 1:100 = 1 m
        Assert.Equal(100, t.ImpliedDenominator, 6);
    }

    [Fact]
    public void DetectsWallFacePairsAndIgnoresLonePlusThinLines()
    {
        var segs = new List<Seg>
        {
            new(new(0, 0), new(6000, 0), 1), new(new(0, 200), new(6000, 200), 1),        // 200 mm wall
            new(new(0, 0), new(0, 4000), 1), new(new(300, 0), new(300, 4000), 1),         // 300 mm wall
            new(new(1000, 2000), new(5000, 2000), 1),                                      // lone line
            new(new(1000, 2010), new(5000, 2010), 1),                                      // 10 mm apart: hatch, not wall
        };
        var walls = PdfDrawing.DetectWalls(segs, 80, 700, 500);
        Assert.Equal(2, walls.Count);
        var w200 = walls.Single(w => Math.Abs(w.Thickness - 200) < 1e-6);
        Assert.Equal(100, w200.A.Y, 6); Assert.Equal(6000, Math.Abs(w200.B.X - w200.A.X), 6);
        Assert.Contains(walls, w => Math.Abs(w.Thickness - 300) < 1e-6 && Math.Abs(w.A.X - 150) < 1e-6);
    }

    [Fact]
    public void MergesDashedSegmentsIntoOneLine()
    {
        var dashes = Enumerable.Range(0, 20).Select(i => new Seg(new(i * 100, 50), new(i * 100 + 70, 50), 0.3)).ToList();
        var merged = PdfDrawing.MergeCollinear(dashes, 40);
        var line = Assert.Single(merged);
        Assert.Equal(1970, line.Length, 6);
    }

    [Fact]
    public void ReadsLinesAndLabelsFromAGeneratedPdfAndCalibratesByGrids()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(842, 595);
        // Grid A (vertical at x=100) and 1 (horizontal at y=100); grid B vertical at x=400; labels past the ends.
        page.DrawLine(new PdfPoint(100, 80), new PdfPoint(100, 500), 0.5);
        page.DrawLine(new PdfPoint(400, 80), new PdfPoint(400, 500), 0.5);
        page.DrawLine(new PdfPoint(80, 100), new PdfPoint(700, 100), 0.5);
        page.DrawLine(new PdfPoint(80, 300), new PdfPoint(700, 300), 0.5);
        page.AddText("A", 10, new PdfPoint(97, 510), font);
        page.AddText("B", 10, new PdfPoint(397, 510), font);
        page.AddText("1", 10, new PdfPoint(708, 97), font);
        page.AddText("2", 10, new PdfPoint(708, 297), font);
        var file = Path.Combine(Path.GetTempPath(), $"cr-pdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(file, builder.Build());
        try
        {
            var read = PdfDrawing.Read(file, 1);
            Assert.Equal(4, read.Segments.Count);
            Assert.Contains(read.Labels, l => l.Text == "A");
            var grids = PdfDrawing.DetectGrids(read.Segments, read.Labels, 200, 20);
            Assert.Equal(new[] { "1", "2", "A", "B" }, grids.Select(g => g.Label).ToArray());
            // Model: A at x=0, B at x=6000 (mm); 1 at y=0, 2 at y=4000 — i.e. 300 pt = 6000 mm => 20 mm/pt.
            var model = new Dictionary<string, (Pt, Pt)>
            {
                ["A"] = (new(0, -1000), new(0, 9000)), ["B"] = (new(6000, -1000), new(6000, 9000)),
                ["1"] = (new(-1000, 0), new(12000, 0)), ["2"] = (new(-1000, 4000), new(12000, 4000)),
            };
            var (t, rms, points) = PdfDrawing.FromGridMatches(grids, model);
            Assert.Equal(4, points);
            Assert.True(rms < 1, $"rms {rms}");
            Assert.Equal(20, t.Scale, 3);
            var origin = t.Apply(new Pt(100, 100));
            Assert.Equal(0, origin.X, 3); Assert.Equal(0, origin.Y, 3);
        }
        finally { File.Delete(file); }
    }
}
