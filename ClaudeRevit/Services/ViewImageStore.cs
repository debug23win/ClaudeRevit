using System.Collections.Concurrent;
using System.IO;

namespace ClaudeRevit.Services;

// Native exports and scoped user attachments register images. MCP cannot use
// this store to read arbitrary paths.
public static class ViewImageStore
{
    public sealed record Entry(string Base64,string DocumentKey,string? ChannelId,DateTime Expires);
    private static readonly ConcurrentDictionary<string,Entry> Images=new();
    public const int MaxBytes=5_000_000;
    // Returns null for an image too large to hand to a model. It used to throw — AFTER the export
    // had already written the file — so the tool reported a failure for a PNG that was on disk.
    // Callers report the file and explain why no image is attached instead.
    public static string? Register(byte[] bytes,string documentKey,string? channelId)
    {
        foreach(var pair in Images.Where(p=>p.Value.Expires<DateTime.UtcNow))Images.TryRemove(pair.Key,out _);
        if(bytes.Length>MaxBytes)return null;
        if(Images.Count>=16)Images.TryRemove(Images.OrderBy(p=>p.Value.Expires).First().Key,out _);
        var id=Guid.NewGuid().ToString("N");Images[id]=new(Convert.ToBase64String(bytes),documentKey,channelId,DateTime.UtcNow.AddMinutes(5));return id;
    }
    public static Entry? Find(string id,string documentKey,string? channelId)=>Images.TryGetValue(id,out var e)&&e.DocumentKey==documentKey&&e.ChannelId==channelId&&e.Expires>DateTime.UtcNow?e:null;
}
