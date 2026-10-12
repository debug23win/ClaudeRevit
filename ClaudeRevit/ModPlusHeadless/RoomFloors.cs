#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace ModPlus.Headless;

internal static class RoomFloors
{
    internal static object Create(Document doc, JsonElement args)
    {
        long[] ids = args.GetProperty("element_ids").EnumerateArray().Select(i => i.GetInt64()).Distinct().ToArray();
        if (ids.Length < 1 || ids.Length > 1000) throw new ArgumentException("Supply 1..1000 room IDs.");
        FloorType type = doc.GetElement(new ElementId(args.GetProperty("floor_type_id").GetInt64())) as FloorType ?? throw new ArgumentException("Supply a FloorType ID.");
        double offset = args.TryGetProperty("offset_mm", out JsonElement o) ? o.GetDouble() / 304.8 : 0;
        if (!double.IsFinite(offset)) throw new ArgumentException("offset_mm must be finite.");
        string boundaryName = args.TryGetProperty("boundary", out JsonElement b) ? b.GetString() : "Finish";
        SpatialElementBoundaryLocation boundary = Enum.Parse<SpatialElementBoundaryLocation>(boundaryName, false);
        if (!Enum.IsDefined(boundary)) throw new ArgumentException("Unknown room boundary location.");
        bool structural = args.TryGetProperty("structural", out JsonElement s) && s.GetBoolean();
        using var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = boundary };
        var result = new List<object>();
        foreach (long id in ids)
        {
            Room room = doc.GetElement(new ElementId(id)) as Room ?? throw new ArgumentException("Element " + id + " is not a Room.");
            if (room.Area <= 0 || doc.GetElement(room.LevelId) is not Level) throw new ArgumentException("Room " + id + " must be placed and enclosed on a valid level.");
            IList<IList<BoundarySegment>> boundaries = room.GetBoundarySegments(options);
            if (boundaries == null || boundaries.Count == 0) throw new ArgumentException("Room " + id + " has no boundary loops.");
            List<CurveLoop> loops = boundaries.Select(segments => CurveLoop.Create(segments.Select(segment => segment.GetCurve()).ToList())).ToList();
            try
            {
                Floor floor = Floor.Create(doc, loops, type.Id, room.LevelId);
                Parameter height = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (height == null || height.IsReadOnly || !height.AsDouble().Equals(offset) && !height.Set(offset)) throw new ArgumentException("Cannot set the floor height offset.");
                Parameter structuralParameter = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                if (structuralParameter == null || structuralParameter.IsReadOnly || structuralParameter.AsInteger() != (structural ? 1 : 0) && !structuralParameter.Set(structural ? 1 : 0)) throw new ArgumentException("Cannot set the floor structural flag.");
                result.Add(new { roomId = room.Id.Value, floorId = floor.Id.Value, levelId = room.LevelId.Value });
            }
            finally { foreach (CurveLoop loop in loops) loop.Dispose(); }
        }
        return new { rows = result, note = "One new floor per room on the room's level. Offset is relative to that level. Existing floors are not replaced." };
    }
}
