namespace ClaudeRevit.Services;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static Rgb Parse(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var value))
            throw new ArgumentException("Color must be #RRGGBB.");
        return new((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
    public static Rgb Interpolate(Rgb from, Rgb to, double fraction)
    {
        if (!double.IsFinite(fraction)) throw new ArgumentException("Invalid gradient fraction.");
        var t = Math.Clamp(fraction, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return new(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}

public static class ParameterColors
{
    public static readonly string[] Palette = ["#4477AA", "#EE6677", "#228833", "#CCBB44", "#66CCEE", "#AA3377", "#BBBBBB"];
    public static double Fraction(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || !double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum < minimum)
            throw new ArgumentException("Invalid gradient range.");
        return maximum == minimum ? 0.5 : Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }
}
