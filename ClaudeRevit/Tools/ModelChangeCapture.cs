using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace ClaudeRevit.Tools;

// Records what one tool call changed in the model, for the task journal on disk.
//
// It used to take a snapshot of EVERY element and type in the document on the first call per
// document (on Revit's UI thread), keep it for the document's lifetime, and refresh it on every
// edit the user made by hand — all to tell "added in this call" from "already existed". The
// DocumentChanged event already says which ids a call added, modified and deleted, so the
// snapshot is gone; and per-element work (category, representation) is done for a bounded sample,
// because a single call can create tens of thousands of rebar.
internal sealed class ModelChangeCapture
{
    private sealed record Info(string Category,string Representation);
    private const int SampleLimit=2000; // elements whose category/representation is resolved
    private const int IdLimit=500;      // ids written to the journal per list (counts stay exact)
    private static ModelChangeCapture? _current;
    private readonly Document _doc;
    private readonly HashSet<long> _added=new(),_modified=new(),_deleted=new();
    private readonly Dictionary<long,Info> _seen=new();
    private static Info Snapshot(Element? e)
    {
        try { return new(e?.Category?.Name ?? "(no category)", e == null ? "deleted" : ModelProvenance.Representation(e)); }
        catch { return new("(unavailable)", "(unavailable)"); }
    }
    private object Readback(long id)
    {
        try
        {
            var element = _doc.GetElement(new ElementId(id)); var box = element?.get_BoundingBox(null);
            return new { id, exists = element != null, category = element?.Category?.Name,
                representation = element == null ? "missing" : ModelProvenance.Representation(element),
                bounds_mm = box == null ? null : new[] { (box.Max.X-box.Min.X)*Units.MmPerFoot, (box.Max.Y-box.Min.Y)*Units.MmPerFoot, (box.Max.Z-box.Min.Z)*Units.MmPerFoot } };
        }
        catch (Exception ex) { return new { id, status = "incomplete", reason = ex.Message }; }
    }
    public ModelChangeCapture(Document doc,string key){_doc=doc;_current=this;}
    // What the finished call left in the model (zero when it rolled back), for the action history.
    public int AddedCount{get;private set;}
    public int ModifiedCount{get;private set;}
    public int DeletedCount{get;private set;}
    public IReadOnlyList<long> AddedSample=>_addedSample;
    // What the call has changed so far — also for a preview, before its rollback is applied.
    public (int Added,int Modified,int Deleted) Raw()=>(_added.Count,_modified.Count(id=>!_added.Contains(id)&&!_deleted.Contains(id)),_deleted.Count(id=>!_added.Contains(id)));
    private long[] _addedSample=[];
    public static void Changed(DocumentChangedEventArgs e)
    {
        UndoSequencer.Changed(e);
        var c=_current;
        // Edits outside a tool call (by hand, other add-ins, undo/redo) invalidate pending plans.
        if((c==null||!Services.DocumentSessions.Same(c._doc,e.GetDocument()))&&Services.DocumentSessions.ExistingKey(e.GetDocument()) is { } changedKey)
        {
            WritePlans.Bump(changedKey);
            GetProjectCatalog.Invalidate(e.GetDocument());
            // Manual edits/Undo/Redo destroy the correspondence to Revit's undo stack.
            if (!UndoSequencer.Active) ActionHistory.Invalidate(changedKey);
        }
        if(c==null||!Services.DocumentSessions.Same(c._doc,e.GetDocument()))return;
        foreach(var id in e.GetAddedElementIds()){c._added.Add(id.Value);c.Sample(id);}
        foreach(var id in e.GetModifiedElementIds()){c._modified.Add(id.Value);c.Sample(id);}
        foreach(var id in e.GetDeletedElementIds())c._deleted.Add(id.Value);
    }
    private void Sample(ElementId id){if(_seen.Count<SampleLimit)_seen[id.Value]=Snapshot(_doc.GetElement(id));}
    public object? Receipt { get; private set; }
    public object Complete(bool rolledBack)
    {
        _current=null;
        bool Exists(long id)=>_doc.IsValidObject&&_doc.GetElement(new ElementId(id))!=null;
        var deleted=rolledBack?[]:_deleted.Where(id=>!_added.Contains(id)).ToArray();
        var added=rolledBack?[]:_added.Where(Exists).ToArray();
        var deletedSet=deleted.ToHashSet();
        var modified=rolledBack?[]:_modified.Where(id=>!_added.Contains(id)&&!deletedSet.Contains(id)&&Exists(id)).ToArray();
        // Resolve only affected surviving elements, not the entire model. Counts by
        // category must not silently become sampled counts on large batches.
        foreach (var id in added.Concat(modified)) _seen[id] = Snapshot(_doc.GetElement(new ElementId(id)));
        Info InfoOf(long id)=>_seen.TryGetValue(id,out var i)?i:new("(deleted category unknown)","deleted");
        object ByCategory(long[] ids)=>ids.GroupBy(id=>InfoOf(id).Category).ToDictionary(g=>g.Key,g=>g.Count());
        AddedCount=added.Length;ModifiedCount=modified.Length;DeletedCount=deleted.Length;_addedSample=added.Take(IdLimit).ToArray();
        var receiptId = "changes_" + Guid.NewGuid().ToString("N");
        var detail = Services.Json.Serialize(new
        {
            added_ids = added, modified_ids = modified, deleted_ids = deleted,
            rolled_back = rolledBack, units = "Revit internal: ft, kg; bounds below in mm"
        });
        // A rolled-back or preview call has nothing to page through; archiving its empty list would
        // only push real results out of the bounded archive.
        var archived = added.Length + modified.Length + deleted.Length > 0;
        if (archived) Services.ToolResultArchive.Record(receiptId, detail);
        Receipt = new
        {
            added = added.Length, modified = modified.Length, deleted = deleted.Length,
            rolled_back = rolledBack, details_id = archived ? receiptId : null,
            details_truncated = detail.Length > Services.ToolResultArchive.MaxContentChars,
            verification = "Changed IDs re-read after execution; dimensional/parameter expectations require verify_model_result.",
            readback = added.Concat(modified).Take(8).Select(Readback).ToArray()
        };
        return new { receipt_id=archived ? receiptId : null, added_count=added.Length,modified_count=modified.Length,deleted_count=deleted.Length,
            added_by_category=ByCategory(added),modified_by_category=ByCategory(modified),
            added_by_representation=added.GroupBy(id=>InfoOf(id).Representation).ToDictionary(g=>g.Key,g=>g.Count()),
            added_ids=added.Take(IdLimit).ToArray(),modified_ids=modified.Take(IdLimit).ToArray(),deleted_ids=deleted.Take(IdLimit).ToArray(),
            ids_truncated=added.Length>IdLimit||modified.Length>IdLimit||deleted.Length>IdLimit,
            rolled_back=rolledBack };
    }
}
