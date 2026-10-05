using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

// Revit API objects are graphs, not DTOs. Particularly Transform.Inverse.Inverse cycles.
// Snapshot the common geometry/value objects before unloading the script assembly.
internal static class RevitJsonConverters
{
    public static readonly JsonSerializerOptions Options = Build();
    private static JsonSerializerOptions Build()
    {
        var o=new JsonSerializerOptions(Services.Json.Options);
        o.Converters.Add(new Snapshot<XYZ>(v=>new { x=v.X,y=v.Y,z=v.Z,unit="feet" }));
        o.Converters.Add(new Snapshot<ElementId>(v=>v.Value));
        o.Converters.Add(new Snapshot<Transform>(v=>new { origin=NativeToolUtil.Vector(v.Origin),basis_x=NativeToolUtil.Vector(v.BasisX),basis_y=NativeToolUtil.Vector(v.BasisY),basis_z=NativeToolUtil.Vector(v.BasisZ),origin_unit="feet" }));
        o.Converters.Add(new Snapshot<BoundingBoxXYZ>(v=>new { min=NativeToolUtil.Vector(v.Min),max=NativeToolUtil.Vector(v.Max),transform=v.Transform,unit="feet" }));
        o.Converters.Add(new Elements());return o;
    }
    private sealed class Snapshot<T>(Func<T,object> value):JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer,T input,JsonSerializerOptions options)=>JsonSerializer.Serialize(writer,value(input),options);
    }
    private sealed class Elements:JsonConverterFactory
    {
        public override bool CanConvert(Type t)=>typeof(Element).IsAssignableFrom(t);
        public override JsonConverter CreateConverter(Type t,JsonSerializerOptions o)=>(JsonConverter)Activator.CreateInstance(typeof(ElementSnapshot<>).MakeGenericType(t))!;
    }
    public sealed class ElementSnapshot<T>:JsonConverter<T> where T:Element
    {
        public override T Read(ref Utf8JsonReader reader,Type t,JsonSerializerOptions o)=>throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer,T e,JsonSerializerOptions o)
        {
            if(!e.IsValidObject){JsonSerializer.Serialize(writer,new { valid=false },o);return;}
            JsonSerializer.Serialize(writer,new { id=e.Id.Value,unique_id=e.UniqueId,name=e.Name,category=e.Category?.Name,@class=e.GetType().Name,valid=true },o);
        }
    }
}
