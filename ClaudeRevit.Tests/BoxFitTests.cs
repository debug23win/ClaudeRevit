using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class BoxFitTests
{
    private static List<(double, double, double)> Box(double cx, double cy, double l, double w, double deg, double z0, double z1)
    {
        var a = deg * Math.PI / 180; var pts = new List<(double, double, double)>();
        foreach (var su in new[] { -1, 1 }) foreach (var sv in new[] { -1, 1 }) foreach (var z in new[] { z0, z1 })
                {
                    var u = su * l / 2; var v = sv * w / 2;
                    pts.Add((cx + u * Math.Cos(a) - v * Math.Sin(a), cy + u * Math.Sin(a) + v * Math.Cos(a), z));
                }
        return pts;
    }

    [Theory]
    [InlineData(0, 0, 6000, 250, 0)]
    [InlineData(1000, -500, 4000, 300, 37)]
    [InlineData(0, 0, 5000, 200, 90)]
    public void FitsRotatedWalls(double cx, double cy, double l, double w, double deg)
    {
        var box = BoxFit.Fit(Box(cx, cy, l, w, deg, 0, 3000));
        Assert.Equal(l, box.Length, 2); Assert.Equal(w, box.Width, 2);
        Assert.Equal(cx, box.Center.X, 2); Assert.Equal(cy, box.Center.Y, 2);
        Assert.Equal(3000, box.Height, 6);
        var expected = deg * Math.PI / 180; while (expected > Math.PI / 2) expected -= Math.PI;
        Assert.Equal(Math.Abs(Math.Cos(expected)), Math.Abs(Math.Cos(box.AngleRad)), 6);
    }

    [Fact]
    public void RejectsDegenerateInput() => Assert.Throws<ArgumentException>(() => BoxFit.Fit([(0, 0, 0), (1, 0, 0)]));
}
