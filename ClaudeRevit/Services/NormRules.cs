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
    bool AtLeast, double Value, string Unit, string Requirement)
{
    // What is measured (see NormRules.Checks); the audit dispatches on this, not on the id, so an
    // office can add rules of its own over the same measurements.
    public string Check { get; init; } = "";
    // Which rooms a room check applies to: room kinds and/or a name pattern (regex, case-insensitive).
    public RoomKind[]? Kinds { get; init; }
    public string? NamePattern { get; init; }
    // Doors: "evacuation" (into a corridor, hall or stair, or to the outside) or "all".
    public string DoorScope { get; init; } = "evacuation";
    // Rule set: base runs by default; sp59 (accessibility), fire_distance and office rules run
    // when their section is requested (or their id is listed).
    public string Section { get; init; } = "base";
}

public sealed record NormContext(
    string? FunctionalClass = null, string? ClimateSubregion = null, int? CorridorOccupants = null,
    bool OneRoomApartment = false, IReadOnlyDictionary<string, double>? Overrides = null);

public sealed record NormThreshold(NormRule Rule, double Value, string Basis);

// The rule set in force: the built-in catalog with an office file applied (thresholds changed,
// rules switched off, rules added).
public sealed record NormCatalog(IReadOnlyList<NormRule> Rules, IReadOnlyDictionary<string, double> Overrides, IReadOnlySet<string> Disabled, string? Source);

public static class NormRules
{
    public const string Sp1 = "СП 1.13130.2020";
    public const string Sp54 = "СП 54.13330.2022";
    public const string Sp59 = "СП 59.13330.2020";
    public const string Sp4 = "СП 4.13130.2013";

    // Measurements the audit knows how to take.
    public static readonly string[] Checks =
    [
        "door_clear_width", "door_clear_height", "corridor_width", "room_width", "room_length", "room_area", "room_height",
        "stair_run_width", "stair_riser", "stair_tread", "stair_slope", "railing_height_stair", "railing_height_other", "building_distance"
    ];

    private static readonly RoomKind[] LivingAndKitchen = [RoomKind.CommonLiving, RoomKind.Bedroom, RoomKind.LivingGeneric, RoomKind.Kitchen, RoomKind.KitchenDining];

    public static IReadOnlyList<NormRule> Ru { get; } =
    [
        new("evac_exit_width", Sp1, "п. 4.2.19", "door on an evacuation path", "clear width", true, 800, "mm",
            "Ширина эвакуационных выходов в свету — не менее 0,8 м") { Check = "door_clear_width" },
        new("evac_exit_height", Sp1, "п. 4.2.18", "door on an evacuation path", "clear height", true, 1900, "mm",
            "Высота эвакуационных выходов в свету — не менее 1,9 м") { Check = "door_clear_height" },
        new("corridor_width", Sp1, "п. 4.3.3", "corridor / horizontal evacuation path", "width", true, 1000, "mm",
            "Ширина горизонтальных участков путей эвакуации — не менее 1,0 м (1,2 м при эвакуации более 50 человек)") { Check = "corridor_width", Kinds = [RoomKind.Corridor] },
        new("stair_run_width", Sp1, "п. 4.4.1", "evacuation stair", "run width", true, 1200, "mm",
            "Ширина марша — не менее 1,2 м (1,05 м — Ф1.3 высотой до 75 м; 1,35 м — Ф1.1, Ф2.1, Ф2.2, Ф3.4, Ф4.1)") { Check = "stair_run_width" },
        new("stair_riser_max", Sp1, "п. 4.4.3", "evacuation stair", "riser height", false, 220, "mm",
            "Высота ступени — не более 22 см") { Check = "stair_riser" },
        new("stair_riser_min", Sp1, "п. 4.4.3", "evacuation stair", "riser height", true, 50, "mm",
            "Высота ступени — не менее 5 см") { Check = "stair_riser" },
        new("stair_tread_min", Sp1, "п. 4.4.3", "evacuation stair", "tread depth", true, 250, "mm",
            "Ширина проступи — не менее 25 см") { Check = "stair_tread" },
        new("stair_slope_max", Sp1, "п. 4.4.3", "evacuation stair", "riser / tread", false, 1.0, "ratio",
            "Уклон лестниц на путях эвакуации — не более 1:1") { Check = "stair_slope" },
        new("ceiling_height_living", Sp54, "п. 5.12", "living room or kitchen", "floor-to-ceiling height", true, 2500, "mm",
            "Высота жилых комнат и кухни — не менее 2,5 м (2,7 м в подрайонах IA, IБ, IГ, IД, IVА)") { Check = "room_height", Kinds = LivingAndKitchen },
        new("ceiling_height_circulation", Sp54, "п. 5.12", "apartment hall / corridor", "floor-to-ceiling height", true, 2100, "mm",
            "Высота внутриквартирных коридоров, холлов, передних — не менее 2,1 м") { Check = "room_height", Kinds = [RoomKind.ApartmentHall, RoomKind.ApartmentCorridor] },
        new("area_common_living", Sp54, "п. 5.11", "common living room", "area", true, 16, "m²",
            "Общая жилая комната — не менее 16 м² (14 м² в однокомнатной квартире)") { Check = "room_area", Kinds = [RoomKind.CommonLiving] },
        new("area_bedroom", Sp54, "п. 5.11", "bedroom", "area", true, 8, "m²",
            "Спальня — не менее 8 м² (10 м² на двух человек)") { Check = "room_area", Kinds = [RoomKind.Bedroom, RoomKind.LivingGeneric] },
        new("area_kitchen", Sp54, "п. 5.11", "kitchen", "area", true, 8, "m²",
            "Кухня — не менее 8 м² (5 м² в однокомнатной квартире)") { Check = "room_area", Kinds = [RoomKind.Kitchen] },
        new("railing_height_exterior", Sp54, "п. 6.4.4", "balcony, loggia or exterior stair railing", "height", true, 1200, "mm",
            "Высота ограждений балконов, лоджий, наружных лестниц — не менее 1,2 м") { Check = "railing_height_other" },
        new("railing_height_interior", Sp54, "п. 6.4.5", "interior stair railing", "height", true, 900, "mm",
            "Высота ограждений внутренних лестниц — не менее 0,9 м") { Check = "railing_height_stair" },
        // Accessibility (СП 59.13330.2020) — run with sections ["sp59"].
        new("mgn_door_width", Sp59, "п. 6.1.5", "door accessible to wheelchair users", "clear width", true, 900, "mm",
            "Дверные проёмы, доступные для инвалидов на креслах-колясках, — ширина в свету не менее 0,9 м") { Check = "door_clear_width", Section = "sp59" },
        new("mgn_corridor_width", Sp59, "п. 6.2.1", "path of movement (corridor, gallery)", "width", true, 1800, "mm",
            "Ширина путей движения в коридорах, галереях — не менее 1,8 м (1,5–1,2 м с карманами для разъезда)") { Check = "corridor_width", Kinds = [RoomKind.Corridor, RoomKind.Lobby], Section = "sp59" },
        new("mgn_wc_width", Sp59, "разд. 6.3", "universal toilet cabin", "width", true, 2200, "mm",
            "Универсальная кабина уборной — не менее 2,2 × 2,25 м") { Check = "room_width", NamePattern = "универсальн|мгн|инвалид|accessible", Section = "sp59" },
        new("mgn_wc_length", Sp59, "разд. 6.3", "universal toilet cabin", "length", true, 2250, "mm",
            "Универсальная кабина уборной — не менее 2,2 × 2,25 м") { Check = "room_length", NamePattern = "универсальн|мгн|инвалид|accessible", Section = "sp59" },
        // Fire distances between buildings (СП 4.13130.2013 табл. 1) — run with sections ["fire_distance"].
        new("fire_distance", Sp4, "п. 4.3, табл. 1", "distance between residential/public buildings", "distance", true, 6000, "mm",
            "Противопожарные расстояния между жилыми и общественными зданиями — 6…15 м по степени огнестойкости и классу конструктивной пожарной опасности") { Check = "building_distance", Section = "fire_distance" },
    ];

    public static NormRule Get(string id) => Get(Ru, id);

    public static NormRule Get(IReadOnlyList<NormRule> rules, string id) =>
        rules.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException($"Unknown norm rule '{id}'. Known: {string.Join(", ", rules.Select(r => r.Id))}.");

    // Office rule file (JSON, editable without a new build):
    // { "overrides": {"corridor_width": 1400}, "disable": ["area_kitchen"],
    //   "rules": [ {"id": "office_corridor", "document": "Стандарт бюро", "clause": "п. 3.1", "requirement": "…",
    //               "check": "corridor_width", "at_least": true, "value": 1500, "unit": "mm",
    //               "kinds": ["Corridor"], "name_pattern": "коридор", "door_scope": "all", "section": "office"} ] }
    // A rule with an existing id replaces the built-in one. Unknown checks or kinds are errors.
    public static NormCatalog Load(string? json, string? source = null)
    {
        var rules = Ru.ToList();
        var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return new(rules, overrides, disabled, null);
        using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.TryGetProperty("rules", out var list))
            foreach (var r in list.EnumerateArray())
            {
                string S(string k, string d = "") => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? d : d;
                var id = S("id"); var check = S("check");
                if (id.Length == 0) throw new ArgumentException("Every office rule needs an id.");
                if (!Checks.Contains(check)) throw new ArgumentException($"Rule '{id}': check must be one of {string.Join(", ", Checks)}.");
                if (!r.TryGetProperty("value", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.Number) throw new ArgumentException($"Rule '{id}' needs a numeric value.");
                RoomKind[]? kinds = null;
                if (r.TryGetProperty("kinds", out var ks))
                    kinds = ks.EnumerateArray().Select(k => Enum.TryParse<RoomKind>(k.GetString(), true, out var kind) ? kind : throw new ArgumentException($"Rule '{id}': unknown room kind '{k.GetString()}'. Known: {string.Join(", ", Enum.GetNames<RoomKind>())}.")).ToArray();
                var pattern = S("name_pattern");
                if (pattern.Length > 0) _ = new System.Text.RegularExpressions.Regex(pattern);
                if (check.StartsWith("room_") || check == "corridor_width")
                    if (kinds == null && pattern.Length == 0) throw new ArgumentException($"Rule '{id}': a room check needs kinds or name_pattern.");
                var rule = new NormRule(id, S("document", "Стандарт организации"), S("clause"), S("subject", check), S("measure", check),
                    !r.TryGetProperty("at_least", out var al) || al.ValueKind != System.Text.Json.JsonValueKind.False, value.GetDouble(), S("unit", "mm"), S("requirement"))
                { Check = check, Kinds = kinds, NamePattern = pattern.Length > 0 ? pattern : null, DoorScope = S("door_scope", "evacuation"), Section = S("section", "office") };
                rules.RemoveAll(x => x.Id == id);
                rules.Add(rule);
            }
        if (root.TryGetProperty("overrides", out var ov))
            foreach (var p in ov.EnumerateObject()) { Get(rules, p.Name); overrides[p.Name] = p.Value.GetDouble(); }
        if (root.TryGetProperty("disable", out var dis))
            foreach (var d in dis.EnumerateArray()) { var id = d.GetString() ?? ""; Get(rules, id); disabled.Add(id); }
        return new(rules, overrides, disabled, source);
    }

    // Fire-distance group of a building for СП 4.13130.2013 table 1 (residential/public):
    // 1 = I, II, III degree C0; 2 = II, III C1 and IV C0, C1; 3 = IV C2, C3 and V.
    public static int FireGroup(string degreeAndClass)
    {
        var s = (degreeAndClass ?? "").ToUpperInvariant().Replace('С', 'C').Replace(" ", "").Replace(",", "");
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(V|IV|III|II|I)(C[0-3])?$");
        if (!m.Success) throw new ArgumentException($"'{degreeAndClass}' is not a fire resistance degree with class, e.g. 'II C0'.");
        var degree = m.Groups[1].Value; var cls = m.Groups[2].Success ? m.Groups[2].Value : "C0";
        if (degree == "V" || degree == "IV" && cls is "C2" or "C3") return 3;
        if (degree is "I" or "II" or "III" && cls == "C0") return 1;
        if (degree is "II" or "III" && cls == "C1" || degree == "IV" && cls is "C0" or "C1") return 2;
        throw new ArgumentException($"'{degreeAndClass}' is outside table 1 (e.g. I C1 or II C2); give the distance as an override.");
    }

    private static readonly double[,] FireTable = { { 6, 8, 10 }, { 8, 10, 12 }, { 10, 12, 15 } };
    public static double FireDistanceMm(string a, string b) => FireTable[FireGroup(a) - 1, FireGroup(b) - 1] * 1000;

    // Shortest distance between two convex footprints (polygons, mm); 0 if they overlap.
    public static double FootprintDistance(IReadOnlyList<(double X, double Y)> a, IReadOnlyList<(double X, double Y)> b)
    {
        if (Inside(a, b[0]) || Inside(b, a[0])) return 0;
        double best = double.MaxValue;
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
            {
                best = Math.Min(best, SegDist(a[i], a[(i + 1) % a.Count], b[j]));
                best = Math.Min(best, SegDist(b[j], b[(j + 1) % b.Count], a[i]));
            }
        return best;
    }

    private static double SegDist((double X, double Y) p, (double X, double Y) q, (double X, double Y) x)
    {
        double dx = q.X - p.X, dy = q.Y - p.Y, l2 = dx * dx + dy * dy;
        var t = l2 > 0 ? Math.Clamp(((x.X - p.X) * dx + (x.Y - p.Y) * dy) / l2, 0, 1) : 0;
        double ex = p.X + t * dx - x.X, ey = p.Y + t * dy - x.Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static bool Inside(IReadOnlyList<(double X, double Y)> poly, (double X, double Y) pt)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > pt.Y) != (poly[j].Y > pt.Y) && pt.X < (poly[j].X - poly[i].X) * (pt.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    // Convex hull (monotone chain) of footprint points; conservative for L/U-shaped buildings —
    // the hull is never farther than the real outline, so a distance that passes really passes.
    public static List<(double X, double Y)> Hull(IEnumerable<(double X, double Y)> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<(double X, double Y)>(); var upper = new List<(double X, double Y)>();
        foreach (var p in pts) { while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(p); }
        for (int i = pts.Count - 1; i >= 0; i--) { var p = pts[i]; while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(p); }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
        return lower.Concat(upper).ToList();
    }

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
