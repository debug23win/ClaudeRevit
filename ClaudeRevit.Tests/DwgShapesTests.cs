using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class DwgShapesTests
{
    private static IEnumerable<Seg> Rect(double cx, double cy, double w, double d, double angleDeg = 0)
    {
        var a = angleDeg * Math.PI / 180; var u = new Pt(Math.Cos(a), Math.Sin(a)); var v = new Pt(-Math.Sin(a), Math.Cos(a));
        var c = new Pt(cx, cy);
        var p = new[] { c - u * (w / 2) - v * (d / 2), c + u * (w / 2) - v * (d / 2), c + u * (w / 2) + v * (d / 2), c - u * (w / 2) + v * (d / 2) };
        for (int i = 0; i < 4; i++) yield return new Seg(p[i], p[(i + 1) % 4], 0);
    }

    [Fact]
    public void FindsColumnRectanglesIncludingRotatedOnes()
    {
        var segs = Rect(0, 0, 400, 600).Concat(Rect(6000, 0, 500, 500, 30)).Concat([new Seg(new(-5000, -5000), new(-1000, -5000), 0)]).ToList();
        var found = DwgShapes.Rectangles(segs);
        Assert.Equal(2, found.Count);
        var a = found.Single(r => Math.Abs(r.Center.X) < 1);
        Assert.Equal((400.0, 600.0, 0.0), (a.Width, a.Depth, a.AngleRad));
        var b = found.Single(r => Math.Abs(r.Center.X - 6000) < 1);
        Assert.Equal(500, b.Width, 1); Assert.Equal(30 * Math.PI / 180, b.AngleRad, 3);
    }

    [Fact]
    public void IgnoresNonRectangularLoops()
    {
        var trapezoid = new List<Seg> { new(new(0, 0), new(1000, 0), 0), new(new(1000, 0), new(800, 500), 0), new(new(800, 500), new(200, 500), 0), new(new(200, 500), new(0, 0), 0) };
        Assert.Empty(DwgShapes.Rectangles(trapezoid));
    }

    [Fact]
    public void NamesGridsTheRussianWay()
    {
        var grids = new List<Seg>
        {
            new(new(6000, -1000), new(6000, 9000), 0), new(new(0, -1000), new(0, 9000), 0),
            new(new(-1000, 6000), new(9000, 6000), 0), new(new(-1000, 0), new(9000, 0), 0),
        };
        var named = DwgShapes.NameGrids(grids).ToDictionary(g => g.Name, g => g.Line);
        Assert.Equal(0, named["1"].A.X); Assert.Equal(6000, named["2"].A.X);
        Assert.Equal(0, named["А"].A.Y); Assert.Equal(6000, named["Б"].A.Y);
    }

    [Fact]
    public void LetterSequenceSkipsForbiddenLetters()
    {
        var letters = Enumerable.Range(0, 22).Select(DwgShapes.Letter).ToArray();
        Assert.DoesNotContain("З", letters); Assert.DoesNotContain("О", letters); Assert.DoesNotContain("Й", letters);
        Assert.Equal("И", letters[7]);
        Assert.Equal("АА", DwgShapes.Letter(22));
    }
}
