using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace ClaudeRevit.Services;

public static class SelectionService
{
    public sealed class SelectionInfo
    {
        public IReadOnlyList<long> Ids { get; init; } = Array.Empty<long>();
        public IReadOnlyDictionary<string, int> CategoryCounts { get; init; } =
            new Dictionary<string, int>();
        public string Description { get; init; } = "";
    }

    private static readonly SelectionInfo Empty = new();
    public static SelectionInfo Current { get; private set; } = Empty;
    public static event Action<SelectionInfo>? Changed;

    private static HashSet<long> _lastIds = new();

    // Idling fires many times a second; polling the selection (and allocating a set) on
    // every tick is needless UI-thread work that makes Revit feel sluggish during all
    // interaction, including opening a document. Sample at most ~4×/second — imperceptible
    // for the selection pill, but it removes almost all of the per-idle cost.
    private static long _lastCheckTick;
    private const long ThrottleMs = 250;

    public static void Initialize(UIControlledApplication app)
    {
        app.Idling += OnIdling;
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication uiApp) return;

        var now = Environment.TickCount64;
        if (now - _lastCheckTick < ThrottleMs) return;
        _lastCheckTick = now;

        var uidoc = uiApp.ActiveUIDocument;
        if (uidoc == null)
        {
            if (_lastIds.Count > 0) { _lastIds = new HashSet<long>(); PublishEmpty(); }
            return;
        }

        var ids = uidoc.Selection.GetElementIds();

        // Fast path: same count AND same membership → nothing changed, and we did it without
        // allocating a new set (the common case, since the selection rarely changes between
        // ticks). Only when it genuinely differs do we rebuild and do the category work.
        if (ids.Count == _lastIds.Count)
        {
            var unchanged = true;
            foreach (var id in ids)
                if (!_lastIds.Contains(id.Value)) { unchanged = false; break; }
            if (unchanged) return;
        }

        if (ids.Count == 0)
        {
            _lastIds = new HashSet<long>();
            PublishEmpty();
            return;
        }

        var newIds = new HashSet<long>(ids.Count);
        foreach (var id in ids) newIds.Add(id.Value);
        _lastIds = newIds;

        // A per-element GetElement loop for a huge selection (Select All can be tens of
        // thousands) would freeze the UI thread. Above this size, skip the category
        // breakdown and just report the count — the assistant only ever shows the first
        // handful of ids anyway.
        const int CategoryScanCap = 2000;
        string description;
        Dictionary<string, int> byCategory;
        if (ids.Count > CategoryScanCap)
        {
            byCategory = new Dictionary<string, int>();
            description = $"{newIds.Count} elements";
        }
        else
        {
            var doc = uidoc.Document;
            byCategory = new Dictionary<string, int>();
            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                var cat = el?.Category?.Name ?? "(no category)";
                byCategory[cat] = byCategory.GetValueOrDefault(cat) + 1;
            }
            description = byCategory.Count == 1
                ? $"{newIds.Count} {byCategory.Keys.First()}"
                : $"{newIds.Count} elements ({string.Join(", ", byCategory.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"))})";
        }

        Current = new SelectionInfo
        {
            Ids = newIds.ToList(),
            CategoryCounts = byCategory,
            Description = description
        };
        Changed?.Invoke(Current);
    }

    private static void PublishEmpty()
    {
        if (Current == Empty) return;
        Current = Empty;
        Changed?.Invoke(Current);
    }
}
