using System.Collections.Concurrent;
using System.IO;

namespace ClaudeRevit.Services;

// Only native export registers images. MCP cannot use this store to read arbitrary paths.
public static class ViewImageStore
{
    public sealed record Entry(string Base64,string DocumentKey,string? ChannelId,DateTime Expires);
    private static readonly ConcurrentDictionary<string,Entry> Images=new();
    public static string Register(byte[] bytes,string documentKey,string? channelId)
    {
        foreach(var pair in Images.Where(p=>p.Value.Expires<DateTime.UtcNow))Images.TryRemove(pair.Key,out _);
        if(bytes.Length>5_000_000)throw new InvalidOperationException("Export image exceeds 5 MB; use a smaller pixel_size.");
        if(Images.Count>=16)Images.TryRemove(Images.OrderBy(p=>p.Value.Expires).First().Key,out _);
        var id=Guid.NewGuid().ToString("N");Images[id]=new(Convert.ToBase64String(bytes),documentKey,channelId,DateTime.UtcNow.AddMinutes(5));return id;
    }
    public static Entry? Find(string id,string documentKey,string? channelId)=>Images.TryGetValue(id,out var e)&&e.DocumentKey==documentKey&&e.ChannelId==channelId&&e.Expires>DateTime.UtcNow?e:null;
}
