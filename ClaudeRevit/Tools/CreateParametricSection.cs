using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public sealed class CreateParametricSection:IRevitTool
{
    public string Name=>"create_parametric_section";
    public string Description=>"Create a native constrained rectangle, box, I, channel, angle, circular tube or two-piece timber section in an ordinary RFA. Real labeled dimensions, material association, analytical area and optional explicit-density mass formula. Independent 0.8x/1.2x flex verifies actual dimensions AND volume; any failure rolls back. Circular tubes use arcs, not polygon meshes. Optionally bind Length to an inspected template placement-length parameter. Default preview=true. Preserves the template category; loaded native structural profiles are preferred. Verify structural placement in a project before production.";
    public bool RequiresTransaction=>false;
    public bool MutatesWithoutTransaction=>true;
    public bool RequiresNoTurnGroup=>true;
    public bool InvalidatesCatalog=>true;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new()
    {
        ["shape"]=NativeToolUtil.Field("string","rectangle, box, i, channel, angle, tube or timber_pair."),["width_mm"]=NativeToolUtil.Field("number","Overall section width; tube diameter."),["height_mm"]=NativeToolUtil.Field("number","Overall section height; tube must equal diameter."),
        ["web_mm"]=NativeToolUtil.Field("number","Box side wall/I web thickness; default 10."),["flange_mm"]=NativeToolUtil.Field("number","Box top/bottom wall/I flange thickness; default 10."),
        ["length_mm"]=NativeToolUtil.Field("number","Extrusion length; default 1000."),["parameter_prefix"]=NativeToolUtil.Field("string","Default Section_. Creates Width, Height, Web, Flange, Length and derived coordinate parameters."),
        ["is_instance"]=NativeToolUtil.Field("boolean","Default true. Driver parameter instance/type mode."),["plane_normal"]=NativeToolUtil.Any("Default Z; use X for a YZ section. Active family view must be parallel to sketch plane."),
        ["view_id"]=NativeToolUtil.Field("integer","Optional native family plan/elevation view for sketch constraints. Defaults to the active view; background RFAs without an active view use a parallel plan/elevation."),
        ["gap_mm"]=NativeToolUtil.Field("number","timber_pair: gap between two rectangular timber pieces; overrides web_mm."),
        ["placement_length_parameter"]=NativeToolUtil.Field("string","Optional EXISTING instance Length parameter from the beam template; Section_Length uses its formula. Discover it first; do not invent a name. Project placement still needs an acceptance test."),
        ["material_id"]=NativeToolUtil.Field("integer","Optional Material in this family document. Always creates/associates a real Material family parameter."),
        ["density_kg_m3"]=NativeToolUtil.Field("number","Optional explicitly agreed positive material density. Creates physical Density and Mass=Area*Length*Density parameters; density is not inferred from a material name."),
        ["preview"]=NativeToolUtil.Field("boolean","Default true; false commits after independent flex checks.")
    },"shape","width_mm","height_mm");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var fm=FamilyEditorUtil.Manager(doc);
        var view=input.TryGetValue("view_id",out var viewId)?doc.GetElement(new ElementId(viewId.GetInt64())) as View:doc.ActiveView;
        var shape=ToolInput.RequiredString(input,"shape");var w=ToolInput.RequiredDouble(input, "width_mm");var h=ToolInput.RequiredDouble(input, "height_mm");var t=ToolInput.OptionalDouble(input,"web_mm")??10;var f=ToolInput.OptionalDouble(input,"flange_mm")??10;var length=ToolInput.OptionalDouble(input,"length_mm")??1000;
        if(shape=="timber_pair")t=ToolInput.OptionalDouble(input,"gap_mm")??t;
        var placementName=NativeToolUtil.Text(input,"placement_length_parameter");var placement=placementName.Length==0?null:FamilyEditorUtil.Require(fm,placementName);
        if(placement!=null){if(!FamilyEditorUtil.IsLength(placement)||!placement.IsInstance)throw new ToolInputException("Placement length must be an existing instance Length parameter.");if(fm.CurrentType==null)throw new ToolInputException("Set a nominal family type before binding template length.");length=fm.CurrentType.AsDouble(placement)!.Value*Units.MmPerFoot;}
        var density=ToolInput.OptionalDouble(input,"density_kg_m3");if(density.HasValue&&(!double.IsFinite(density.Value)||density<=0))throw new ToolInputException("density_kg_m3 must be finite and positive.");
        SectionProfile.Area(shape,w,h,t,f);if(!double.IsFinite(length)||length<=0)throw new ToolInputException("length_mm must be positive.");
        var prefix=NativeToolUtil.Text(input,"parameter_prefix","Section_");Services.GeometryPreflight.Name(prefix+"Width");
        bool instance=!input.TryGetValue("is_instance",out var ins)||ins.GetBoolean();bool preview=NativeToolUtil.Preview(input);
        var n=input.TryGetValue("plane_normal",out var norm)?NativeToolUtil.Point(norm,false).Normalize():XYZ.BasisZ;
        var u=Math.Abs(n.DotProduct(XYZ.BasisZ))>.99?XYZ.BasisX:XYZ.BasisZ.CrossProduct(n).Normalize();var v=n.CrossProduct(u).Normalize();
        if(view==null&&!input.ContainsKey("view_id"))view=new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(candidate=>!candidate.IsTemplate&&candidate.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Elevation or ViewType.Section)
            .FirstOrDefault(candidate=>Math.Abs(candidate.ViewDirection.Normalize().DotProduct(n))>=.99999);
        if(view==null||view.IsTemplate||view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Elevation or ViewType.Section)||Math.Abs(view.ViewDirection.Normalize().DotProduct(n))<.99999)throw new ToolInputException("Choose a family plan/elevation view parallel to the section sketch plane.");
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
            Add("Width",w);Add("Height",h,shape=="tube"?prefix+"Width":null);Add("Length",length,placementName.Length>0?placementName:null);
            string thickness=prefix+(shape=="timber_pair"?"Gap":"Web");
            if(shape!="rectangle"){var web=Add(shape=="timber_pair"?"Gap":"Web",t);parameters["Web"]=web;}
            if(shape is "box" or "i" or "channel" or "angle")Add("Flange",f);
            double left=shape is "box" or "channel" or "angle"?t:(w-t)/2,right=shape=="box"?w-t:(w+t)/2;
            if(shape is "box" or "i" or "channel" or "angle" or "timber_pair")
            {
                Add("InnerLeft",left,shape is "box" or "channel" or "angle"?thickness:"("+prefix+"Width - "+thickness+") / 2");
                if(shape is "box" or "i" or "timber_pair")Add("InnerRight",right,shape=="box"?prefix+"Width - "+thickness:"("+prefix+"Width + "+thickness+") / 2");
                if(shape is "box" or "i" or "channel")Add("InnerTop",h-f,prefix+"Height - "+prefix+"Flange");
            }
            XYZ P(double x,double y)=>(u*x+v*y)/Units.MmPerFoot;
            using var plane=Plane.CreateByOriginAndBasis(XYZ.Zero,u,v);var sp=SketchPlane.Create(doc,plane);
            var loops=new CurveArrArray();
            if(shape=="tube")
            {
                Add("Radius",w/2,prefix+"Width / 2");Add("InnerRadius",w/2-t,prefix+"Radius - "+thickness);
                foreach(var radius in new[]{w/2,w/2-t}){var curves=new CurveArray();curves.Append(Arc.Create(XYZ.Zero,radius/Units.MmPerFoot,0,Math.PI,u,v));curves.Append(Arc.Create(XYZ.Zero,radius/Units.MmPerFoot,Math.PI,2*Math.PI,u,v));loops.Append(curves);}
            }
            else foreach(var polygon in SectionProfile.Loops(shape,w,h,t,f))
            {var curves=new CurveArray();for(int i=0;i<polygon.Length;i++)curves.Append(Line.CreateBound(P(polygon[i][0],polygon[i][1]),P(polygon[(i+1)%polygon.Length][0],polygon[(i+1)%polygon.Length][1])));loops.Append(curves);}
            form=doc.FamilyCreate.NewExtrusion(true,loops,sp,length/Units.MmPerFoot);doc.Regenerate();
            fm.AssociateElementParameterToFamilyParameter(form.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM),parameters["Length"]);
            var material=FamilyEditorUtil.Find(fm,prefix+"Material")??fm.AddParameter(prefix+"Material",GroupTypeId.Materials,SpecTypeId.Reference.Material,instance);
            if(material.Definition.GetDataType()!=SpecTypeId.Reference.Material)throw new ToolInputException("Existing Material parameter is incompatible.");
            fm.AssociateElementParameterToFamilyParameter(form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM),material);
            if(input.TryGetValue("material_id",out var mat)){var id=new ElementId(mat.GetInt64());if(doc.GetElement(id) is not Material)throw new ToolInputException("material_id must be a Material in this RFA.");fm.Set(material,id);}
            string W=prefix+"Width",H=prefix+"Height",T=thickness,F=prefix+"Flange";
            var area=fm.AddParameter(prefix+"Area",GroupTypeId.Geometry,SpecTypeId.Area,instance);
            fm.SetFormula(area,shape switch {"rectangle"=>$"{W} * {H}","box"=>$"{W} * {H} - ({W} - 2 * {T}) * ({H} - 2 * {F})","i"=>$"2 * {W} * {F} + ({H} - 2 * {F}) * {T}","channel"=>$"{H} * {T} + 2 * ({W} - {T}) * {F}","angle"=>$"{H} * {T} + ({W} - {T}) * {F}","timber_pair"=>$"({W} - {T}) * {H}",_=>$"3.141592653589793 * ({W} * {W} - ({W} - 2 * {T}) * ({W} - 2 * {T})) / 4"});
            if(density.HasValue){var dp=fm.AddParameter(prefix+"Density",GroupTypeId.Materials,SpecTypeId.MassDensity,instance);fm.Set(dp,UnitUtils.ConvertToInternalUnits(density.Value,UnitTypeId.KilogramsPerCubicMeter));var mass=fm.AddParameter(prefix+"Mass",GroupTypeId.Data,SpecTypeId.Mass,instance);fm.SetFormula(mass,prefix+"Area * "+prefix+"Length * "+prefix+"Density");}
            var edges=form.Sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>().ToArray();
            void Drive(bool horizontal,Dictionary<double,string?> positions)
            {
                var axis=horizontal?u:v;var tangent=horizontal?v:u;ReferencePlane? origin=null;
                foreach(var pair in positions.OrderBy(p=>p.Key))
                {
                    var p=axis*pair.Key/Units.MmPerFoot;var rp=doc.FamilyCreate.NewReferencePlane(p-tangent,p+tangent,n,view);rp.Name=prefix+(horizontal?"X":"Y")+pair.Key+" "+form.Id.Value;
                    doc.Regenerate();
                    if(pair.Key==0){rp.Pinned=true;origin=rp;}
                    else
                    {
                        var refs=new ReferenceArray();refs.Append(origin!.GetReference());refs.Append(rp.GetReference());
                        var shift=-tangent*(100/Units.MmPerFoot);var dimension=doc.FamilyCreate.NewDimension(view,Line.CreateBound(shift,p+shift),refs);dimension.FamilyLabel=parameters[pair.Value!];
                        doc.Regenerate();
                    }
                    foreach(var edge in edges.Where(e=>e.GeometryCurve is Line && Math.Abs(e.GeometryCurve.GetEndPoint(0).DotProduct(axis)-pair.Key/Units.MmPerFoot)<1e-7&&Math.Abs(e.GeometryCurve.GetEndPoint(1).DotProduct(axis)-pair.Key/Units.MmPerFoot)<1e-7))
                        doc.FamilyCreate.NewAlignment(view,rp.GetReference(),edge.GeometryCurve.Reference);
                }
            }
            if(shape=="tube")
            {
                foreach(var edge in edges.Where(e=>e.GeometryCurve is Arc))
                {var arc=(Arc)edge.GeometryCurve;var label=Math.Abs(arc.Radius*Units.MmPerFoot-w/2)<1e-5?"Radius":"InnerRadius";doc.FamilyCreate.NewRadialDimension(view,arc.Reference,P(w,w)).FamilyLabel=parameters[label];}
                // Nominal arc centres are fixed by orthogonal centre/end-point references.
                var xplane=doc.FamilyCreate.NewReferencePlane(-v,v,n,view);var yplane=doc.FamilyCreate.NewReferencePlane(-u,u,n,view);xplane.Pinned=yplane.Pinned=true;
                doc.Regenerate();
                foreach(var edge in edges.Where(e=>e.GeometryCurve is Arc))foreach(var end in new[]{0,1})doc.FamilyCreate.NewAlignment(view,yplane.GetReference(),edge.GeometryCurve.GetEndPointReference(end));
                // Both diametric endpoints constrain the centre in the other direction.
                // Revit may reject an incompatible template constraint; preview/flex rolls it back.
                foreach(var edge in edges.Where(e=>e.GeometryCurve is Arc).GroupBy(e=>Math.Round(((Arc)e.GeometryCurve).Radius,8)).Select(g=>g.First()))foreach(var end in new[]{0,1})
                {
                    var arc=(Arc)edge.GeometryCurve;var refs=new ReferenceArray();refs.Append(xplane.GetReference());refs.Append(arc.GetEndPointReference(end));
                    var point=arc.GetEndPoint(end);var shift=-v*(100/Units.MmPerFoot);
                    var dim=doc.FamilyCreate.NewDimension(view,Line.CreateBound(shift,point+shift),refs);
                    dim.FamilyLabel=parameters[Math.Abs(arc.Radius*Units.MmPerFoot-w/2)<1e-5?"Radius":"InnerRadius"];
                }
            }
            else
            {
                var xs=new Dictionary<double,string?>{{0,null},{w,"Width"}};var ys=new Dictionary<double,string?>{{0,null},{h,"Height"}};
                if(shape!="rectangle"){xs[left]="InnerLeft";if(shape is "box" or "i" or "timber_pair")xs[right]="InnerRight";if(shape is "box" or "i" or "channel" or "angle")ys[f]="Flange";if(shape is "box" or "i" or "channel")ys[h-f]="InnerTop";}
                Drive(true,xs);Drive(false,ys);
            }
            doc.Regenerate();
            return new {form_id=preview?(long?)null:form.Id.Value,shape,category=doc.OwnerFamily.FamilyCategory?.Name,drivers=parameters.Where(p=>p.Key is "Width" or "Height" or "Length" or "Web" or "Flange").Select(p=>new {name=p.Value.Definition.Name,is_instance=p.Value.IsInstance}).ToArray(),axis=NativeToolUtil.Vector(n)};
        },()=>
        {
            double Volume() {var e=doc.GetElement(form!.Id)??throw new InvalidOperationException("Section disappeared after commit.");return ConnectionNodes.Solids(e).Sum(s=>s.Volume)*Math.Pow(Units.MmPerFoot,3);}
            var baseline=new Dictionary<string,double>{{"Width",w},{"Height",h},{"Web",t},{"Flange",f},{"Length",length}};
            void Verify(Dictionary<string,double> dims,string scenario)
            {
                var expected=SectionProfile.Area(shape,dims["Width"],dims["Height"],dims["Web"],dims["Flange"])*dims["Length"];var actual=Volume();
                // Revit's native curved-solid volume integration has a small tolerance.
                // Prove the actual cylindrical surfaces/radii as well as volume, so
                // this tolerance cannot accept a polygonal or wrongly sized tube.
                if(shape=="tube")
                {
                    var radii=ConnectionNodes.Solids(doc.GetElement(form!.Id)).SelectMany(s=>s.Faces.Cast<Face>()).OfType<CylindricalFace>()
                        .SelectMany(face=>new[]{face.get_Radius(0).GetLength(),face.get_Radius(1).GetLength()}).Select(radius=>Math.Round(radius*Units.MmPerFoot,5)).Distinct().OrderBy(r=>r).ToArray();
                    var expectedRadii=new[]{dims["Width"]/2-dims["Web"],dims["Width"]/2};
                    if(radii.Length!=2||radii.Zip(expectedRadii,(a,b)=>Math.Abs(a-b)>.001).Any(failed=>failed))throw new ToolInputException("Tube must retain its two native cylindrical radii; update rolled back.");
                }
                var volumeTolerance=shape=="tube"?1e-4:1e-5;
                if(Math.Abs(actual-expected)>Math.Max(1,expected*volumeTolerance))throw new ToolInputException(FormattableString.Invariant($"Section flex '{scenario}' has wrong volume: measured {actual:F3} mm3, expected {expected:F3} mm3; update rolled back."));
                var points=ConnectionNodes.Solids(doc.GetElement(form!.Id)).SelectMany(s=>s.Faces.Cast<Face>()).SelectMany(face=>face.Triangulate().Vertices).ToArray();
                var measured=new[]{u,v,n}.Select(axis=>(points.Max(p=>p.DotProduct(axis))-points.Min(p=>p.DotProduct(axis)))*Units.MmPerFoot).ToArray();var requested=new[]{dims["Width"],dims["Height"],dims["Length"]};
                if(measured.Zip(requested,(a,b)=>Math.Abs(a-b)>Math.Max(.01,b*1e-5)).Any(failed=>failed))throw new ToolInputException($"Section flex '{scenario}' has wrong width/height/length; update rolled back.");
                var minimum=new[]{u,v,n}.Select(axis=>points.Min(p=>p.DotProduct(axis))*Units.MmPerFoot).ToArray();
                var expectedMinimum=shape=="tube"?new[]{-dims["Width"]/2,-dims["Height"]/2,0d}:new[]{0d,0,0};
                if(minimum.Zip(expectedMinimum,(a,b)=>Math.Abs(a-b)>.01).Any(failed=>failed))throw new ToolInputException($"Section flex '{scenario}' moved off its reference origin; update rolled back.");
                flex.Add(new {scenario,measured_dimensions_mm=measured,expected_dimensions_mm=requested,measured_volume_mm3=actual,expected_volume_mm3=expected,passed=true});
            }
            Verify(baseline,"initial");
            foreach(var key in parameters.Keys.Where(k=>baseline.ContainsKey(k)&&!(shape=="tube"&&k=="Height")&&!(placement!=null&&k=="Length")&&string.IsNullOrWhiteSpace(parameters[k].Formula)&&!parameters[k].IsDeterminedByFormula).ToArray())foreach(var factor in new[]{.8,1.2})
            {
                ToolContext.ThrowIfCancelled();var dims=new Dictionary<string,double>(baseline){[key]=baseline[key]*factor};
                if(shape=="tube"&&key=="Width")dims["Height"]=dims["Width"];
                // Refuse shapes whose requested nominal proportions cannot sustain the test envelope.
                SectionProfile.Area(shape,dims["Width"],dims["Height"],dims["Web"],dims["Flange"]);
                NativeToolUtil.Commit(doc,"Claude: section flex "+key,true,()=>{fm.Set(parameters[key],dims[key]/Units.MmPerFoot);doc.Regenerate();return true;},()=>Verify(dims,key+" x "+factor));
            }
            if(placement!=null)foreach(var factor in new[]{.8,1.2})
            {
                var dims=new Dictionary<string,double>(baseline){["Length"]=length*factor};
                NativeToolUtil.Commit(doc,"Claude: template placement-length flex",true,()=>{fm.Set(placement,dims["Length"]/Units.MmPerFoot);doc.Regenerate();return true;},()=>Verify(dims,"placement length x "+factor));
            }
            Verify(baseline,"restored");
        });
        return Services.Json.Serialize(new {preview,result,flex,warnings,placement_length_parameter=placementName,density_source=density.HasValue?"explicit_user_agreed_density":"not_set",limitation="Template placement length/material/reference-plane behavior still needs a native project acceptance test. Explicit density does not automatically follow a different material."});
    }
}
