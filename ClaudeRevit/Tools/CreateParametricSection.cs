using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public sealed class CreateParametricSection:IRevitTool
{
    public string Name=>"create_parametric_section";
    public string Description=>"In an active ordinary RFA, create a native rectangular/box/I extrusion with real labeled sketch constraints, formulas and length association. All section dimensions flex independently at 0.8x/1.2x and measured solid volume must match the analytical section area; any failure rolls back. Default preview=true. Reuses compatible named length parameters; never fabricates shared GUIDs. Preserves template category; use loaded native structural profiles first. Does not make a steel connection or automatically bind beam placement length.";
    public bool RequiresTransaction=>false;
    public bool MutatesWithoutTransaction=>true;
    public bool RequiresNoTurnGroup=>true;
    public bool InvalidatesCatalog=>true;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new()
    {
        ["shape"]=NativeToolUtil.Field("string","rectangle, box or i."),["width_mm"]=NativeToolUtil.Field("number","Section width."),["height_mm"]=NativeToolUtil.Field("number","Section height."),
        ["web_mm"]=NativeToolUtil.Field("number","Box side wall/I web thickness; default 10."),["flange_mm"]=NativeToolUtil.Field("number","Box top/bottom wall/I flange thickness; default 10."),
        ["length_mm"]=NativeToolUtil.Field("number","Extrusion length; default 1000."),["parameter_prefix"]=NativeToolUtil.Field("string","Default Section_. Creates Width, Height, Web, Flange, Length and derived coordinate parameters."),
        ["is_instance"]=NativeToolUtil.Field("boolean","Default true. Driver parameter instance/type mode."),["plane_normal"]=NativeToolUtil.Any("Default Z; use X for a YZ section. Active family view must be parallel to sketch plane."),
        ["preview"]=NativeToolUtil.Field("boolean","Default true; false commits after independent flex checks.")
    },"shape","width_mm","height_mm");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var fm=FamilyEditorUtil.Manager(doc);var view=doc.ActiveView;
        var shape=ToolInput.RequiredString(input,"shape");var w=input["width_mm"].GetDouble();var h=input["height_mm"].GetDouble();var t=ToolInput.OptionalDouble(input,"web_mm")??10;var f=ToolInput.OptionalDouble(input,"flange_mm")??10;var length=ToolInput.OptionalDouble(input,"length_mm")??1000;
        SectionProfile.Area(shape,w,h,t,f);if(!double.IsFinite(length)||length<=0)throw new ToolInputException("length_mm must be positive.");
        var prefix=NativeToolUtil.Text(input,"parameter_prefix","Section_");Services.GeometryPreflight.Name(prefix+"Width");
        bool instance=!input.TryGetValue("is_instance",out var ins)||ins.GetBoolean();bool preview=NativeToolUtil.Preview(input);
        var n=input.TryGetValue("plane_normal",out var norm)?NativeToolUtil.Point(norm,false).Normalize():XYZ.BasisZ;
        var u=Math.Abs(n.DotProduct(XYZ.BasisZ))>.99?XYZ.BasisX:XYZ.BasisZ.CrossProduct(n).Normalize();var v=n.CrossProduct(u).Normalize();
        if(view is View3D||Math.Abs(view.ViewDirection.Normalize().DotProduct(n))<.99999)throw new ToolInputException("Activate a family view parallel to the section sketch plane.");
        var parameters=new Dictionary<string,FamilyParameter>();Extrusion? form=null;var flex=new List<object>();
        var (result,warnings)=NativeToolUtil.Commit(doc,"Claude: parametric "+shape+" section",preview,()=>
        {
            if(fm.CurrentType==null)fm.NewType("Section default");
            FamilyParameter Add(string suffix,double value,string? formula=null)
            {
                var name=prefix+suffix;var p=FamilyEditorUtil.Find(fm,name);
                if(p==null)p=fm.AddParameter(name,GroupTypeId.Geometry,SpecTypeId.Length,instance);
                else if(!FamilyEditorUtil.IsLength(p)||p.IsInstance!=instance||p.IsDeterminedByFormula)throw new ToolInputException("Existing driver is incompatible: "+name);
                fm.Set(p,value/Units.MmPerFoot);if(formula!=null)fm.SetFormula(p,formula);parameters[suffix]=p;return p;
            }
            Add("Width",w);Add("Height",h);Add("Length",length);
            if(shape!="rectangle") {Add("Web",t);Add("Flange",f);}
            double left=shape=="box"?t:(w-t)/2,right=shape=="box"?w-t:(w+t)/2;
            if(shape!="rectangle")
            {Add("InnerLeft",left,shape=="box"?prefix+"Web":"("+prefix+"Width - "+prefix+"Web) / 2");Add("InnerRight",right,shape=="box"?prefix+"Width - "+prefix+"Web":"("+prefix+"Width + "+prefix+"Web) / 2");Add("InnerTop",h-f,prefix+"Height - "+prefix+"Flange");}
            XYZ P(double x,double y)=>(u*x+v*y)/Units.MmPerFoot;
            using var plane=Plane.CreateByOriginAndBasis(XYZ.Zero,u,v);var sp=SketchPlane.Create(doc,plane);
            var loops=new CurveArrArray();
            foreach(var polygon in SectionProfile.Loops(shape,w,h,t,f))
            {var curves=new CurveArray();for(int i=0;i<polygon.Length;i++)curves.Append(Line.CreateBound(P(polygon[i][0],polygon[i][1]),P(polygon[(i+1)%polygon.Length][0],polygon[(i+1)%polygon.Length][1])));loops.Append(curves);}
            form=doc.FamilyCreate.NewExtrusion(true,loops,sp,length/Units.MmPerFoot);doc.Regenerate();
            fm.AssociateElementParameterToFamilyParameter(form.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM),parameters["Length"]);
            var edges=form.Sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>().ToArray();
            void Drive(bool horizontal,Dictionary<double,string?> positions)
            {
                var axis=horizontal?u:v;var tangent=horizontal?v:u;ReferencePlane? origin=null;
                foreach(var pair in positions.OrderBy(p=>p.Key))
                {
                    var p=axis*pair.Key/Units.MmPerFoot;var rp=doc.FamilyCreate.NewReferencePlane(p-tangent,p+tangent,n,view);rp.Name=prefix+(horizontal?"X":"Y")+pair.Key+" "+form.Id.Value;
                    if(pair.Key==0){rp.Pinned=true;origin=rp;}
                    else
                    {
                        var refs=new ReferenceArray();refs.Append(origin!.GetReference());refs.Append(rp.GetReference());
                        var shift=-tangent*(100/Units.MmPerFoot);var dimension=doc.FamilyCreate.NewDimension(view,Line.CreateBound(shift,p+shift),refs);dimension.FamilyLabel=parameters[pair.Value!];
                    }
                    foreach(var edge in edges.Where(e=>e.GeometryCurve is Line && Math.Abs(e.GeometryCurve.GetEndPoint(0).DotProduct(axis)-pair.Key/Units.MmPerFoot)<1e-7&&Math.Abs(e.GeometryCurve.GetEndPoint(1).DotProduct(axis)-pair.Key/Units.MmPerFoot)<1e-7))
                        doc.FamilyCreate.NewAlignment(view,rp.GetReference(),edge.GeometryCurve.Reference);
                }
            }
            var xs=new Dictionary<double,string?>{{0,null},{w,"Width"}};var ys=new Dictionary<double,string?>{{0,null},{h,"Height"}};
            if(shape!="rectangle"){xs[left]="InnerLeft";xs[right]="InnerRight";ys[f]="Flange";ys[h-f]="InnerTop";}
            Drive(true,xs);Drive(false,ys);doc.Regenerate();
            return new {form_id=preview?(long?)null:form.Id.Value,shape,category=doc.OwnerFamily.FamilyCategory?.Name,drivers=parameters.Where(p=>p.Key is "Width" or "Height" or "Length" or "Web" or "Flange").Select(p=>new {name=p.Value.Definition.Name,is_instance=p.Value.IsInstance}).ToArray(),axis=NativeToolUtil.Vector(n)};
        },()=>
        {
            double Volume() {var e=doc.GetElement(form!.Id)??throw new InvalidOperationException("Section disappeared after commit.");return ConnectionNodes.Solids(e).Sum(s=>s.Volume)*Math.Pow(Units.MmPerFoot,3);}
            var baseline=new Dictionary<string,double>{{"Width",w},{"Height",h},{"Web",t},{"Flange",f},{"Length",length}};
            void Verify(Dictionary<string,double> dims,string scenario)
            {var expected=SectionProfile.Area(shape,dims["Width"],dims["Height"],dims["Web"],dims["Flange"])*dims["Length"];var actual=Volume();if(Math.Abs(actual-expected)>Math.Max(1,expected*1e-5))throw new ToolInputException($"Section flex '{scenario}' did not change actual geometry as required; update rolled back.");flex.Add(new {scenario,measured_volume_mm3=actual,expected_volume_mm3=expected,passed=true});}
            Verify(baseline,"initial");
            foreach(var key in parameters.Keys.Where(k=>baseline.ContainsKey(k)).ToArray())foreach(var factor in new[]{.8,1.2})
            {
                ToolContext.ThrowIfCancelled();var dims=new Dictionary<string,double>(baseline){[key]=baseline[key]*factor};
                // Refuse shapes whose requested nominal proportions cannot sustain the test envelope.
                SectionProfile.Area(shape,dims["Width"],dims["Height"],dims["Web"],dims["Flange"]);
                NativeToolUtil.Commit(doc,"Claude: section flex "+key,true,()=>{fm.Set(parameters[key],dims[key]/Units.MmPerFoot);doc.Regenerate();return true;},()=>Verify(dims,key+" x "+factor));
            }
            Verify(baseline,"restored");
        });
        return Services.Json.Serialize(new {preview,result,flex,warnings,limitation="Constrained profile geometry only. Existing beam template length/material/reference-plane behavior must be checked before loading as a structural beam."});
    }
}
