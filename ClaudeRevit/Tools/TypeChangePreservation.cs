using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

internal static class TypeChangePreservation
{
    private sealed record Value(long Id, string? Guid, string Name, string Spec, StorageType Storage, object? Raw);
    private static object? Raw(Parameter p) => p.StorageType switch { StorageType.Double=>p.AsDouble(), StorageType.Integer=>p.AsInteger(), StorageType.String=>p.AsString(), StorageType.ElementId=>p.AsElementId().Value, _=>null };
    private static bool Same(object? a,object? b) => a is double x && b is double y ? Math.Abs(x-y)<=1e-9*Math.Max(1,Math.Abs(x)) : Equals(a,b);
    // Match identity and data type, never a translated parameter name alone. Type parameters
    // deliberately stay on the selected type. Throw to roll back if a requested value is lost.
    public static (Element Element, List<object> Changes) Change(Element source, ElementId target, bool preserve, string[]? names = null)
    {
        var before=source.Parameters.Cast<Parameter>().Where(p=>p.HasValue && p.StorageType!=StorageType.None)
            .Select(p=>new Value(p.Id.Value,p.IsShared?p.GUID.ToString():null,p.Definition.Name,p.Definition.GetDataType().TypeId,p.StorageType,Raw(p))).ToArray();
        if(names!=null && names.Any(name=>!before.Any(p=>p.Name==name)))throw new ToolInputException("A preserve_parameters name is missing from the instance.");
        var doc=source.Document;var oldId=source.Id;var newId=source.ChangeTypeId(target);
        var element=doc.GetElement(newId!=ElementId.InvalidElementId?newId:oldId) ?? throw new InvalidOperationException("Type change returned no valid element.");
        doc.Regenerate();
        var changes=new List<object>();var verify=new List<Value>();
        foreach(var value in before)
        {
            var p=value.Guid!=null?element.get_Parameter(System.Guid.Parse(value.Guid)):element.Parameters.Cast<Parameter>().FirstOrDefault(p=>p.Id.Value==value.Id);
            bool selected=preserve && (names!=null?names.Contains(value.Name,StringComparer.Ordinal):p is { IsReadOnly:false } && p.StorageType==value.Storage && p.Definition.GetDataType().TypeId==value.Spec);
            if(selected)verify.Add(value);
            var after=p!=null?Raw(p):null;
            if(Same(value.Raw,after))continue;
            bool restored=false;
            if(selected)
            {
                if(p==null || p.IsReadOnly || p.StorageType!=value.Storage || p.Definition.GetDataType().TypeId!=value.Spec)throw new ToolInputException($"Cannot preserve {value.Name}; type change rolled back.");
                restored=value.Storage switch
                {
                    StorageType.Double=>p.Set((double)value.Raw!), StorageType.Integer=>p.Set((int)value.Raw!),
                    StorageType.String=>p.Set((string?)value.Raw??""), StorageType.ElementId=>p.Set(new ElementId((long)value.Raw!)), _=>false
                };
                if(!restored)throw new ToolInputException($"Revit rejected restoration of {value.Name}.");
            }
            changes.Add(new { parameter=value.Name,parameter_id=value.Id,parameter_guid=value.Guid,storage=value.Storage.ToString(),before=value.Raw,after_type_change=after,restored });
        }
        doc.Regenerate();
        foreach(var value in verify)
        {
            var p=value.Guid!=null?element.get_Parameter(System.Guid.Parse(value.Guid)):element.Parameters.Cast<Parameter>().FirstOrDefault(p=>p.Id.Value==value.Id);
            if(p==null||!Same(value.Raw,Raw(p)))throw new ToolInputException($"{value.Name} did not survive regeneration; type change rolled back.");
        }
        return(element,changes);
    }
}
