using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class ConnectionAndSpdsTests
{
    private static ConnectionPart Part(string key,string? parent=null)=>new(){Key=key,ElementId=1,Role="member",RelativeTo=parent};
    [Fact] public void ConnectionDependenciesAreOrderedAndCyclesRejected()
    {
        var spec=new ConnectionSpec {Parts=[Part("bolt","plate"),Part("plate","beam"),Part("beam")]};
        Assert.Equal(new[]{"beam","plate","bolt"},spec.OrderedParts().Select(p=>p.Key));
        spec.Parts[2].RelativeTo="bolt";Assert.Throws<ArgumentException>(()=>spec.OrderedParts());
    }
    [Fact] public void InvalidFramesAndMissingPartsFailBeforeRevit()
    {
        var p=Part("a");p.AxisX=[1,0,0];p.AxisY=[1,1,0];
        Assert.Throws<ArgumentException>(()=>new ConnectionSpec {Parts=[p]}.OrderedParts());
        Assert.Throws<ArgumentException>(()=>new ConnectionSpec {Parts=[Part("a"),Part("a")]}.OrderedParts());
        Assert.Throws<ArgumentException>(()=>new ConnectionSpec {Parts=[Part("a","missing")]}.OrderedParts());
        Assert.Throws<ArgumentException>(()=>new ConnectionSpec {Parts=[Part("a"),Part("b")],Rules=[new(){Kind="contact",A="a",B="b"}]}.OrderedParts());
    }
    [Fact] public void SnakeCaseRoundTripRetainsSizeAndConstraintIntent()
    {
        var spec=JsonSerializer.Deserialize<ConnectionSpec>("""{"parts":[{"key":"bolt","role":"bolt","family_type_id":123,"point_mm":[1,2,3],"parameters":[{"name":"Length","unit":"mm","value":100}]}],"rules":[]}""",ConnectionSpec.Options)!;
        Assert.Single(spec.OrderedParts());Assert.Equal(100,spec.Parts[0].Parameters[0].Value.GetInt32());
        Assert.Equal(123,JsonSerializer.Deserialize<ConnectionSpec>(JsonSerializer.Serialize(spec,ConnectionSpec.Options),ConnectionSpec.Options)!.Parts[0].FamilyTypeId);
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<ConnectionSpec>("{\"partz\":[]}",ConnectionSpec.Options));
    }
    [Fact] public void BoltAxisChecksDistinguishOppositeAxesFromCrossingAxes()
    {
        var same=ConnectionMath.Axes([0,0,0],[0,0,1],[0,0,20],[0,0,-1]);Assert.Equal(0,same.DistanceMm);Assert.Equal(0,same.AngleDeg);
        var offset=ConnectionMath.Axes([0,0,0],[0,0,1],[3,4,20],[0,0,1]);Assert.Equal(5,offset.DistanceMm,8);
        var crossing=ConnectionMath.Axes([0,0,0],[0,0,1],[0,0,0],[1,0,0]);Assert.Equal(90,crossing.AngleDeg,8);
    }
    [Fact] public void ForbiddenZoneIncludesBoltRadiusAtSpliceEdge()
    {
        var plane=new ConnectionPlane {OriginMm=[5000,0,0],Normal=[2,0,0],HalfWidthMm=10};
        Assert.True(ConnectionMath.InForbiddenZone([5020,0,0],plane,12,1));
        Assert.False(ConnectionMath.InForbiddenZone([5080,0,0],plane,12,1));
    }
    [Fact] public void ContinuousTrussSharesIntermediateSupportAndMaintainsGlobalDiagonals()
    {
        var plan=TrussLayout.Build([40000,60000],4740,6000,5000,11700,1,5850);
        Assert.Equal(new double[]{0,40000,100000},plan.SupportStationsMm);
        Assert.Equal(84,plan.Joints.Count);Assert.Equal(plan.Joints.Count,plan.Joints.Select(j=>j.Key).Distinct().Count());
        Assert.Equal(4,plan.Joints.Count(j=>j.PointMm[0]==40000));
        var diagonal=plan.Members.Where(m=>m.Role=="diagonal").ToArray();Assert.Equal(40,diagonal.Length);
        Assert.All(diagonal,m=>Assert.True(m.LengthMm>5000));
        var lower=plan.Splices.Where(s=>s.Chord=="lower").Select(s=>s.Xmm).SkipLast(1).ToHashSet();
        Assert.DoesNotContain(plan.Splices.Where(s=>s.Chord=="upper").Select(s=>s.Xmm).SkipLast(1),x=>lower.Contains(x));
        Assert.Equal(100000,plan.Splices.Where(s=>s.Chord=="lower").Sum(s=>s.SegmentLengthMm),6);
    }
    [Theory] [InlineData("rectangle",200,300,10,10,60000)] [InlineData("box",200,300,10,20,13200)] [InlineData("i",200,300,10,20,10600)]
    public void SectionPolygonAreaMatchesAnalyticalArea(string shape,double w,double h,double web,double flange,double area)
    {
        double polygon=0;foreach(var loop in SectionProfile.Loops(shape,w,h,web,flange))for(int i=0;i<loop.Length;i++){var a=loop[i];var b=loop[(i+1)%loop.Length];polygon+=(a[0]*b[1]-a[1]*b[0])/2;}
        Assert.Equal(area,SectionProfile.Area(shape,w,h,web,flange),6);Assert.Equal(area,polygon,6);
        Assert.NotEqual(area,SectionProfile.Area(shape,w*1.2,h,web,flange));
    }
    [Fact] public void CollapsedSectionAndNonfiniteLayoutsAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>SectionProfile.Area("box",20,100,10,10));
        Assert.Throws<ArgumentException>(()=>SectionProfile.Area("i",200,double.NaN,10,10));
        Assert.Throws<ArgumentException>(()=>TrussLayout.Build([1000],1000,1000,0,11700,1,5000));
    }
    private static SpdsItem Steel(long id,double mass,string size="20",string group="Балки")=>new(){SourceId=id,MassKg=mass,Profile="Двутавр",Grade="С345",Size=size,Group=group};
    [Fact] public void SteelRollupSumsBeforeRoundingAndKeepsGroupAndGradeTotals()
    {
        var t=SpdsTables.Build("steel_rollup",new[]{Steel(1,49),Steel(2,49),Steel(3,302,"100","Колонны")});
        Assert.Equal(400,t.TotalMassKg);Assert.Equal("20",t.Rows.First(r=>r.Kind=="data").Cells[2]);
        Assert.Equal("0,1",t.Rows.First(r=>r.Kind=="data").Cells[^1]);
        var total=t.Rows.Single(r=>r.Kind=="total");Assert.Equal("0,4",total.Cells[^1]);Assert.Equal(new long[]{1,2,3},total.SourceIds);
        Assert.Equal(7,t.Columns.Count);Assert.All(t.Rows,r=>Assert.Equal(t.Columns.Count,r.Cells.Length));
    }
    [Fact] public void MissingGradeAndDuplicateSourcesCannotProduceMisleadingTotals()
    {
        var s=Steel(1,12);s.Grade="";Assert.Throws<ArgumentException>(()=>SpdsTables.Build("steel_rollup",new[]{s}));
        Assert.Throws<ArgumentException>(()=>SpdsTables.Build("steel_rollup",new[]{Steel(1,12),Steel(1,12)}));
        Assert.Throws<ArgumentException>(()=>SpdsTables.Build("steel_rollup",new[]{Steel(1,double.NaN)}));
    }
    [Fact] public void TimberQuantitiesAreGroupedOnlyByMatchingUnitsAndSourceMeaning()
    {
        var t=SpdsTables.Build("timber_materials",new SpdsItem[]{new(){SourceId=1,Name="Брус",Unit="м³",Amount=.25,Quantity=2},new(){SourceId=2,Name="Брус",Unit="м³",Amount=.75},new(){SourceId=3,Name="Брус",Unit="шт.",Amount=3}});
        Assert.Equal(2,t.Rows.Count);Assert.Contains(t.Rows,r=>r.Cells[1]=="м³"&&r.Cells[2]=="1,25");
        Assert.Equal(185,t.Columns.Sum(c=>c.WidthMm));
        Assert.Equal(185,SpdsTables.Columns("scheme_specification").Sum(c=>c.WidthMm));
    }
    [Fact] public void ErrorExtractionRetainsCompilationReason()
    {Assert.Equal("bad snippet",ToolResult.ErrorMessage(ToolResult.Failure("compile","bad snippet")));Assert.Null(ToolResult.ErrorMessage("[1,2]"));}
    [Fact] public void LiveSteelValuesRecalculateQuantityAndGroupWithoutEarlyRounding()
    {
        var i=Steel(1,49);i.Quantity=2;
        var first=SpdsTables.ModelValues("steel_rollup",i,["Балки","Колонны"]);
        Assert.Equal(98d,first["total"]);Assert.Equal(98d,first["group_0"]);Assert.Equal(0d,first["group_1"]);
        var key=first["row_key"];i.Group="Колонны";i.Quantity=3;i.MassKg=53.2;
        var after=SpdsTables.ModelValues("steel_rollup",i,["Балки","Колонны"]);
        Assert.Equal(key,after["row_key"]);Assert.Equal(0d,after["group_0"]);Assert.Equal(159.6,(double)after["group_1"],8);
        i.Group="Новая группа";Assert.Throws<ArgumentException>(()=>SpdsTables.ModelValues("steel_rollup",i,["Балки","Колонны"]));
    }
    [Fact] public void LiveTimberGroupingKeepsUnitMassIdentityAndRecalculatesMaterialAmounts()
    {
        var a=new SpdsItem{SourceId=1,Mark="Д1",Name="Брус",MassKg=25,Amount=.1,Unit="м³",Quantity=2};
        var b=new SpdsItem{SourceId=2,Mark="Д1",Name="Брус",MassKg=30,Amount=.1,Unit="м³",Quantity=2};
        Assert.NotEqual(SpdsTables.RowKey("timber_elements",a),SpdsTables.RowKey("timber_elements",b));
        Assert.Equal(SpdsTables.RowKey("timber_materials",a),SpdsTables.RowKey("timber_materials",b));
        Assert.Equal(.2d,SpdsTables.ModelValues("timber_materials",a,[])["quantity"]);
        a.Amount=.25;Assert.Equal(.5d,SpdsTables.ModelValues("timber_materials",a,[])["quantity"]);
        b.Unit="м";Assert.NotEqual(SpdsTables.RowKey("timber_materials",a),SpdsTables.RowKey("timber_materials",b));
    }
    [Theory] [InlineData("{\"parts\":null}")] [InlineData("{\"parts\":[null]}")] [InlineData("{\"parts\":[{\"key\":\"a\",\"element_id\":1,\"parameters\":null}]}")]
    public void NullNodeContractsFailValidationCleanly(string json)=>Assert.Throws<ArgumentException>(()=>JsonSerializer.Deserialize<ConnectionSpec>(json,ConnectionSpec.Options)!.OrderedParts());
}
