using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ClaudeRevit.Services;

public sealed record BenchmarkDistribution(int Count,double Median,double P95,double Minimum,double Maximum,double PassRate);
public static class BenchmarkStatistics
{
    public static BenchmarkDistribution Summarize(IEnumerable<(double Seconds,bool Passed)> samples)
    {
        var data=samples.Where(s=>double.IsFinite(s.Seconds)&&s.Seconds>0).ToArray();
        if(data.Length==0)return new(0,0,0,0,0,0);
        var sorted=data.Select(s=>s.Seconds).Order().ToArray();
        double Quantile(double q){double index=(sorted.Length-1)*q;int lo=(int)Math.Floor(index),hi=(int)Math.Ceiling(index);return sorted[lo]+(sorted[hi]-sorted[lo])*(index-lo);}
        return new(data.Length,Quantile(.5),Quantile(.95),sorted[0],sorted[^1],100d*data.Count(s=>s.Passed)/data.Length);
    }
    public static string SeedFingerprint(string probe)
    {
        using var parsed=JsonDocument.Parse(probe);
        var values=parsed.RootElement.EnumerateObject().Where(p=>p.Name is not ("document_key" or "document_title" or "document_path" or "schedule_exports"))
            .OrderBy(p=>p.Name,StringComparer.Ordinal).ToDictionary(p=>p.Name,p=>p.Value.Clone());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    }
    public static string EnvironmentKey(string revitVersion)=>$"Revit-{revitVersion}|{Environment.MachineName}|{Environment.ProcessorCount}|{Environment.OSVersion.Version}|{Environment.Version}";
    public static IReadOnlyList<(string Key,BenchmarkDistribution Distribution)> Group(IEnumerable<BenchmarkResult> rows)=>rows.Where(r=>r.Score.HasValue)
        .GroupBy(r=>$"{r.TaskId}|{r.Model}|{r.SeedFingerprint}|{r.EnvironmentKey}|{r.ComparisonKey}",StringComparer.Ordinal)
        .Select(g=>(g.Key,Summarize(g.Select(r=>(r.Seconds,r.Verdict=="✓"))))).ToArray();
}
