using ClaudeRevit.Tools;
using Xunit;

namespace ClaudeRevit.Tests;

public class UnitsTests
{
    [Fact]
    public void FootIsExactly304Point8Mm()
    {
        Assert.Equal(304.8, Units.FeetToMm(1.0), 9);
        Assert.Equal(1.0, Units.MmToFeet(304.8), 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3.2808398950131)] // ~1 m in feet
    [InlineData(123.456)]
    public void LengthRoundTrips(double feet)
    {
        Assert.Equal(feet, Units.MmToFeet(Units.FeetToMm(feet)), 9);
    }

    [Fact]
    public void AreaAndVolumeAreLengthSquaredAndCubed()
    {
        // 1 ft^2 = 0.3048^2 m^2, 1 ft^3 = 0.3048^3 m^3.
        Assert.Equal(0.3048 * 0.3048, Units.SqFeetToSqM(1.0), 9);
        Assert.Equal(0.3048 * 0.3048 * 0.3048, Units.CuFeetToCuM(1.0), 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10.5)]
    [InlineData(9999.9)]
    public void AreaAndVolumeRoundTrip(double v)
    {
        Assert.Equal(v, Units.SqMToSqFeet(Units.SqFeetToSqM(v)), 9);
        Assert.Equal(v, Units.CuMToCuFeet(Units.CuFeetToCuM(v)), 9);
    }

    [Fact]
    public void KnownMetreConversion()
    {
        // A 5 m wall is ~16.404 ft internally; converting back gives 5000 mm.
        var feet = Units.MmToFeet(5000);
        Assert.Equal(5000, Units.FeetToMm(feet), 6);
    }
}
