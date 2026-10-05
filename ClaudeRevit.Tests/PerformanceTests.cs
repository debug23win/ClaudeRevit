using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public sealed class PerformanceTests
{
    [Fact]
    public async Task TaskJournalChannelCorrelationSurvivesAsyncBoundaries()
    {
        using var task=TaskJournal.Start("test-project");var id=TaskJournal.CurrentId;
        var channel=McpTurnChannel.Open(CancellationToken.None,"test-project");
        try
        {
            Assert.Equal(id,channel.TaskId);
            await Task.Run(()=>{TaskJournal.RecordTiming(channel.TaskId,0.5,1.5);TaskJournal.RecordWorker(channel.TaskId,2.25);});
            Assert.Equal((0.5,1.5),TaskJournal.ReadTimings(id));
            Assert.Equal(2.25,TaskJournal.ReadWorker(id));
            using(var nested=TaskJournal.Start("other-project"))Assert.NotEqual(id,TaskJournal.CurrentId);
            Assert.Equal(id,TaskJournal.CurrentId);
        }
        finally { await channel.CloseAsync(); }
    }
    [Fact]
    public void EmbeddedNewtonsoftCloneDoesNotMakeUserSnippetAmbiguous()
    {
        var root=Path.Combine(Path.GetTempPath(),"ClaudeRevit-json-reference-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context=new System.Runtime.Loader.AssemblyLoadContext("JsonReferenceTest",isCollectible:true);
        try
        {
            void Load(string assemblyName)
            {
                var syntax=Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("namespace Newtonsoft.Json { public static class JsonConvert {} }");
                var compilation=Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(assemblyName,[syntax],
                    [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                    new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
                var path=Path.Combine(root,assemblyName+".dll");
                using(var output=File.Create(path))Assert.True(compilation.Emit(output).Success);
                context.LoadFromAssemblyPath(path);
            }
            if(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="Newtonsoft.Json"))Load("Newtonsoft.Json");
            Load("ModPlus.Newtonsoft.Json.ReferenceTest");
            var (bytes,error)=ClaudeRevit.Tools.ScriptCompiler.CompileAssembly("public static class Probe { public static System.Type Read() => typeof(Newtonsoft.Json.JsonConvert); }","JsonProbe");
            Assert.Null(error);Assert.NotNull(bytes);
        }
        finally {context.Unload();}
    }
    [Fact]
    public void WarningsDoNotInvalidateJsonOrHideFailure()
    {
        using var doc=JsonDocument.Parse(ToolResult.Complete("{\"ok\":false,\"id\":12,\"warnings\":[\"existing\"]}",["native warning"]));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(12,doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(2,doc.RootElement.GetProperty("warnings").GetArrayLength());
        using var array=JsonDocument.Parse(ToolResult.Complete("[1,2]"));
        Assert.Equal(2,array.RootElement.GetProperty("result").GetArrayLength());
    }
    [Fact]
    public void BatchCountsRemainCompatible()
    {
        using var doc=JsonDocument.Parse(ToolResult.Complete("{\"ok\":3,\"failed\":1}"));
        Assert.Equal(3,doc.RootElement.GetProperty("ok").GetInt32());
    }
    [Fact]
    public void RejectsCrossingAndShortContoursBeforeMutation()
    {
        Assert.Throws<ArgumentException>(()=>GeometryPreflight.Contour([new(0,0),new(10,10),new(0,10),new(10,0)],1));
        Assert.Throws<ArgumentException>(()=>GeometryPreflight.Contour([new(0,0),new(0.1,0),new(10,10)],1));
        Assert.Throws<ArgumentException>(()=>GeometryPreflight.Contour([new(double.NaN,0),new(10,0),new(0,10)],1));
    }
    [Fact]
    public void AlignmentIsOptInAndReportsActualShift()
    {
        PlanPoint[] points=[new(0,0),new(100,0.1),new(100,100),new(0,100)];
        Assert.Equal(0,GeometryPreflight.Contour(points,1).MaxShiftMm);
        var result=GeometryPreflight.Contour(points,1,0.2);
        Assert.Equal(0.1,result.MaxShiftMm,8);Assert.Equal(0,result.Points[1].Y);
    }
    [Fact]
    public void HolesCannotTouchCrossOrNest()
    {
        PlanPoint[] outer=[new(0,0),new(100,0),new(100,100),new(0,100)];
        PlanPoint[] hole=[new(10,10),new(20,10),new(20,20),new(10,20)];
        GeometryPreflight.Holes([outer,hole]);
        Assert.Throws<ArgumentException>(()=>GeometryPreflight.Holes([outer,hole,hole]));
        Assert.Throws<ArgumentException>(()=>GeometryPreflight.Holes([outer,[new(0,10),new(20,10),new(20,20)]]));
    }
    [Theory]
    [InlineData("Tower | Level 2")]
    [InlineData("Tower ~ reconstructed")]
    [InlineData("")]
    public void InvalidNamesFailPreflight(string name)=>Assert.Throws<ArgumentException>(()=>GeometryPreflight.Name(name));

    [Fact]
    public void TurnUsageIgnoresCumulativeCountersAndPriorTurns()
    {
        const string id="01a10847-bef5-7f32-af26-ad32c1af1dac";
        var folder=Path.Combine(Path.GetTempPath(),"ClaudeRevit-usage-test-"+Guid.NewGuid());
        try
        {
            var date=DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(id.Replace("-","")[..12],16)).UtcDateTime;
            var session=Path.Combine(folder,"sessions",date.ToString("yyyy"),date.ToString("MM"),date.ToString("dd"));Directory.CreateDirectory(session);
            File.WriteAllLines(Path.Combine(session,"rollout-"+id+".jsonl"),[
                "{\"type\":\"token_usage_record\",\"timestamp\":\"2026-10-04T10:00:00Z\",\"payload\":{\"turn_token_usage\":{\"input_tokens\":9000000}}}",
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"input_tokens\":9000000}}",
                "{\"type\":\"token_usage_record\",\"timestamp\":\"2026-10-04T22:00:00Z\",\"payload\":{\"turn_token_usage\":{\"input_tokens\":400,\"cached_input_tokens\":300,\"output_tokens\":100,\"reasoning_output_tokens\":60}}}"]);
            var usage=CodexUsage.ReadTurn(id,DateTime.Parse("2026-10-04T21:00:00Z").ToUniversalTime(),folder);
            Assert.Equal(new CodexUsage(400,300,100,60,"turn"),usage);
            Assert.Null(CodexUsage.ReadTurn(id,DateTime.Parse("2026-10-04T23:00:00Z").ToUniversalTime(),folder));
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
    }
    [Fact]
    public void ExactDiscoveryAvoidsIrrelevantSchemasAndInventedIdentifiers()
    {
        ToolSearchLogic.ToolInfo[] tools=[new("create_rebar_type","Rebar type.","Rebar",false),new("create_rebar","Create bar.","Rebar",false)];
        Assert.Equal(new[]{"create_rebar_type"},CompactMcpTools.Search(tools,"create_rebar_type"));
        Assert.Empty(CompactMcpTools.Search(tools,"invented_rebar_tool"));
        Assert.Equal(2,CompactMcpTools.Search(tools,"rebar").Count);
    }
    [Fact]
    public void SteelOnlySeedDoesNotReceiveAnImpossibleRebarGrade()
    {
        var task=BenchmarkTasks.All.Single(t=>t.Id=="R1");
        Assert.Contains("concrete",BenchmarkEligibility.SkipReason(task,"{\"is_family_document\":false,\"resources\":{\"concrete_column_type_ids\":[],\"valid_rebar_column_ids\":[]}}")!);
        Assert.Null(BenchmarkEligibility.SkipReason(task,"{\"is_family_document\":false,\"resources\":{\"concrete_column_type_ids\":[42],\"valid_rebar_column_ids\":[]}}"));
    }
}
