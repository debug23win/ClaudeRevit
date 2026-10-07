using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeRevit.Services;

// Pure structural rules behind the reinforcement, load and documentation tools: stirrup zone
// layout, СП 20.13330.2016 load combinations, ГОСТ 23279 mesh designations and the bar bending
// schedule's parameter profiles. No Revit types, so every number here is unit-tested.

public sealed record StirrupZone(double? LengthMm, double SpacingMm);
public sealed record StirrupRun(int Zone, double StartMm, int Count, double SpacingMm)
{
    public double EndMm => StartMm + (Count - 1) * SpacingMm;
}

public static class StirrupZones
{
    // Zones along a member between `startOffset` and `length - endOffset` (stirrup centre lines).
    // Fixed-length zones before the single open zone (LengthMm = null) are anchored at the start,
    // those after it at the end — so support zones get exactly the spacing asked for — and the open
    // zone fills the gap with an even spacing no larger than requested. Without an open zone the
    // zones run forward from the start. Adjacent zones never share or duplicate a stirrup.
    public static List<StirrupRun> Layout(double length, IReadOnlyList<StirrupZone> zones, double startOffset, double endOffset)
    {
        if (zones.Count == 0) throw new ArgumentException("Give at least one zone.");
        if (zones.Any(z => !(z.SpacingMm > 0))) throw new ArgumentException("Every zone needs a positive spacing.");
        if (zones.Any(z => z.LengthMm is <= 0)) throw new ArgumentException("Zone lengths must be positive.");
        var open = zones.Select((z, i) => (z, i)).Where(x => x.z.LengthMm == null).Select(x => x.i).ToList();
        if (open.Count > 1) throw new ArgumentException("Only one zone may omit its length (it takes the remainder).");
        double a = startOffset, b = length - endOffset;
        if (b <= a) throw new ArgumentException("The member is shorter than its end offsets.");
        var fixedSum = zones.Where(z => z.LengthMm != null).Sum(z => z.LengthMm!.Value);
        if (fixedSum > b - a + 1e-6) throw new ArgumentException($"Zones total {fixedSum:0} mm but only {b - a:0} mm is available.");
        var runs = new List<StirrupRun>();
        const double eps = 1e-6;
        if (open.Count == 0)
        {
            double cursor = a, zoneStart = a;
            for (int i = 0; i < zones.Count; i++)
            {
                var zoneEnd = zoneStart + zones[i].LengthMm!.Value;
                var s = zones[i].SpacingMm;
                if (cursor <= zoneEnd + eps)
                {
                    var n = (int)Math.Floor((zoneEnd - cursor) / s + eps) + 1;
                    runs.Add(new(i, cursor, n, s));
                    cursor += n * s;
                }
                zoneStart = zoneEnd;
                if (i + 1 < zones.Count) cursor = Math.Max(cursor - s + zones[i + 1].SpacingMm, zoneStart);
            }
            return runs;
        }
        int m = open[0];
        // Forward from the start.
        double fwd = a; double lastFwd = double.NaN; double zs = a;
        for (int i = 0; i < m; i++)
        {
            var ze = zs + zones[i].LengthMm!.Value; var s = zones[i].SpacingMm;
            var start = double.IsNaN(lastFwd) ? a : lastFwd + s;
            if (start <= ze + eps)
            {
                var n = (int)Math.Floor((ze - start) / s + eps) + 1;
                runs.Add(new(i, start, n, s)); lastFwd = start + (n - 1) * s;
            }
            zs = ze;
        }
        // Backward from the end.
        var back = new List<StirrupRun>(); double lastBack = double.NaN; double be = b;
        for (int i = zones.Count - 1; i > m; i--)
        {
            var bs = be - zones[i].LengthMm!.Value; var s = zones[i].SpacingMm;
            var end = double.IsNaN(lastBack) ? b : lastBack - s;
            if (end >= bs - eps)
            {
                var n = (int)Math.Floor((end - bs) / s + eps) + 1;
                var start = end - (n - 1) * s;
                back.Add(new(i, start, n, s)); lastBack = start;
            }
            be = bs;
        }
        // The open zone between the last forward and first backward stirrup.
        var left = double.IsNaN(lastFwd) ? a - zones[m].SpacingMm : lastFwd;
        var right = double.IsNaN(lastBack) ? b + zones[m].SpacingMm : lastBack;
        bool includeLeft = double.IsNaN(lastFwd), includeRight = double.IsNaN(lastBack);
        if (includeLeft) left = a; if (includeRight) right = b;
        var gap = right - left;
        if (gap > eps)
        {
            var target = zones[m].SpacingMm;
            var intervals = Math.Max(1, (int)Math.Ceiling(gap / target - eps));
            var spacing = gap / intervals;
            var first = includeLeft ? left : left + spacing;
            var last = includeRight ? right : right - spacing;
            var n = (int)Math.Round((last - first) / spacing) + 1;
            if (n > 0) runs.Add(new(m, first, n, spacing));
        }
        back.Reverse();
        runs.AddRange(back);
        return runs;
    }
}

public enum LoadKind { Dead, Long, Short }
public sealed record LoadCaseSpec(string Name, LoadKind Kind, double GammaF);
public sealed record CombinationSpec(string Name, bool Ultimate, List<(string Case, double Factor)> Components);

public static class LoadCombinations
{
    // Load factors γf by load nature (СП 20.13330.2016): dead 1.1 (reinforced concrete;
    // steel 1.05, site-made finishes 1.3 — set per case), floor live 1.2 (1.3 below 2 kPa),
    // snow and wind 1.4, temperature 1.1. Every value is overridable per case.
    public static double DefaultGamma(string category) => category.ToLowerInvariant() switch
    {
        "dead" => 1.1, "live" => 1.2, "rooflive" => 1.3, "snow" => 1.4, "wind" => 1.4, "temperature" => 1.1, _ => 1.2
    };

    // Basic combinations per СП 20.13330.2016 п. 6.4 (formula 6.1):
    // C = ΣPd + ψl1·Pl1 + ψl2·(Pl2…) + ψt1·Pt1 + ψt2·Pt2 + ψt3·(Pt3…), ψl1 = 1, ψl2 = 0.95,
    // ψt1 = 1, ψt2 = 0.9, ψt3 = 0.7. Every ordering of the leading long load and the two leading
    // short loads is generated. Ultimate combinations carry γf; serviceability ones are normative
    // (γf = 1). Special (accidental, seismic) combinations are not generated.
    public static List<CombinationSpec> Basic(IReadOnlyList<LoadCaseSpec> cases, bool ultimate, string prefix, int max = 200)
    {
        var dead = cases.Where(c => c.Kind == LoadKind.Dead).ToList();
        var longs = cases.Where(c => c.Kind == LoadKind.Long).ToList();
        var shorts = cases.Where(c => c.Kind == LoadKind.Short).ToList();
        if (dead.Count + longs.Count + shorts.Count == 0) throw new ArgumentException("No load cases to combine.");
        double G(LoadCaseSpec c) => ultimate ? c.GammaF : 1.0;
        var longOrders = longs.Count == 0 ? new List<LoadCaseSpec?> { null } : longs.Select(l => (LoadCaseSpec?)l).ToList();
        var shortPairs = new List<(LoadCaseSpec? T1, LoadCaseSpec? T2)>();
        if (shorts.Count == 0) shortPairs.Add((null, null));
        else if (shorts.Count == 1) shortPairs.Add((shorts[0], null));
        else foreach (var t1 in shorts) foreach (var t2 in shorts.Where(s => s != t1)) shortPairs.Add((t1, t2));
        var result = new List<CombinationSpec>();
        foreach (var l1 in longOrders)
            foreach (var (t1, t2) in shortPairs)
            {
                var comps = dead.Select(d => (d.Name, Math.Round(G(d), 4))).ToList();
                foreach (var l in longs) comps.Add((l.Name, Math.Round(G(l) * (l == l1 ? 1.0 : 0.95), 4)));
                foreach (var t in shorts) comps.Add((t.Name, Math.Round(G(t) * (t == t1 ? 1.0 : t == t2 ? 0.9 : 0.7), 4)));
                var tag = string.Join(", ", new[] { l1?.Name, t1?.Name, t2?.Name }.Where(x => x != null));
                result.Add(new($"{prefix} {result.Count + 1}" + (tag.Length > 0 ? $" ({tag})" : ""), ultimate, comps));
                if (result.Count > max) throw new ArgumentException($"More than {max} combinations; reduce the number of short-term cases or give combinations explicitly.");
            }
        return result;
    }

    // Classifies a Revit load case by its category/nature name (English or Russian).
    public static LoadKind KindOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("dead") || n.Contains("постоян") || n.Contains("собствен")) return LoadKind.Dead;
        if (n.Contains("длитель") || n.Contains("long")) return LoadKind.Long;
        return LoadKind.Short;
    }
}

// ГОСТ 23279-2012 welded mesh designation, e.g. "4С 5Вр1-100/5Вр1-100 230×500 25/25":
// longitudinal wire Ø-class-spacing / transverse wire Ø-class-spacing, width × length in cm,
// optional overhangs (longitudinal/transverse) in mm.
public sealed record MeshDesignation(double LongDiameterMm, string LongClass, double LongSpacingMm,
    double CrossDiameterMm, string CrossClass, double CrossSpacingMm, double? WidthMm, double? LengthMm, double? Overhang1Mm, double? Overhang2Mm);

public static class MeshDesignations
{
    private static readonly Regex Pattern = new(
        @"^\s*(?:\d+\s*С\s+)?(?<d1>\d+(?:[.,]\d+)?)\s*(?<c1>[A-Za-zА-Яа-я]+\d*[A-Za-zА-Яа-я]?)\s*-\s*(?<s1>\d+)\s*/\s*(?<d2>\d+(?:[.,]\d+)?)\s*(?<c2>[A-Za-zА-Яа-я]+\d*[A-Za-zА-Яа-я]?)\s*-\s*(?<s2>\d+)" +
        @"(?:\s+(?<w>\d+)\s*[x×х]\s*(?<l>\d+))?(?:\s+(?<o1>\d+)\s*/\s*(?<o2>\d+))?\s*$", RegexOptions.Compiled);

    public static MeshDesignation Parse(string text)
    {
        var m = Pattern.Match(text ?? "");
        if (!m.Success) throw new ArgumentException($"'{text}' is not a ГОСТ 23279 mesh designation like '4С 5Вр1-100/5Вр1-100 230×500 25/25'.");
        double D(string g) => double.Parse(m.Groups[g].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        double? O(string g) => m.Groups[g].Success ? D(g) : null;
        return new(D("d1"), m.Groups["c1"].Value, D("s1"), D("d2"), m.Groups["c2"].Value, D("s2"),
            m.Groups["w"].Success ? D("w") * 10 : null, m.Groups["l"].Success ? D("l") * 10 : null, O("o1"), O("o2"));
    }
}

// Parameter profiles for the ГОСТ 21.501 bar bending schedule ("ведомость деталей"). Each field
// lists candidates in preference order; "builtin:" names a Revit built-in parameter.
public static class BendingScheduleProfiles
{
    public static readonly string[] Fields = ["position", "construction", "diameter", "length"];

    public static IReadOnlyDictionary<string, string[]> Get(string profile) => profile switch
    {
        "native" => new Dictionary<string, string[]>
        {
            ["position"] = ["builtin:REBAR_NUMBER"], ["construction"] = ["builtin:NUMBER_PARTITION_PARAM"],
            ["diameter"] = ["builtin:REBAR_BAR_DIAMETER"], ["length"] = ["builtin:REBAR_ELEM_LENGTH"]
        },
        "adsk" => new Dictionary<string, string[]>
        {
            ["position"] = ["ADSK_Позиция", "builtin:REBAR_NUMBER"], ["construction"] = ["ADSK_Марка конструкции", "ADSK_Марка изделия", "builtin:NUMBER_PARTITION_PARAM"],
            ["diameter"] = ["ADSK_Диаметр арматуры", "builtin:REBAR_BAR_DIAMETER"], ["length"] = ["ADSK_Длина арматуры", "ADSK_Длина арматуры по детали", "builtin:REBAR_ELEM_LENGTH"]
        },
        "bimstarter" => new Dictionary<string, string[]>
        {
            ["position"] = ["builtin:REBAR_NUMBER"], ["construction"] = ["Мрк.МаркаКонструкции", "builtin:NUMBER_PARTITION_PARAM"],
            ["diameter"] = ["Мрк.ПозАрмДиаметр", "Рзм.Диаметр", "builtin:REBAR_BAR_DIAMETER"], ["length"] = ["Мрк.ПозАрмДлина", "Рзм.Длина", "builtin:REBAR_ELEM_LENGTH"]
        },
        _ => throw new ArgumentException("profile must be auto, native, adsk, bimstarter or custom (with field_mapping).")
    };

    // Auto-detection: the profile whose distinctive (non built-in) parameters the model has most of.
    public static string Detect(Func<string, bool> modelHasParameter)
    {
        int Score(string p) => Get(p).Values.SelectMany(v => v).Where(n => !n.StartsWith("builtin:")).Distinct().Count(modelHasParameter);
        var adsk = Score("adsk"); var bs = Score("bimstarter");
        return adsk == 0 && bs == 0 ? "native" : adsk >= bs ? "adsk" : "bimstarter";
    }

    // Vertical centres of the data rows of a placed schedule, from the top of the instance down:
    // header rows first, then the data rows. Heights in any consistent unit.
    public static List<double> RowCentres(double top, IReadOnlyList<double> headerHeights, IReadOnlyList<double> bodyHeights, int firstDataRow)
    {
        var y = top - headerHeights.Sum();
        var centres = new List<double>();
        for (int i = 0; i < bodyHeights.Count; i++)
        {
            if (i >= firstDataRow) centres.Add(y - bodyHeights[i] / 2);
            y -= bodyHeights[i];
        }
        return centres;
    }
}
