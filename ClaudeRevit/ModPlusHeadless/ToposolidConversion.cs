#nullable disable
// Floor ↔ toposolid conversion on the Revit API, without dialogs; reports are JSON.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ModPlus.Headless;

public sealed class ToposolidConversion
{
    private Document _doc;
    private ElementId _floorType;
    private Element _toposolidType;
    private bool _deleteSource;
    public double ShortCurveTolerance;
    private readonly List<long> created = new List<long>();
    private readonly List<object> messages = new List<object>();
    private sealed class Report
    {
        public readonly List<object> Items = new List<object>();
        public void Add(string message, string elementId, int unused) => Items.Add(new { message, elementId });
        public bool IsEmpty() => Items.Count == 0;
    }
    public string Run(UIApplication app, JsonElement args, bool preview)
    {
        if (app == null || app.Application.VersionNumber != "2027") throw new ArgumentException("Revit 2027 is required.");
        _doc = app.ActiveUIDocument?.Document ?? throw new ArgumentException("Open a document.");
        if (_doc.IsModifiable) throw new ArgumentException("Run without a host transaction.");
        created.Clear(); messages.Clear();
        _floorType = new ElementId(args.GetProperty("floor_type_id").GetInt64());
        _toposolidType = _doc.GetElement(new ElementId(args.GetProperty("toposolid_type_id").GetInt64()));
        if (_doc.GetElement(_floorType) is not FloorType || _toposolidType is not ToposolidType) throw new ArgumentException("Supply valid FloorType and ToposolidType IDs.");
        _deleteSource = args.TryGetProperty("delete_source", out JsonElement deletion) && deletion.GetBoolean();
        bool strict = !args.TryGetProperty("allow_partial", out JsonElement partial) || !partial.GetBoolean();
        ShortCurveTolerance = app.Application.ShortCurveTolerance;
        List<Element> elements = args.GetProperty("element_ids").EnumerateArray().Select(i => i.GetInt64()).Distinct().Select(i => _doc.GetElement(new ElementId(i))).ToList();
        if (elements.Count < 1 || elements.Count > 1000 || elements.Any(e => e is not Floor && e is not Toposolid)) throw new ArgumentException("Supply 1..1000 Floor or Toposolid IDs.");
        using var group = new TransactionGroup(_doc, "Claude: ModPlus / Toposolid conversion");
        using var failures = new NoDialogFailures(app.Application, _doc);
        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start group.");
        try
        {
            ConvertElements(elements.OfType<Floor>().ToList(), elements.OfType<Toposolid>().ToList());
            if (failures.HasErrors) throw new InvalidOperationException("Revit rejected the conversion. No changes were committed.");
            bool rejected = strict && messages.Count > 0;
            ClaudeRevit.Tools.ToolContext.ThrowIfCancelled();
            TransactionStatus final = preview || rejected ? group.RollBack() : group.Assimilate();
            if (final != (preview || rejected ? TransactionStatus.RolledBack : TransactionStatus.Committed)) throw new InvalidOperationException("Unexpected transaction group status.");
            return JsonSerializer.Serialize(new { ok = !rejected, preview, committed = !preview && !rejected, rolledBack = preview || rejected,
                createdElementIds = created, errors = messages, rejected, idsTemporary = preview || rejected, failures = failures.Messages });
        }
        catch { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
    }
    private static bool HasVolume(Solid solid)
    {
        try { return solid.Volume > 0.0; } catch { return false; }
    }
	private void ConvertElements(List<Floor> floors, List<Toposolid> toposolids)
	{
		BuiltInParameter specify = BuiltInParameter.SPECIFY_SLOPE_OR_OFFSET;
		BuiltInParameter floorHeightOffset = BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM;
		BuiltInParameter toposolidHeightOffset = BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM;
		Report resultService = new Report();
		List<ElementId> successfullyHandledElements = new List<ElementId>();
		using (Transaction transaction = new Transaction(_doc, "Claude: ModPlus / Toposolid conversion"))
		{
			if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start conversion transaction.");
            NoDialogFailures.Configure(transaction);
			floors.ForEach((Floor floor) =>
			{
				if (_doc.GetElement(floor.SketchId) is Sketch sketch)
				{
					List<CurveLoop> profiles = (from ca in sketch.Profile.OfType<CurveArray>()
						select CurveLoop.Create(ca.OfType<Curve>().ToList())).ToList();
					ModelLine modelLine = sketch.GetAllElements().Select(_doc.GetElement).OfType<ModelLine>()
						.FirstOrDefault((ModelLine c) => ((Element)c).get_Parameter(specify) != null);
					bool isWithSlopeLine = modelLine != null;
					List<XYZ> floorReliefPoints = GetFloorReliefPoints(floor, isWithSlopeLine);
					if (!IsEnoughToposolidTypeThickness(floor, floorHeightOffset, _toposolidType.Id, floorReliefPoints))
					{
						resultService.Add(string.Format("Toposolid type {0} is too thin for the relief.", _toposolidType.Name), floor.Id.ToString(), 0);
					}
					else
					{
						Toposolid toposolid = null;
						try
						{
							toposolid = Toposolid.Create(_doc, profiles, _toposolidType.Id, floor.LevelId);
						}
						catch (Autodesk.Revit.Exceptions.ArgumentException)
						{
							resultService.Add("Cannot create a toposolid from the floor profile.", floor.Id.ToString(), 0);
						}
						if (toposolid != null)
						{
							((Element)toposolid).get_Parameter(toposolidHeightOffset).Set(((Element)floor).get_Parameter(floorHeightOffset).AsDouble());
							SlabShapeEditor slabShapeEditor = toposolid.GetSlabShapeEditor();
							ApplyPoints(slabShapeEditor, floorReliefPoints);
                            created.Add(toposolid.Id.Value);
							successfullyHandledElements.Add(floor.Id);
						}
					}
				}
                else resultService.Add("The floor has no editable sketch profile.", floor.Id.ToString(), 0);
			});
			toposolids.ForEach((Toposolid toposolid) =>
			{
				if (_doc.GetElement(toposolid.SketchId) is Sketch sketch)
				{
					List<CurveLoop> profile = (from ca in sketch.Profile.OfType<CurveArray>()
						select CurveLoop.Create(ca.OfType<Curve>().ToList())).ToList();
					Floor floor = null;
					bool flag = toposolid.HostTopoId != ElementId.InvalidElementId;
                    bool createdForSource = false;
					ElementId levelId = (flag ? _doc.GetElement(toposolid.HostTopoId).LevelId : toposolid.LevelId);
					ModelLine modelLine = sketch.GetAllElements().Select(_doc.GetElement).OfType<ModelLine>()
						.FirstOrDefault((ModelLine c) => ((Element)c).get_Parameter(specify) != null);
					if (modelLine != null)
					{
						try
						{
							floor = Floor.Create(_doc, profile, _floorType, levelId, isStructural: false, (Line)modelLine.GeometryCurve, ((Element)modelLine).get_Parameter(BuiltInParameter.ROOF_SLOPE).AsDouble());
						}
						catch (Autodesk.Revit.Exceptions.ArgumentException)
						{
							resultService.Add("Cannot create a floor from the toposolid profile.", toposolid.Id.ToString(), 0);
						}
						if (floor == null)
						{
							return;
						}
						((Element)floor).get_Parameter(floorHeightOffset).Set(((Element)toposolid).get_Parameter(toposolidHeightOffset).AsDouble());
						Sketch sketch2 = (Sketch)_doc.GetElement(floor.SketchId);
						ModelLine modelLine2 = sketch2.GetAllElements().Select(_doc.GetElement).OfType<ModelLine>()
							.FirstOrDefault((ModelLine c) => ((Element)c).get_Parameter(specify) != null);
						if (modelLine2 != null)
						{
							((Element)modelLine2).get_Parameter(specify).Set(((Element)modelLine).get_Parameter(specify).AsInteger());
							((Element)modelLine2).get_Parameter(BuiltInParameter.SLOPE_START_HEIGHT).Set(((Element)modelLine).get_Parameter(BuiltInParameter.SLOPE_START_HEIGHT).AsDouble());
							Parameter parameter = ((Element)modelLine2).get_Parameter(BuiltInParameter.SLOPE_END_HEIGHT);
							if (!parameter.IsReadOnly)
							{
								parameter.Set(((Element)modelLine).get_Parameter(BuiltInParameter.SLOPE_END_HEIGHT).AsDouble());
							}
						}
					}
					else if (flag)
					{
						int before = created.Count;
                        BuildFloorFromSolidGeometry(toposolid, levelId, _floorType);
                        createdForSource = created.Count > before;
                        if (!createdForSource) resultService.Add("No closed floor profile could be built from the hosted toposolid geometry.", toposolid.Id.ToString(), 0);
					}
					else
					{
						try
						{
							floor = Floor.Create(_doc, profile, _floorType, levelId);
						}
						catch (Autodesk.Revit.Exceptions.ArgumentException)
						{
							resultService.Add("Cannot create a floor from the toposolid profile.", toposolid.Id.ToString(), 0);
						}
						if (floor == null)
						{
							return;
						}
						((Element)floor).get_Parameter(floorHeightOffset).Set(((Element)toposolid).get_Parameter(toposolidHeightOffset).AsDouble());
						List<XYZ> reliefPoints = (from ssv in toposolid.GetSlabShapeEditor().SlabShapeVertices.OfType<SlabShapeVertex>()
							select ssv.Position).ToList();
						SlabShapeEditor slabShapeEditor = floor.GetSlabShapeEditor();
						ApplyPoints(slabShapeEditor, reliefPoints);
					}
					if (floor != null) created.Add(floor.Id.Value);
                    if (floor != null || createdForSource) successfullyHandledElements.Add(toposolid.Id);
				}
                else resultService.Add("The toposolid has no editable sketch profile.", toposolid.Id.ToString(), 0);
			});
			if (_deleteSource)
			{
				_doc.Delete(successfullyHandledElements);
			}
			if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Conversion transaction was rolled back.");
		}
		if (!resultService.IsEmpty())
		{
			messages.AddRange(resultService.Items);
		}
	}

	private bool IsEnoughToposolidTypeThickness(Floor floor, BuiltInParameter floorHeightOffset, ElementId toposolidType, List<XYZ> reliefPoints)
	{
		double floorElevation = ((Level)_doc.GetElement(floor.LevelId)).Elevation + ((Element)floor).get_Parameter(floorHeightOffset).AsDouble();
		double toposolidTypeThickness = _doc.GetElement(toposolidType).get_Parameter(BuiltInParameter.TOPOSOLID_TYPE_DEFAULT_THICKNESS_PARAM).AsDouble();
		return reliefPoints.All((XYZ point) => floorElevation - point.Z < toposolidTypeThickness);
	}

	private List<XYZ> GetFloorReliefPoints(Floor floor, bool isWithSlopeLine)
	{
		if (isWithSlopeLine)
		{
			List<XYZ> reliefPoints = new List<XYZ>();
			List<Solid> list = (from s in ((Element)floor).get_Geometry(new Options()).GetTransformed(Transform.Identity).OfType<Solid>()
				where HasVolume(s)
				select s).ToList();
			list.ForEach((Solid solid) =>
			{
				List<Face> source = solid.Faces.OfType<Face>().Where(LooksUp).ToList();
				List<Edge> source2 = source.SelectMany((Face f) => f.EdgeLoops.OfType<EdgeArray>().SelectMany((EdgeArray ea) => ea.OfType<Edge>())).ToList();
				List<Edge> source3 = (from e in (from e in source2
						group e by e into g
						where g.Count() == 1
						select g).SelectMany((IGrouping<Edge, Edge> g) => g)
					where e.ApproximateLength > ShortCurveTolerance
					select e).ToList();
				reliefPoints.AddRange(Enumerable.DistinctBy(source3.SelectMany((Edge e) => e.Tessellate()), (XYZ p) => (Math.Round(p.X, 3), Math.Round(p.Y, 3))));
			});
			return reliefPoints;
		}
		return (from ssv in floor.GetSlabShapeEditor().SlabShapeVertices.OfType<SlabShapeVertex>()
			select ssv.Position).ToList();
	}

	private void ApplyPoints(SlabShapeEditor slabShapeEditor, List<XYZ> reliefPoints)
	{
		if (reliefPoints.Count != 0)
		{
			if (!slabShapeEditor.IsEnabled)
			{
				slabShapeEditor.Enable();
			}
			_doc.Regenerate();
			slabShapeEditor.AddPoints(reliefPoints);
		}
	}

	private void BuildFloorFromSolidGeometry(Toposolid toposolid, ElementId levelId, ElementId floorType)
	{
		List<Solid> list = (from s in ((Element)toposolid).get_Geometry(new Options()).GetTransformed(Transform.Identity).OfType<Solid>()
			where HasVolume(s)
			select s).ToList();
		list.ForEach((Solid solid) =>
		{
			List<Face> source = solid.Faces.OfType<Face>().Where(LooksUp).ToList();
			List<Edge> list2 = source.SelectMany((Face f) => f.EdgeLoops.OfType<EdgeArray>().SelectMany((EdgeArray ea) => ea.OfType<Edge>())).ToList();
			List<Edge> list3 = (from e in (from e in list2
					group e by e into g
					where g.Count() == 1
					select g).SelectMany((IGrouping<Edge, Edge> g) => g)
				where e.ApproximateLength > ShortCurveTolerance
				select e).ToList();
			List<Edge> list4 = list2.Except(list3).Distinct().ToList();
			List<CurveLoop> list5 = BuildCurveLoop(list3, out var reliefPoints);
			if (list5 == null || list5.Count == 0)
			{
				return;
			}
			Floor floor = Floor.Create(_doc, list5, floorType, levelId);
            created.Add(floor.Id.Value);
			SlabShapeEditor slabShapeEditor = floor.GetSlabShapeEditor();
			ApplyPoints(slabShapeEditor, reliefPoints);
			foreach (Edge item in list4)
			{
				if (!(item.AsCurve() is Line))
				{
					break;
				}
				IList<XYZ> points = item.Tessellate();
				List<SlabShapeVertex> list6 = (from ssv in slabShapeEditor.SlabShapeVertices.OfType<SlabShapeVertex>()
					where points.Any((XYZ p) => p.DistanceTo(ssv.Position) < ShortCurveTolerance)
					select ssv).ToList();
				if (list6.Count == 2)
				{
					slabShapeEditor.AddSplitLine(list6[0], list6[1]);
				}
			}
		});
	}

	private List<CurveLoop> BuildCurveLoop(List<Edge> edges, out List<XYZ> reliefPoints)
	{
		List<CurveLoop> list = new List<CurveLoop>();
		LinkedList<(Curve, XYZ, XYZ)> linkedList = new LinkedList<(Curve, XYZ, XYZ)>(from e in edges
			select e.AsCurve() into c
			select (c: c, c.GetEndPoint(0), c.GetEndPoint(1)));
		List<(Curve Curve, XYZ End0, XYZ End1)> prepCurves = new List<(Curve, XYZ, XYZ)>();
		reliefPoints = new List<XYZ>();
		while (linkedList.Count > 0)
		{
			if (prepCurves.Count == 0)
			{
				prepCurves.Add(linkedList.First());
				linkedList.RemoveFirst();
			}
			else
			{
                var matches = linkedList.Where(c => c.Item2.DistanceTo(prepCurves.Last().End1) < ShortCurveTolerance || c.Item3.DistanceTo(prepCurves.Last().End1) < ShortCurveTolerance).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("The boundary contains a gap or a branch and cannot form an unambiguous floor profile.");
                (Curve, XYZ, XYZ) tuple = matches[0];
				prepCurves.Add((tuple.Item2.DistanceTo(prepCurves.Last().End1) < ShortCurveTolerance) ? tuple : (tuple.Item1.CreateReversed(), tuple.Item3, tuple.Item2));
				linkedList.Remove(tuple);
			}
			if (prepCurves.Last().End1.DistanceTo(prepCurves.First().End0) < ShortCurveTolerance)
			{
				List<IList<XYZ>> list2 = prepCurves.Select(((Curve Curve, XYZ End0, XYZ End1) c) => (IList<XYZ>)c.Curve.Tessellate().ToList()).ToList();
				list2.ForEach((IList<XYZ> l) =>
				{
					l.RemoveAt(l.Count - 1);
				});
				List<XYZ> list3 = list2.SelectMany((IList<XYZ> p) => p).ToList();
				reliefPoints.AddRange(list3);
				List<XYZ> list4 = list3.Select((XYZ p) => new XYZ(p.X, p.Y, 0.0)).ToList();
				IEnumerable<Curve> source = list4.Zip(list4.Skip(1).Append(list4.First()), Line.CreateBound);
				list.Add(CurveLoop.Create(source.ToList()));
				prepCurves.Clear();
			}
		}
        if (prepCurves.Count > 0) throw new InvalidOperationException("The projected floor boundary is open.");
		return list;
	}

	private bool LooksUp(Face face)
	{
		BoundingBoxUV boundingBox = face.GetBoundingBox();
		UV point = (boundingBox.Min + boundingBox.Max) / 2.0;
		return face.ComputeNormal(point).Z > 0.01;
	}
}
