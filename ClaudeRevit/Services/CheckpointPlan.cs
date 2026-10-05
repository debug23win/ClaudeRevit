using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
namespace ClaudeRevit.Services;

public sealed class CheckpointStep
{
    public string Key {get;set;}="";
    public string Tool {get;set;}="";
    public JsonElement Arguments {get;set;}
    public List<long> WatchIds {get;set;}=[];
}
public sealed class CheckpointPlan
{
    public List<CheckpointStep> Steps {get;set;}=[];
    public static readonly JsonSerializerOptions Options=new(Json.Options){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    public string Hash()=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this,Options))));
    public void Validate()
    {
        if(Steps==null||Steps.Count is <1 or >500)throw new ArgumentException("Supply 1..500 steps.");var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var step in Steps)
        {
            if(step==null||string.IsNullOrWhiteSpace(step.Key)||step.Key.Length>64||string.IsNullOrWhiteSpace(step.Tool)||step.Arguments.ValueKind!=JsonValueKind.Object||step.WatchIds==null||step.WatchIds.Count>1000||step.WatchIds.Any(i=>i<=0))throw new ArgumentException("Invalid step key/tool/arguments/watch_ids.");
            ValidateRefs(step.Arguments,keys);if(!keys.Add(step.Key))throw new ArgumentException("Duplicate step key.");
        }
        if(JsonSerializer.Serialize(this,Options).Length>500_000)throw new ArgumentException("Plan exceeds 500000 characters.");
    }
    private static void ValidateRefs(JsonElement value,HashSet<string> previous)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            if(value.TryGetProperty("from_step",out var source))
            {if(value.EnumerateObject().Count()!=2||!value.TryGetProperty("json_pointer",out var pointer)||!previous.Contains(source.GetString()??"")||pointer.ValueKind!=JsonValueKind.String)throw new ArgumentException("References need a previous from_step and json_pointer, without extra members.");return;}
            foreach(var p in value.EnumerateObject())ValidateRefs(p.Value,previous);
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())ValidateRefs(item,previous);
    }
    public static JsonElement Resolve(JsonElement arguments,IReadOnlyDictionary<string,JsonElement> results)
    {
        JsonNode? Visit(JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.Object)
            {
                if(value.TryGetProperty("from_step",out var source))
                {
                    var selected=results.GetValueOrDefault(source.GetString()??"");if(selected.ValueKind==JsonValueKind.Undefined)throw new ArgumentException("Referenced result is unavailable.");
                    var pointer=value.GetProperty("json_pointer").GetString()??"";if(pointer!=""&&!pointer.StartsWith('/'))throw new ArgumentException("Use an RFC6901 JSON pointer.");
                    foreach(var segment in pointer.Split('/').Skip(1))
                    {var key=segment.Replace("~1","/").Replace("~0","~");selected=selected.ValueKind==JsonValueKind.Array?selected[int.Parse(key,System.Globalization.CultureInfo.InvariantCulture)]:selected.GetProperty(key);}
                    return JsonNode.Parse(selected.GetRawText());
                }
                var obj=new JsonObject();foreach(var p in value.EnumerateObject())obj[p.Name]=Visit(p.Value);return obj;
            }
            if(value.ValueKind==JsonValueKind.Array){var a=new JsonArray();foreach(var item in value.EnumerateArray())a.Add(Visit(item));return a;}
            return JsonNode.Parse(value.GetRawText());
        }
        return JsonSerializer.SerializeToElement(Visit(arguments));
    }
}
