using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class RoomNumberingTests
{
    private static readonly (double X, double Y)[] Grid = [(0, 10), (10, 10.4), (20, 9.8), (0, 0), (10, 0.3), (20, -0.2)];

    [Fact]
    public void ReadingOrderBandsRowsTopDownLeftToRight() =>
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, RoomNumbering.ReadingOrder(Grid, 1.5, snake: false).ToArray());

    [Fact]
    public void SnakeReversesEveryOtherRow() =>
        Assert.Equal(new[] { 0, 1, 2, 5, 4, 3 }, RoomNumbering.ReadingOrder(Grid, 1.5, snake: true).ToArray());

    [Fact]
    public void PathOrderFollowsTheRoute()
    {
        // An L-shaped corridor: up the left side, then right along the top.
        var pts = new (double, double)[] { (5, 1), (1, 30), (40, 41), (1, 10), (20, 41) };
        var path = new (double, double)[] { (0, 0), (0, 40), (50, 40) };
        Assert.Equal(new[] { 0, 3, 1, 4, 2 }, RoomNumbering.PathOrder(pts, path).ToArray());
    }

    [Theory]
    [InlineData("{L}{N:00}", 1, 5, null, "105")]
    [InlineData("{L}.{N}", 12, 3, null, "12.3")]
    [InlineData("{A}.{N}", 2, 4, "17", "17.4")]
    [InlineData("П{N:000}", 0, 7, null, "П007")]
    [InlineData("{L}{N:00}", -1, 2, null, "-102")]
    public void FormatsNumbers(string format, int level, int seq, string? apt, string expected) =>
        Assert.Equal(expected, RoomNumbering.Format(format, level, seq, apt));

    [Fact]
    public void FormatRequiresASequenceToken() => Assert.Throws<ArgumentException>(() => RoomNumbering.Format("{L}", 1, 1, null));

    [Fact]
    public void CompactsConsecutiveRoomNumbers()
    {
        Assert.Equal("101–103, 105", RoomNumbering.CompactList(["103", "101", "102", "105"]));
        Assert.Equal("1, 2, 10", RoomNumbering.CompactList(["10", "2", "1"]));
        Assert.Equal("1.1–1.3", RoomNumbering.CompactList(["1.2", "1.1", "1.3"]));
    }

    [Fact]
    public void EstimatesWrappedLines()
    {
        Assert.Equal(1, RoomNumbering.Lines("Кухня", 40, 2.5));
        Assert.Equal(3, RoomNumbering.Lines(new string('x', 60), 40, 2.5));
        Assert.Equal(2, RoomNumbering.Lines("a\nb", 40, 2.5));
    }
}
