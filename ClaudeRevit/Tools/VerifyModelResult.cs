using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;
namespace ClaudeRevit.Tools;

internal static class VerificationContext
{
    public static string Version(Document doc) => doc.Application.VersionNumber + "/" + doc.Application.VersionBuild;
    public static string Key(Document doc)
    {
        var types=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Select(s=>$"{s.Category?.Id.Value}|{s.FamilyName}|{s.Name}").Order(StringComparer.Ordinal).ToArray();
        var names=doc.IsFamilyDocument?doc.FamilyManager.Parameters.Cast<FamilyParameter>().Select(p=>$"{p.Definition.Name}|{p.Definition.GetDataType().TypeId}|{p.IsInstance}|{p.Formula}").Order(StringComparer.Ordinal).ToArray():[];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {doc.IsFamilyDocument,category=doc.IsFamilyDocument?doc.OwnerFamily.FamilyCategory?.Id.Value:null,types,names}))));
    }
}
public sealed class VerifyModelResult : IRevitTool
{
    public string Name => "verify_model_result";
    public string Description => "Independently compare current native element geometry/parameters with declared expected values. Each assertion is passed/failed/incomplete. Optionally associate checks with an exact journal run_id in this document and store verified experience; code success alone never qualifies. Bounds are actual solid tessellation, volume is actual solid volume. Missing geometry cannot pass. Records certify only the supplied checks, not engineering adequacy or an optimal workflow.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["checks"] = NativeToolUtil.Any("1..100 {kind:bounds|volume|parameter|native_rebar_host,element_id,expected_mm?:[X,Y,Z],expected_m3?,parameter_name? OR parameter_guid?,expected_value?,unit?:internal|mm|kg,tolerance?:nonnegative}. bounds compares solid extents; volume in m3. Native host validates Rebar and RebarHostData relationship, not cover."),
        ["journal_run_id"] = NativeToolUtil.Field("string", "Optional exact run_id returned by get_script_journal. All checked IDs must be actual added/modified IDs of that run, and at least one geometry assertion is required to record experience.")
    }, "checks");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app); var raw = input["checks"].EnumerateArray().ToArray(); if (raw.Length is <1 or >100) throw new ToolInputException("Supply 1..100 checks.");
        var checks=new List<ObjectiveCheck>(); var signatures=new Dictionary<string,string>(); var ids=new HashSet<long>(); bool geometryAssertion=false;
        foreach(var c in raw)
        {
            ToolContext.ThrowIfCancelled(); var id=c.GetProperty("element_id").GetInt64();ids.Add(id);var kind=c.GetProperty("kind").GetString()??"";
            double tolerance=c.TryGetProperty("tolerance",out var t)?t.GetDouble():kind=="volume"?1e-6:1;
            if(!double.IsFinite(tolerance)||tolerance<0)throw new ToolInputException("Tolerance must be finite and nonnegative.");
            var e=doc.GetElement(new ElementId(id));if(e==null){checks.Add(new(kind+":"+id,"failed","Element missing."));continue;}
            signatures[e.UniqueId]=ConnectionNodes.Signature(e);
            try
            {
                bool? valid=null;string detail="";
                switch(kind)
                {
                    case "bounds": case "volume":
                        geometryAssertion=true;var solids=ConnectionNodes.Solids(e);if(solids.Count==0){detail="No native solid geometry.";break;}
                        if(kind=="volume")
                        {var value=solids.Sum(s=>s.Volume)*Math.Pow(.3048,3);var expected=c.GetProperty("expected_m3").GetDouble();if(!double.IsFinite(expected)||expected<=0)throw new ToolInputException("Expected volume must be positive and finite.");valid=Math.Abs(value-expected)<=tolerance;detail=$"Actual volume {value:R} m3; expected {expected:R}.";}
                        else
                        {var expected=c.GetProperty("expected_mm").EnumerateArray().Select(x=>x.GetDouble()).ToArray();if(expected.Length!=3||expected.Any(x=>!double.IsFinite(x)||x<=0))throw new ToolInputException("Expected bounds need three positive finite extents.");var points=solids.SelectMany(s=>s.Faces.Cast<Face>()).SelectMany(f=>f.Triangulate().Vertices).Take(50001).Select(NativeToolUtil.Mm).ToArray();if(points.Length>50000){detail="Geometry vertex evidence limit exceeded.";break;}if(points.Length==0){detail="No actual solid vertices.";break;}var actual=Enumerable.Range(0,3).Select(i=>points.Max(p=>p[i])-points.Min(p=>p[i])).ToArray();valid=actual.Zip(expected,(a,b)=>Math.Abs(a-b)<=tolerance).All(x=>x);detail="Actual extents mm: "+JsonSerializer.Serialize(actual);}
                        break;
                    case "parameter":
                        var p=NativeToolUtil.Parameter(e,c.TryGetProperty("parameter_name",out var n)?n.GetString()??"":"",c.TryGetProperty("parameter_guid",out var g)?g.GetString()??"":"");
                        if(p==null||!p.HasValue){valid=false;detail="Parameter missing/unset.";break;}
                        var v=c.GetProperty("expected_value");var unit=c.TryGetProperty("unit",out var u)?u.GetString():"internal";
                        if(p.StorageType==StorageType.String){valid=p.AsString()==v.GetString();detail="Actual text: "+p.AsString();}
                        else if(p.StorageType is StorageType.Double or StorageType.Integer)
                        {double actual=p.StorageType==StorageType.Double?p.AsDouble():p.AsInteger();if(unit=="mm"&&p.Definition.GetDataType()==SpecTypeId.Length)actual*=Units.MmPerFoot;else if(unit=="kg"&&p.Definition.GetDataType()==SpecTypeId.Mass)actual=UnitUtils.ConvertFromInternalUnits(actual,UnitTypeId.Kilograms);else if(unit!="internal")throw new ToolInputException("Parameter dimension does not match unit.");var expected=v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean()?1:0:v.GetDouble();if(!double.IsFinite(expected))throw new ToolInputException("Expected parameter must be finite.");valid=Math.Abs(actual-expected)<=tolerance;detail=$"Actual value {actual:R} {unit}.";}
                        else {detail="Unsupported parameter storage.";}
                        break;
                    case "native_rebar_host":
                        valid=e is Rebar r && doc.GetElement(r.GetHostId()) is { } host && RebarHostData.GetRebarHostData(host)?.IsValidHost()==true && r.Quantity>0;
                        detail="Native Rebar host validity/quantity only; cover and unsampled positions are not certified.";break;
                    default:throw new ToolInputException("Unknown check kind.");
                }
                checks.Add(new(kind+":"+id,valid==null?"incomplete":valid.Value?"passed":"failed",detail));
            }
            catch(Autodesk.Revit.Exceptions.RegenerationFailedException){throw;}
            catch(OperationCanceledException){throw;}
            catch(Exception ex){checks.Add(new(kind+":"+id,"incomplete",ex.Message));}
        }
        VerifiedExperience? recorded=null;
        if(input.TryGetValue("journal_run_id",out var run))
        {
            var entry=ScriptJournal.ReadRecent(500).FirstOrDefault(e=>e.TryGetProperty("run_id",out var ri)&&ri.GetString()==run.GetString());
            if(entry.ValueKind!=JsonValueKind.Object||entry.GetProperty("document_key").GetString()!=DocumentSessions.Key(doc))throw new ToolInputException("Journal run not found in this document session.");
            var changed=entry.GetProperty("changes");var affected=new[]{"added_ids","modified_ids"}.SelectMany(k=>changed.TryGetProperty(k,out var a)&&a.ValueKind==JsonValueKind.Array?a.EnumerateArray().Select(v=>v.GetInt64()):[]).ToHashSet();
            if(!geometryAssertion||ids.Any(id=>!affected.Contains(id))||!entry.GetProperty("ok").GetBoolean())throw new ToolInputException("Experience requires actual geometry assertions on the successful run's affected elements.");
            recorded=new(run.GetString()!,entry.GetProperty("code_sha256").GetString()!,entry.GetProperty("code_truncated").GetBoolean()?null:entry.GetProperty("code").GetString(),entry.GetProperty("tool").GetString()!,VerificationContext.Version(doc),VerificationContext.Key(doc),DateTime.UtcNow.ToString("o"),entry.GetProperty("duration_ms").GetDouble()/1000,checks,signatures);
            VerifiedExperienceStore.Save(recorded);
        }
        return Services.Json.Serialize(new {checks,passed=checks.All(c=>c.Status=="passed"),experience_recorded=recorded!=null,scope="Only declared checks; failed/incomplete records are retained but never returned as successful experience."});
    }
}
public sealed class GetVerifiedExperience : IRevitTool
{
    public string Name=>"get_verified_experience";
    public string Description=>"Read optional verified experience with matching actual Revit build and family/type catalog context. Includes exact checks, duration, code hash and complete code only when it was stored without truncation. No automatic replay or forced strategy; choose an improved approach freely. Failures are kept separately and returned only with include_failures=true.";
    public bool RequiresTransaction=>false;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new(){["include_failures"]=NativeToolUtil.Field("boolean","Default false.")});
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {var doc=NativeToolUtil.Doc(app);var version=VerificationContext.Version(doc);var context=VerificationContext.Key(doc);return Services.Json.Serialize(new {revit_version=version,context_key=context,optional=true,records=VerifiedExperienceStore.Read().Where(r=>r.RevitVersion==version&&r.ContextKey==context&&(r.Passed||ToolInput.Flag(input,"include_failures"))).Take(20).ToArray()});}
}
