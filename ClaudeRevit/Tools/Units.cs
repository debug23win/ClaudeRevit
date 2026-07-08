namespace ClaudeRevit.Tools;

// Single source of truth for the feet↔metric conversions that were copy-pasted (as raw magic
// numbers or per-file local helpers) across ~19 tools. Revit's internal unit is decimal feet;
// the add-in speaks mm / m² / m³ to the user. Pure math, no Revit types — unit-tested.
public static class Units
{
    // Exact by definition: 1 ft = 0.3048 m = 304.8 mm.
    public const double MmPerFoot = 304.8;
    public const double SqMPerSqFoot = 0.09290304;      // 0.3048^2
    public const double CuMPerCuFoot = 0.028316846592;  // 0.3048^3

    public static double FeetToMm(double feet) => feet * MmPerFoot;
    public static double MmToFeet(double mm) => mm / MmPerFoot;

    public static double SqFeetToSqM(double sqFeet) => sqFeet * SqMPerSqFoot;
    public static double SqMToSqFeet(double sqM) => sqM / SqMPerSqFoot;

    public static double CuFeetToCuM(double cuFeet) => cuFeet * CuMPerCuFoot;
    public static double CuMToCuFeet(double cuM) => cuM / CuMPerCuFoot;
}
