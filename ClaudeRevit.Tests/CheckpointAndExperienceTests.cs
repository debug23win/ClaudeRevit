using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;
namespace ClaudeRevit.Tests;

public class CheckpointAndExperienceTests
{
    [Fact] public void ReferenceResolutionKeepsNativeOutputIdsAndRejectsForwardDependencies()
    {
        var plan=JsonSerializer.Deserialize<CheckpointPlan>("""{"steps":[{"key":"level","tool":"create_level","arguments":{"name":"L","elevation_mm":3500}},{"key":"wall","tool":"create_wall","arguments":{"level_id":{"from_step":"level","json_pointer":"/id"},"name":"A"}}]}""",CheckpointPlan.Options)!;
        plan.Validate();
        var args=CheckpointPlan.Resolve(plan.Steps[1].Arguments,new Dictionary<string,JsonElement>{{"level",JsonSerializer.SerializeToElement(new{id=42})}});
        Assert.Equal(42,args.GetProperty("level_id").GetInt32());Assert.Equal("A",args.GetProperty("name").GetString());
        plan.Steps.Reverse();Assert.Throws<ArgumentException>(plan.Validate);
    }
    [Fact] public void PlanChangesCannotReuseOldCompletionHash()
    {
        var plan=new CheckpointPlan{Steps=[new(){Key="a",Tool="create_level",Arguments=JsonSerializer.SerializeToElement(new{name="A"})}]};var hash=plan.Hash();
        plan.Steps[0].Arguments=JsonSerializer.SerializeToElement(new{name="B"});Assert.NotEqual(hash,plan.Hash());
        plan.Steps.Add(plan.Steps[0]);Assert.Throws<ArgumentException>(plan.Validate);
    }
    [Fact] public void JsonPointerEscapesAndArrayReferencesAreResolved()
    {
        var args=JsonSerializer.Deserialize<JsonElement>("""{"id":{"from_step":"a","json_pointer":"/x~1y/1/~0value"}}""");
        var results=new Dictionary<string,JsonElement>{{"a",JsonSerializer.Deserialize<JsonElement>("""{"x/y":[{}, {"~value":88}]}""")}};
        Assert.Equal(88,CheckpointPlan.Resolve(args,results).GetProperty("id").GetInt32());
    }
    [Fact] public void NestingPoliciesAvoidParentChildDoubleCountsAcrossThreeLevels()
    {
        (string,string?)[] pairs=[("assembly",null),("plate","assembly"),("bolt","plate"),("beam",null)];
        Assert.Equal(new[]{"assembly","beam"},NestingSelection.Select(pairs,"parents_only").Order());
        Assert.Equal(new[]{"beam","bolt"},NestingSelection.Select(pairs,"children_only").Order());
        Assert.Equal(4,NestingSelection.Select(pairs,"explicit").Count);
        Assert.Throws<ArgumentException>(()=>NestingSelection.Select([("a","b"),("b","a")],"parents_only"));
    }
    [Fact] public void FailedAndWrongEnvironmentExperienceNeverBecomesVerifiedAdvice()
    {
        var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));var path=Path.Combine(dir,"records.json");
        try
        {
            VerifiedExperience Record(string id,string status,string version="2026")=>new(id,"hash","complete code","execute_csharp",version,"template","2026-10-05T00:00:00Z",1,[new("geometry",status,"")],new Dictionary<string,string>());
            VerifiedExperienceStore.Save(Record("failed","failed"),path);VerifiedExperienceStore.Save(Record("unknown","incomplete"),path);VerifiedExperienceStore.Save(Record("old","passed","2025"),path);VerifiedExperienceStore.Save(Record("good","passed"),path);
            Assert.Equal(4,VerifiedExperienceStore.Read(path).Count);
            Assert.Equal("good",Assert.Single(VerifiedExperienceStore.Compatible("2026","template",path)).RunId);
            Assert.Empty(VerifiedExperienceStore.Compatible("2026","another template",path));
            var digest=VerifiedExperienceStore.Digest(VerifiedExperienceStore.Read(path));Assert.Contains("Choose a better approach freely",digest);Assert.DoesNotContain("complete code",digest);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void DependentNodeDimensionsOrderSourcesAndRejectCycles()
    {
        var dependent=new ConnectionPart{Key="plate",ElementId=1,Parameters=[new(){Name="Width",Unit="mm",ValueFrom=new(){PartKey="beam",Name="b",Scope="type",OffsetMm=20}}]};
        var root=new ConnectionPart{Key="beam",ElementId=2};var spec=new ConnectionSpec{Parts=[dependent,root]};
        Assert.Equal(new[]{"beam","plate"},spec.OrderedParts().Select(p=>p.Key));
        var roundtrip=JsonSerializer.Deserialize<ConnectionSpec>(JsonSerializer.Serialize(spec,ConnectionSpec.Options),ConnectionSpec.Options)!;Assert.Equal(20,roundtrip.Parts[0].Parameters[0].ValueFrom!.OffsetMm);
        root.RelativeTo="plate";Assert.Throws<ArgumentException>(()=>spec.OrderedParts());
    }
    [Theory] [InlineData("channel",200,300,10,20,10600)] [InlineData("angle",200,300,10,20,6800)] [InlineData("timber_pair",200,300,20,10,54000)]
    public void NewSectionsPreserveAnalyticalAreaAndActualClosedPolygonArea(string shape,double w,double h,double t,double f,double expected)
    {
        double area=0;foreach(var loop in SectionProfile.Loops(shape,w,h,t,f))for(int i=0;i<loop.Length;i++){var a=loop[i];var b=loop[(i+1)%loop.Length];area+=(a[0]*b[1]-a[1]*b[0])/2;}
        Assert.Equal(expected,area,6);Assert.Equal(area,SectionProfile.Area(shape,w,h,t,f),6);
    }
}
