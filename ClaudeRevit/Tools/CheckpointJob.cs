using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;
namespace ClaudeRevit.Tools;

internal sealed class JobRecord
{
    public string Key {get;set;}="";
    public int Revision {get;set;}
    public string PlanHash {get;set;}="";
    public CheckpointPlan Plan {get;set;}=new();
    public Dictionary<string,JsonElement> Results {get;set;}=new();
    public Dictionary<string,string> Signatures {get;set;}=new();
}
internal static class CheckpointJobs
{
    private static readonly Guid SchemaId=new("0e3c8c19-e074-45aa-999e-10a0d6431182");
    private static Schema Schema()
    {var schema=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(schema!=null)return schema;var b=new SchemaBuilder(SchemaId);b.SetSchemaName("ClaudeRevitCheckpointJobV1");b.SetReadAccessLevel(AccessLevel.Public);b.SetWriteAccessLevel(AccessLevel.Public);b.AddSimpleField("Json",typeof(string));return b.Finish();}
    public static IEnumerable<(DataStorage Storage,JobRecord Record)> All(Document doc)
    {
        var schema=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(schema==null)yield break;
        foreach(var s in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
        {var e=s.GetEntity(schema);if(e.IsValid()&&JsonSerializer.Deserialize<JobRecord>(e.Get<string>(schema.GetField("Json")),CheckpointPlan.Options) is { } r)yield return(s,r);}
    }
    public static void Save(DataStorage s,JobRecord r)
    {var text=JsonSerializer.Serialize(r,CheckpointPlan.Options);if(text.Length>1_000_000)throw new ToolInputException("Checkpoint record exceeds 1 MB; split the plan.");var schema=Schema();var e=new Entity(schema);e.Set(schema.GetField("Json"),text);s.SetEntity(e);}
    public static string[] Stale(Document doc,JobRecord r)=>r.Signatures.Where(p=>doc.GetElement(p.Key) is not { } e||ConnectionNodes.Signature(e)!=p.Value).Select(p=>p.Key).ToArray();
    public static IRevitTool Tool(string name)
    {
        var tool=ToolRegistry.Instance.Get(name)??throw new ToolInputException("Unknown tool: "+name);
        if(!tool.RequiresTransaction||tool.IsScriptTool||tool is DynamicToolProxy||name is "run_batch" or "delete_elements"||tool.RequiresNoTurnGroup)throw new ToolInputException("Checkpoint steps need bounded built-in transactional tools; scripts, deletion, dynamic tools, batches and document lifecycle tools are excluded.");
        if(tool.RequiresConfirmation&&SettingsStore.ConfirmOperations)throw new ToolInputException("Call confirmation-requiring tools directly.");
        if(SettingsStore.DisabledToolGroups.Contains(ToolCatalog.CategoryOf(tool),StringComparer.OrdinalIgnoreCase))throw new ToolInputException("Tool group is disabled: "+name);
        return tool;
    }
}
public sealed class RunCheckpointJob : IRevitTool
{
    public string Name=>"run_checkpoint_job";
    public string Description=>"Run/resume a document-local plan in bounded atomic batches (default 5 steps, max 25). Completed step outputs and actual changed-element signatures persist in RVT DataStorage in the same transaction as edits, preventing duplicate retries. Repeating a completed job is a no-op. Rejects changed plan/revision or external edits/missing elements. One Undo per committed batch, no transaction held between MCP calls. Default preview=true does not advance progress. Save the RVT to make checkpoints durable across process crashes; unsaved Revit edits are not recovered automatically.";
    public bool RequiresTransaction=>false;
    public bool RequiresNoTurnGroup=>true;
    public bool MutatesWithoutTransaction=>true;
    public bool InvalidatesCatalog=>true;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new()
    {
        ["job_key"]=NativeToolUtil.Field("string","Stable 1..64 character document-local key."),
        ["plan"]=NativeToolUtil.Any("New job: {steps:[{key,tool,arguments:{...},watch_ids?:[IDs]}]}. Use one ordinary native transactional tool per step. Argument values can reference earlier results as {from_step:key,json_pointer:'/id'}. Omit plan to resume. watch_ids are extra existing elements whose edits/deletion block continuation."),
        ["batch_size"]=NativeToolUtil.Field("integer","Default 5; 1..25. Each call yields after one batch."),["expected_revision"]=NativeToolUtil.Field("integer","Optimistic lock: 0 for a new job."),["preview"]=NativeToolUtil.Field("boolean","Default true; false commits this batch.")
    },"job_key","expected_revision");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var key=ToolInput.RequiredString(input,"job_key");if(key.Length>64)throw new ToolInputException("job_key exceeds 64 chars.");
        var matches=CheckpointJobs.All(doc).Where(p=>p.Record.Key==key).Take(2).ToArray();if(matches.Length>1)throw new ToolInputException("Duplicate copied job key; resolve its DataStorage first.");
        var storage=matches.FirstOrDefault().Storage;var old=matches.FirstOrDefault().Record;
        if(input["expected_revision"].GetInt32()!=(old?.Revision??0))throw new ToolInputException("Checkpoint revision differs; inspect get_checkpoint_job.");
        var plan=input.TryGetValue("plan",out var p)?p.Deserialize<CheckpointPlan>(CheckpointPlan.Options)??throw new ToolInputException("Plan is empty."):old?.Plan??throw new ToolInputException("A new job requires a plan.");plan.Validate();
        if(old!=null&&old.PlanHash!=plan.Hash())throw new ToolInputException("Saved plan hash differs; start an explicit new job instead of reusing completed steps.");
        if(old!=null&&CheckpointJobs.Stale(doc,old).Length>0)throw new ToolInputException("Completed elements were edited/deleted externally; inspect before continuation.");
        var record=old==null?new JobRecord{Key=key,Plan=plan,PlanHash=plan.Hash()}:JsonSerializer.Deserialize<JobRecord>(JsonSerializer.Serialize(old,CheckpointPlan.Options),CheckpointPlan.Options)!;
        int start=record.Results.Count;if(start==plan.Steps.Count)return Services.Json.Serialize(new {job_key=key,revision=record.Revision,complete=true,no_op=true});
        var batch=ToolInput.OptionalInt(input,"batch_size")??5;if(batch is <1 or >25)throw new ToolInputException("batch_size must be 1..25.");
        foreach(var step in plan.Steps.Skip(start).Take(batch))CheckpointJobs.Tool(step.Tool);
        var preview=NativeToolUtil.Preview(input);var emitted=new List<object>();
        var (result,warnings)=NativeToolUtil.Commit(doc,"Claude: checkpoint "+key,preview,()=>
        {
            var initial=BenchmarkModelProbe.AllElementIds(doc).ToHashSet();if(initial.Count>50000)throw new ToolInputException("Checkpoint evidence is bounded to 50000 elements; split/narrow the test model.");
            var watched=plan.Steps.Skip(start).Take(batch).SelectMany(s=>s.WatchIds).ToHashSet();
            void Ids(JsonElement value,string name="")
            {
                if(value.ValueKind==JsonValueKind.Object){foreach(var prop in value.EnumerateObject())Ids(prop.Value,prop.Name);}
                else if(value.ValueKind==JsonValueKind.Array){foreach(var item in value.EnumerateArray())Ids(item,name);}
                else if((name.EndsWith("_id",StringComparison.Ordinal)||name.EndsWith("_ids",StringComparison.Ordinal))&&value.TryGetInt64(out var id)&&initial.Contains(id))watched.Add(id);
            }
            var signatures=record.Signatures.Keys.Select(doc.GetElement).Where(e=>e!=null).ToDictionary(e=>e!.UniqueId,e=>ConnectionNodes.Signature(e!));
            foreach(var step in plan.Steps.Skip(start).Take(batch))
            {
                ToolContext.ReportProgress(record.Results.Count,plan.Steps.Count,"checkpoint steps");
                var args=CheckpointPlan.Resolve(step.Arguments,record.Results).EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone());
                foreach(var argument in args)Ids(argument.Value,argument.Key);
                foreach(var id in watched){var e=NativeToolUtil.Element(doc,id);signatures.TryAdd(e.UniqueId,ConnectionNodes.Signature(e));}
                var inner=CheckpointJobs.Tool(step.Tool);inner.Preflight(args,app);var raw=inner.Execute(args,app);
                if(ToolResult.ErrorMessage(raw) is { } error)throw new ToolInputException("Step "+step.Key+": "+error);
                using var parsed=JsonDocument.Parse(raw);record.Results[step.Key]=parsed.RootElement.Clone();doc.Regenerate();
                foreach(var id in step.WatchIds){var e=NativeToolUtil.Element(doc,id);record.Signatures[e.UniqueId]=ConnectionNodes.Signature(e);}
                emitted.Add(new {key=step.Key,result=parsed.RootElement.Clone()});
            }
            var after=BenchmarkModelProbe.AllElementIds(doc);if(initial.Except(after).Any())throw new ToolInputException("This batch deleted existing elements; deletion plans require explicit manual handling.");
            foreach(var id in after)
            {
                var e=doc.GetElement(new ElementId(id));if(e is null or DataStorage)continue;
                if(initial.Contains(id)&&!signatures.ContainsKey(e.UniqueId)&&!record.Signatures.ContainsKey(e.UniqueId))continue;var signature=ConnectionNodes.Signature(e);if(!initial.Contains(id)||signatures.TryGetValue(e.UniqueId,out var before)&&before!=signature||record.Signatures.ContainsKey(e.UniqueId))record.Signatures[e.UniqueId]=signature;
            }
            record.Revision++;storage??=DataStorage.Create(doc);CheckpointJobs.Save(storage,record);
            return new {job_key=key,revision=record.Revision,completed=record.Results.Count,total=plan.Steps.Count,complete=record.Results.Count==plan.Steps.Count,next_step=record.Results.Count<plan.Steps.Count?plan.Steps[record.Results.Count].Key:null,steps=emitted};
        },()=>
        {
            // Updaters and Revit joins may alter geometry during commit. Capture the
            // committed state before yielding, in the same bounded transaction group.
            using var tx=new Transaction(doc,"Claude: committed checkpoint evidence");tx.Start();
            foreach(var uid in record.Signatures.Keys.ToArray()){var e=doc.GetElement(uid)??throw new ToolInputException("A committed step element disappeared.");record.Signatures[uid]=ConnectionNodes.Signature(e);}
            CheckpointJobs.Save(storage!,record);if(tx.Commit()!=TransactionStatus.Committed)throw new ToolInputException("Checkpoint evidence commit failed.");
        });
        return Services.Json.Serialize(new {preview,result,warnings,save_required_for_crash_durability=!preview});
    }
}
public sealed class GetCheckpointJob : IRevitTool
{
    public string Name=>"get_checkpoint_job";
    public string Description=>"Inspect persisted job revision, completed outputs and stale/missing element signatures before resuming. Omit job_key to list document-local jobs. Survives reopening a saved RVT; unsaved changes follow ordinary Revit recovery.";
    public bool RequiresTransaction=>false;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new(){["job_key"]=NativeToolUtil.Field("string","Optional saved job key.")});
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var key=NativeToolUtil.Text(input,"job_key");var jobs=CheckpointJobs.All(doc).Where(p=>key.Length==0||p.Record.Key==key).Take(100).Select(p=>new {key=p.Record.Key,revision=p.Record.Revision,plan_hash=p.Record.PlanHash,completed=p.Record.Results.Count,total=p.Record.Plan.Steps.Count,results=key.Length==0?null:p.Record.Results,stale_unique_ids=CheckpointJobs.Stale(doc,p.Record)}).ToArray();return Services.Json.Serialize(new {jobs});
    }
}
