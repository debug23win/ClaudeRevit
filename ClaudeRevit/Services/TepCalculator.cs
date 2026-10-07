using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeRevit.Services;

// Технико-экономические показатели (ТЭП) from rooms and levels. Idea from the NewLevelHub fork of
// mcp-servers-for-revit (export_tep_data / render_tep_table); the indicators and the summer-space
// coefficients (лоджии 0,5, балконы и террасы 0,3 — СП 54.13330.2022, приложение А) are applied
// here as pure arithmetic so they are unit-tested. Values the model cannot give exactly (footprint,
// construction volume) are labelled with the method used, never presented as surveyed figures.
public sealed record TepRoom(string Level, double LevelElevationMm, double AreaM2, RoomKind Kind, string? Apartment);
public sealed record TepLevel(string Name, double ElevationMm, double SlabAreaM2, double StoreyHeightMm);
public sealed record TepIndicator(string Name, string Unit, double Value, string Method);

public static class TepCalculator
{
    public const double LoggiaFactor = 0.5, BalconyFactor = 0.3;

    public static List<TepIndicator> Compute(IReadOnlyList<TepRoom> rooms, IReadOnlyList<TepLevel> levels, double groundElevationMm = 0)
    {
        var result = new List<TepIndicator>();
        bool Above(double elevation) => elevation >= groundElevationMm - 1;
        var roomLevels = rooms.Select(r => (r.Level, r.LevelElevationMm)).Distinct().ToList();
        var levelsAbove = levels.Where(l => Above(l.ElevationMm)).ToList();
        var levelsBelow = levels.Where(l => !Above(l.ElevationMm)).ToList();

        if (levelsAbove.Count > 0)
            result.Add(new("Площадь застройки", "м²", levelsAbove.Max(l => l.SlabAreaM2),
                "наибольшая суммарная площадь перекрытий надземного уровня (приближённо, по плитам)"));
        result.Add(new("Этажность (надземные этажи)", "эт.", roomLevels.Count(l => Above(l.LevelElevationMm)), "уровни с помещениями не ниже отметки земли"));
        result.Add(new("Количество этажей (всего)", "эт.", roomLevels.Count, "уровни с помещениями"));
        if (levels.Count > 0)
        {
            result.Add(new("Строительный объём надземной части", "м³", Round(levelsAbove.Sum(l => l.SlabAreaM2 * l.StoreyHeightMm / 1000)),
                "Σ площадь перекрытий уровня × высота этажа (приближённо)"));
            if (levelsBelow.Count > 0)
                result.Add(new("Строительный объём подземной части", "м³", Round(levelsBelow.Sum(l => l.SlabAreaM2 * l.StoreyHeightMm / 1000)),
                    "Σ площадь перекрытий уровня × высота этажа (приближённо)"));
        }

        bool Summer(RoomKind k) => k is RoomKind.Balcony or RoomKind.Loggia;
        result.Add(new("Площадь помещений (без летних)", "м²", Round(rooms.Where(r => !Summer(r.Kind)).Sum(r => r.AreaM2)), "Σ площадей помещений"));

        var apartments = rooms.Where(r => !string.IsNullOrWhiteSpace(r.Apartment)).GroupBy(r => r.Apartment!.Trim()).ToList();
        var dwelling = apartments.Count > 0 ? apartments.SelectMany(g => g).ToList() : rooms.ToList();
        result.Add(new("Жилая площадь", "м²", Round(dwelling.Where(r => NormRules.IsLiving(r.Kind)).Sum(r => r.AreaM2)),
            apartments.Count > 0 ? "Σ жилых комнат в квартирах" : "Σ жилых комнат (квартиры не размечены)"));
        if (apartments.Count == 0) return result;

        result.Add(new("Площадь квартир", "м²", Round(dwelling.Where(r => !Summer(r.Kind)).Sum(r => r.AreaM2)), "без лоджий и балконов"));
        result.Add(new("Общая площадь квартир с летними помещениями", "м²", Round(dwelling.Sum(r => r.AreaM2 * Factor(r.Kind))),
            "лоджии × 0,5; балконы и террасы × 0,3 (СП 54.13330.2022, прил. А)"));
        result.Add(new("Количество квартир", "шт.", apartments.Count, "по параметру квартиры"));
        foreach (var g in apartments.GroupBy(a => a.Count(r => NormRules.IsLiving(r.Kind))).OrderBy(g => g.Key))
            result.Add(new(g.Key == 0 ? "Квартиры без жилых комнат (проверьте имена помещений)" : $"{g.Key}-комнатные квартиры", "шт.", g.Count(), "по числу жилых комнат"));
        return result;
    }

    public static double Factor(RoomKind k) => k switch { RoomKind.Loggia => LoggiaFactor, RoomKind.Balcony => BalconyFactor, _ => 1 };
    private static double Round(double v) => Math.Round(v, 2);
}
