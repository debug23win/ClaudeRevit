using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeRevit.Services;

// Russian code-compliance rules (СП РФ) the norm audit measures the model against. Idea from the
// NewLevelHub fork of mcp-servers-for-revit (normative checks with drawing callouts, written there
// for СП РК); the catalog, measurement and thresholds here are our own and follow the Russian
// codes. Pure logic — no Revit types — so the thresholds and classification are unit-tested.
//
// Thresholds are the general-case values of the cited clause. Several clauses scale with the
// building's functional class, climate subregion or occupant count; NormContext carries those so
// the right variant is chosen, and every rule can be overridden per project. Clause numbers are
// those of the editions named in Document, including their published amendments.
public enum RoomKind
{
    Other, CommonLiving, Bedroom, LivingGeneric, Kitchen, KitchenDining, KitchenNiche,
    ApartmentHall, ApartmentCorridor, Wet, Corridor, Lobby, Stair, Balcony, Loggia, Technical
}

public sealed record NormRule(
    string Id, string Document, string Clause, string Subject, string Measure,
    bool AtLeast, double Value, string Unit, string Requirement);

public sealed record NormContext(
    string? FunctionalClass = null, string? ClimateSubregion = null, int? CorridorOccupants = null,
    bool OneRoomApartment = false, IReadOnlyDictionary<string, double>? Overrides = null);

public sealed record NormThreshold(NormRule Rule, double Value, string Basis);

public static class NormRules
{
    public const string Sp1 = "СП 1.13130.2020";
    public const string Sp54 = "СП 54.13330.2022";

    public static IReadOnlyList<NormRule> Ru { get; } =
    [
        new("evac_exit_width", Sp1, "п. 4.2.19", "door on an evacuation path", "clear width", true, 800, "mm",
            "Ширина эвакуационных выходов в свету — не менее 0,8 м"),
        new("evac_exit_height", Sp1, "п. 4.2.18", "door on an evacuation path", "clear height", true, 1900, "mm",
            "Высота эвакуационных выходов в свету — не менее 1,9 м"),
        new("corridor_width", Sp1, "п. 4.3.3", "corridor / horizontal evacuation path", "width", true, 1000, "mm",
            "Ширина горизонтальных участков путей эвакуации — не менее 1,0 м (1,2 м при эвакуации более 50 человек)"),
        new("stair_run_width", Sp1, "п. 4.4.1", "evacuation stair", "run width", true, 1200, "mm",
            "Ширина марша — не менее 1,2 м (1,05 м — Ф1.3 высотой до 75 м; 1,35 м — Ф1.1, Ф2.1, Ф2.2, Ф3.4, Ф4.1)"),
        new("stair_riser_max", Sp1, "п. 4.4.3", "evacuation stair", "riser height", false, 220, "mm",
            "Высота ступени — не более 22 см"),
        new("stair_riser_min", Sp1, "п. 4.4.3", "evacuation stair", "riser height", true, 50, "mm",
            "Высота ступени — не менее 5 см"),
        new("stair_tread_min", Sp1, "п. 4.4.3", "evacuation stair", "tread depth", true, 250, "mm",
            "Ширина проступи — не менее 25 см"),
        new("stair_slope_max", Sp1, "п. 4.4.3", "evacuation stair", "riser / tread", false, 1.0, "ratio",
            "Уклон лестниц на путях эвакуации — не более 1:1"),
        new("ceiling_height_living", Sp54, "п. 5.12", "living room or kitchen", "floor-to-ceiling height", true, 2500, "mm",
            "Высота жилых комнат и кухни — не менее 2,5 м (2,7 м в подрайонах IA, IБ, IГ, IД, IVА)"),
        new("ceiling_height_circulation", Sp54, "п. 5.12", "apartment hall / corridor", "floor-to-ceiling height", true, 2100, "mm",
            "Высота внутриквартирных коридоров, холлов, передних — не менее 2,1 м"),
        new("area_common_living", Sp54, "п. 5.11", "common living room", "area", true, 16, "m²",
            "Общая жилая комната — не менее 16 м² (14 м² в однокомнатной квартире)"),
        new("area_bedroom", Sp54, "п. 5.11", "bedroom", "area", true, 8, "m²",
            "Спальня — не менее 8 м² (10 м² на двух человек)"),
        new("area_kitchen", Sp54, "п. 5.11", "kitchen", "area", true, 8, "m²",
            "Кухня — не менее 8 м² (5 м² в однокомнатной квартире)"),
        new("railing_height_exterior", Sp54, "п. 6.4.4", "balcony, loggia or exterior stair railing", "height", true, 1200, "mm",
            "Высота ограждений балконов, лоджий, наружных лестниц — не менее 1,2 м"),
        new("railing_height_interior", Sp54, "п. 6.4.5", "interior stair railing", "height", true, 900, "mm",
            "Высота ограждений внутренних лестниц — не менее 0,9 м"),
    ];

    public static NormRule Get(string id) =>
        Ru.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException($"Unknown norm rule '{id}'. Known: {string.Join(", ", Ru.Select(r => r.Id))}.");

    private static readonly string[] ColdSubregions = ["IA", "IB", "IG", "ID", "IVA"];
    private static readonly string[] HighOccupancyClasses = ["Ф1.1", "Ф2.1", "Ф2.2", "Ф3.4", "Ф4.1"];

    // The threshold that applies in this context: an explicit project override wins, otherwise the
    // clause's variant for the functional class / climate / occupancy, otherwise the general value.
    public static NormThreshold Threshold(NormRule rule, NormContext context)
    {
        if (context.Overrides != null && context.Overrides.TryGetValue(rule.Id, out var o))
            return new(rule, o, "project override");
        var fc = NormalizeClass(context.FunctionalClass);
        switch (rule.Id)
        {
            case "corridor_width" when context.CorridorOccupants > 50:
                return new(rule, 1200, "more than 50 evacuees");
            case "stair_run_width" when fc != null && HighOccupancyClasses.Contains(fc):
                return new(rule, 1350, fc);
            case "stair_run_width" when fc == "Ф1.3":
                return new(rule, 1050, "Ф1.3 (height up to 75 m)");
            case "ceiling_height_living" when ColdSubregions.Contains(NormalizeSubregion(context.ClimateSubregion)):
                return new(rule, 2700, "climate subregion " + context.ClimateSubregion!.Trim());
            case "area_common_living" when context.OneRoomApartment:
                return new(rule, 14, "one-room apartment");
            case "area_kitchen" when context.OneRoomApartment:
                return new(rule, 5, "one-room apartment");
        }
        return new(rule, rule.Value, "general case");
    }

    // Measurement noise (unit round-trips, modelled finish thickness) shouldn't flip a result, so a
    // hair under the limit passes; 0.5 mm / 0.005 m² / 0.001 ratio.
    public static bool Passes(NormThreshold t, double measured)
    {
        var tol = t.Rule.Unit switch { "m²" => 0.005, "ratio" => 0.001, _ => 0.5 };
        return t.Rule.AtLeast ? measured >= t.Value - tol : measured <= t.Value + tol;
    }

    public static string? NormalizeClass(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().ToUpperInvariant().Replace('F', 'Ф').Replace(',', '.').Replace(" ", "");
        return s.StartsWith("Ф") ? s : "Ф" + s;
    }

    // Canonical Latin spelling: subregions are typed with Cyrillic or Latin look-alike letters
    // (IА / IA, IVА / IVA, IIВ / IIV), and both must select the same variant.
    private static string NormalizeSubregion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var map = new Dictionary<char, char> { ['А'] = 'A', ['Б'] = 'B', ['В'] = 'V', ['Г'] = 'G', ['Д'] = 'D' };
        return new string(s.Trim().ToUpperInvariant().Where(c => c != ' ').Select(c => map.TryGetValue(c, out var l) ? l : c).ToArray());
    }

    // Room purpose from its name, Russian first (the codes are Russian), English as a fallback.
    // Order matters: "кухня-столовая" before "кухня", "лифтовой холл" before generic "холл".
    public static RoomKind Classify(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
        if (n.Length == 0) return RoomKind.Other;
        bool Has(params string[] keys) => keys.Any(n.Contains);
        if (Has("кухня-столовая", "кухня-гостиная", "kitchen-dining", "kitchen/dining")) return RoomKind.KitchenDining;
        if (Has("кухня-ниша", "кухонная ниша", "kitchenette")) return RoomKind.KitchenNiche;
        if (Has("кухня", "kitchen")) return RoomKind.Kitchen;
        if (Has("спальн", "bedroom")) return RoomKind.Bedroom;
        if (Has("общая комната", "гостиная", "living room", "lounge")) return RoomKind.CommonLiving;
        if (Has("жилая комната", "комната")) return RoomKind.LivingGeneric;
        if (Has("лоджия", "loggia")) return RoomKind.Loggia;
        if (Has("балкон", "терраса", "balcony", "terrace")) return RoomKind.Balcony;
        if (Has("лестничн", "лестница", "stair")) return RoomKind.Stair;
        if (Has("передняя", "прихожая", "entrance hall", "foyer")) return RoomKind.ApartmentHall;
        if (Has("внутриквартирн")) return RoomKind.ApartmentCorridor;
        if (Has("коридор", "corridor", "галерея")) return RoomKind.Corridor;
        if (Has("холл", "вестибюль", "тамбур", "лифтов", "hall", "lobby", "vestibule")) return RoomKind.Lobby;
        if (Has("санузел", "с/у", "ванн", "уборн", "туалет", "душев", "bath", "toilet", "wc", "shower")) return RoomKind.Wet;
        if (Has("техническ", "электрощит", "венткамер", "насосн", "тепловой пункт", "mechanical", "electrical")) return RoomKind.Technical;
        return RoomKind.Other;
    }

    public static bool IsLiving(RoomKind k) => k is RoomKind.CommonLiving or RoomKind.Bedroom or RoomKind.LivingGeneric;
    public static bool IsEvacuationSpace(RoomKind k) => k is RoomKind.Corridor or RoomKind.Lobby or RoomKind.Stair;

    // Width of the rectangle with the room's area and perimeter — exact for rectangular rooms and
    // a fair "corridor width" for long L/T shapes, where it tends to the leg width. For shapes too
    // compact to be a rectangle (discriminant < 0) the square side is the answer.
    public static double EquivalentWidth(double area, double perimeter)
    {
        if (area <= 0 || perimeter <= 0) return 0;
        var half = perimeter / 2;
        var d = half * half - 4 * area;
        return d <= 0 ? Math.Sqrt(area) : (half - Math.Sqrt(d)) / 2;
    }
}
