using System.Text.Json;
using ClaudeRevit.Services;
using ClaudeRevit.Tools;
using Autodesk.Revit.UI;

var checks=0;
void Check(bool condition,string name) { if(!condition)throw new Exception(name); checks++; }
Dictionary<string,JsonElement> Args(string json)=>JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(json)!;
JsonElement Call(IRevitTool tool,string json,UIApplication app)=>JsonSerializer.Deserialize<JsonElement>(tool.Execute(Args(json),app));
void Reject(Action action,string name) { try{action();}catch(ToolInputException){checks++;return;} throw new Exception("Not rejected: "+name); }
var data=Path.Combine(Path.GetTempPath(),"ClaudeRevit-ModPlus-contracts-"+Guid.NewGuid());
var previous=Environment.GetEnvironmentVariable("CLAUDEREVIT_MODPLUS_DATA");
Environment.SetEnvironmentVariable("CLAUDEREVIT_MODPLUS_DATA",data);
try
{
    var app=new UIApplication();
    var panel=new RibbonPanel();
    var pull=new PulldownButton();
    pull.Items.Add(new PushButton { Name="Sum", ItemText="Сумма параметров", ClassName="mprSumParameters.Command" });
    panel.Items.Add(pull); app.Panels.Add(panel);
    var list=new GetModPlusTools(); var algorithm=new GetModPlusAlgorithm(); var run=new RunModPlusCommand(); var sum=new ModPlusSumParameters();
    foreach(var tool in new IRevitTool[]{list,algorithm,run,sum})
    {
        Check(!tool.RequiresTransaction,tool.Name+" transaction contract");
        Check(!tool.RequiresCodeExecutionOptIn,tool.Name+" fixed native adapter");
        var schema=JsonSerializer.SerializeToElement(tool.InputSchema);
        Check(schema.GetProperty("type").GetString()=="object",tool.Name+" real MCP object schema");
    }
    var catalog=Call(list,"{\"module\":\"mprSumParameters\"}",app);
    Check(catalog.GetProperty("modules")[0].GetProperty("installed")[0].GetProperty("className").GetString()=="mprSumParameters.Command","nested ribbon matching");
    for(int offset=0;offset<86;offset+=10)
    {
        var result=list.Execute(Args("{\"offset\":"+offset+"}"),app);
        Check(result.Length<40000,"catalog pages stay below transport cap");
    }
    var metadata=Call(algorithm,"{\"module\":\"mprSumParameters\",\"class_query\":\"Services\",\"method_query\":\"GetSelectElements\"}",app);
    Check(!metadata.GetProperty("source_available").GetBoolean(),"metadata without local source");
    Check(metadata.GetProperty("algorithmClasses")[0].GetProperty("methods")[0].GetProperty("name").GetString()=="GetSelectElements","public service mapping");
    Directory.CreateDirectory(Path.Combine(data,"source"));
    File.WriteAllLines(Path.Combine(data,"source","mprSumParameters_2027.decompiled.cs"),["// fixture one","// fixture two","// fixture three"]);
    var excerpt=Call(algorithm,"{\"module\":\"mprSumParameters\",\"start_line\":2,\"line_count\":1}",app);
    Check(excerpt.GetProperty("source").GetString()=="2: // fixture two","local source paging");
    Reject(()=>algorithm.Execute(Args("{\"module\":\"../outside\"}"),app),"unknown module");
    Reject(()=>algorithm.Execute(Args("{\"module\":\"mprSumParameters\",\"execute\":true}"),app),"executable argument");
    Reject(()=>algorithm.Execute(Args("{\"module\":\"mprSumParameters\",\"line_count\":301}"),app),"source range");
    var preview=Call(run,"{\"command\":\"mprSumParameters.Command\",\"element_ids\":[2]}",app);
    Check(preview.GetProperty("preview").GetBoolean()&&!preview.GetProperty("posted").GetBoolean()&&app.Posted.Count==0,"preview never posts");
    Check(app.ActiveUIDocument!.Selection.GetElementIds().Single().Value==1,"preview preserves selection");
    Reject(()=>run.Execute(Args("{\"command\":\"mprSumParameters.Command\",\"command_id\":\"CustomCtrl_%CustomCtrl_%BIM-STARTER%Utilities%Sum\"}"),app),"foreign command ID");
    Reject(()=>run.Execute(Args("{\"command\":\"mprSumParameters.Command\",\"element_ids\":[999]}"),app),"missing selection ID");
    ToolDispatcher.ForceSuppress=true;
    Reject(()=>run.Execute(Args("{\"command\":\"mprSumParameters.Command\",\"preview\":false}"),app),"unattended posting");
    ToolDispatcher.ForceSuppress=false;
    ToolContext.Bound=new UIDocument();
    Reject(()=>run.Execute(Args("{\"command\":\"mprSumParameters.Command\"}"),app),"another document");
    Reject(()=>sum.Execute(Args("{}"),app),"sum in another document");
    ToolContext.Bound=null;
    app.Application.VersionNumber="2026";
    Reject(()=>sum.Execute(Args("{}"),app),"unsupported Revit execution");
    Check(Call(list,"{}",app).GetProperty("currentRevit").GetString()=="2026","cross-version catalog inspection");
    app.Application.VersionNumber="2027";
    Reject(()=>sum.Execute(Args("{}"),app),"unloaded module");
    app.FailPost=true;
    try { run.Execute(Args("{\"command\":\"mprSumParameters.Command\",\"element_ids\":[2],\"preview\":false}"),app); throw new Exception("Expected posting failure."); }
    catch(InvalidOperationException){ Check(app.ActiveUIDocument.Selection.GetElementIds().Single().Value==1,"failed posting restores selection"); }
    app.FailPost=false;
    var posted=Call(run,"{\"command\":\"mprSumParameters.Command\",\"element_ids\":[2],\"preview\":false}",app);
    Check(posted.GetProperty("posted").GetBoolean()&&!posted.GetProperty("completed").GetBoolean()&&app.Posted.Count==1,"posted is not completed");
    Check(app.ActiveUIDocument.Selection.GetElementIds().Single().Value==2,"selection passed to posted command");
    Console.WriteLine(JsonSerializer.Serialize(new { checks, status="passed", nativeRevitExecution="not tested", scope="production tools with isolated Revit contracts and real Anthropic schemas" }));
}
finally { Environment.SetEnvironmentVariable("CLAUDEREVIT_MODPLUS_DATA",previous); ToolContext.Bound=null; ToolDispatcher.ForceSuppress=false; if(Directory.Exists(data))Directory.Delete(data,true); }
