using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

// Picks (or, when allowed, creates) a type of the right size: family types by their size
// parameters (columns, beams), host types (walls, floors) by total thickness. Shared by the CAD
// and IFC import tools; created types and size compromises are reported, never hidden.
internal sealed class SizedTypes(Document doc, bool create)
{
    private readonly Dictionary<string, ElementType> _cache = new();
    public List<string> Created { get; } = new();
    public List<string> Mismatches { get; } = new();

    public static readonly string[] WidthNames = ["b", "Ширина", "ADSK_Размер_Ширина", "Width"];
    public static readonly string[] DepthNames = ["h", "Высота", "ADSK_Размер_Высота", "Глубина", "Depth", "Height"];
    public static readonly string[] DiameterNames = ["d", "Диаметр", "ADSK_Размер_Диаметр", "Diameter"];

    private static double? Get(Element s, string[] names) =>
        names.Select(n => s.LookupParameter(n)).FirstOrDefault(p => p is { StorageType: StorageType.Double })?.AsDouble() * Units.MmPerFoot;

    public FamilySymbol Family(FamilySymbol[] symbols, string[] wNames, double wMm, string[]? dNames, double dMm)
    {
        var key = $"F|{symbols[0].Family.Id.Value}|{wMm:0}|{dMm:0}";
        if (_cache.TryGetValue(key, out var cached)) return (FamilySymbol)cached;
        var exact = symbols.FirstOrDefault(s => Get(s, wNames) is { } w && Math.Abs(w - wMm) < 1 && (dNames == null || Get(s, dNames) is { } d && Math.Abs(d - dMm) < 1));
        if (exact == null && create)
        {
            var name = dNames == null ? $"Ø{wMm:0}" : $"{wMm:0}x{dMm:0}";
            exact = (FamilySymbol)symbols[0].Duplicate(symbols.Any(s => s.Name == name) ? name + " (импорт)" : name);
            Set(exact, wNames, wMm);
            if (dNames != null) Set(exact, dNames, dMm);
            Created.Add(exact.Family.Name + " : " + exact.Name);
        }
        if (exact == null)
        {
            exact = symbols.OrderBy(s => Math.Abs((Get(s, wNames) ?? 0) - wMm) + (dNames == null ? 0 : Math.Abs((Get(s, dNames) ?? 0) - dMm))).First();
            Mismatches.Add($"{wMm:0}{(dNames == null ? "" : "×" + dMm.ToString("0"))} → {exact.Name}");
        }
        if (!exact.IsActive) exact.Activate();
        _cache[key] = exact;
        return exact;
    }

    private static void Set(Element type, string[] names, double mm)
    {
        var p = names.Select(n => type.LookupParameter(n)).FirstOrDefault(x => x is { StorageType: StorageType.Double, IsReadOnly: false })
            ?? throw new ToolInputException($"Type {type.Name} has no writable size parameter among {string.Join(", ", names)}; give size_parameters.");
        p.Set(mm / Units.MmPerFoot);
    }

    // Wall or floor type of a total thickness: an existing one within 1 mm, else (create) a copy of
    // the nearest with its core layer resized, else the nearest (reported).
    public T Host<T>(IEnumerable<T> types, double thicknessMm, Func<T, double> width) where T : HostObjAttributes
    {
        var list = types.ToList();
        if (list.Count == 0) throw new ToolInputException($"The project has no {typeof(T).Name} to use.");
        var key = $"H|{typeof(T).Name}|{thicknessMm:0}";
        if (_cache.TryGetValue(key, out var cached)) return (T)cached;
        var nearest = list.OrderBy(t => Math.Abs(width(t) * Units.MmPerFoot - thicknessMm)).First();
        T result = nearest;
        if (Math.Abs(width(nearest) * Units.MmPerFoot - thicknessMm) >= 1)
        {
            var cs = nearest.GetCompoundStructure();
            if (create && cs != null)
            {
                var core = Math.Max(0, cs.GetFirstCoreLayerIndex());
                var others = cs.GetLayers().Where((_, i) => i != core).Sum(l => l.Width);
                var coreWidth = thicknessMm / Units.MmPerFoot - others;
                if (coreWidth > 1 / Units.MmPerFoot)
                {
                    var name = $"{nearest.Name} {thicknessMm:0}";
                    var copy = (T)nearest.Duplicate(list.Any(t => t.Name == name) ? name + " (импорт)" : name);
                    cs.SetLayerWidth(core, coreWidth);
                    copy.SetCompoundStructure(cs);
                    Created.Add(copy.Name);
                    result = copy;
                }
                else Mismatches.Add($"{thicknessMm:0} мм → {nearest.Name} (other layers are thicker than the target)");
            }
            else Mismatches.Add($"{thicknessMm:0} мм → {nearest.Name} ({width(nearest) * Units.MmPerFoot:0} мм)");
        }
        _cache[key] = result;
        return result;
    }
}
