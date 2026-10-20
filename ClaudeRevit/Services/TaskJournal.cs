using System.IO;
using System.Text.Json;
using System.Threading;

namespace ClaudeRevit.Services;

public static class TaskJournal
{
    private static readonly object Gate=new();
    private static Task _pending=Task.CompletedTask;
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ClaudeRevit","task_journal.jsonl");
    private static readonly AsyncLocal<string?> Current=new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,(double Queue,double Execution)> Timings=new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,double> Workers=new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, PhaseMetric>> Phases = new();
    public sealed record PhaseMetric(int Count, double Seconds);
    private static string? _lastWriteError;
    public static string? LastWriteError => Volatile.Read(ref _lastWriteError);
    public static string? CurrentId => Current.Value;
    public static IDisposable MeasurePhase(string phase) => new PhaseScope(CurrentId, phase);
    // The Revit API thread does not carry the chat flow's AsyncLocal, so code there passes the id.
    public static IDisposable MeasurePhase(string? id, string phase) => new PhaseScope(id, phase);
    private sealed class PhaseScope(string? id, string phase) : IDisposable
    {
        private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || id == null || !Phases.TryGetValue(id, out var phases)) return;
            var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalSeconds;
            phases.AddOrUpdate(phase, new PhaseMetric(1, seconds), (_, old) => new(old.Count + 1, old.Seconds + seconds));
        }
    }
    public static IReadOnlyDictionary<string, PhaseMetric> ReadPhases(string? id) =>
        id != null && Phases.TryGetValue(id, out var phases) ? new Dictionary<string, PhaseMetric>(phases) : new Dictionary<string, PhaseMetric>();
    public static IDisposable Start(string documentKey)
    { var old=Current.Value;Current.Value=Guid.NewGuid().ToString("N");Timings[Current.Value]=default;Workers[Current.Value]=0;Phases[Current.Value]=new();Append(new { kind="task_started",task_id=Current.Value,document_key=documentKey,utc=DateTime.UtcNow });return new Scope(old,Current.Value); }
    private sealed class Scope(string? old,string id):IDisposable { public void Dispose(){ Timings.TryRemove(id,out _);Workers.TryRemove(id,out _);Phases.TryRemove(id,out _);Current.Value=old; } }
    public static void RecordWorker(string? id,double seconds) {if(id!=null&&Workers.ContainsKey(id))Workers.AddOrUpdate(id,seconds,(_,sum)=>sum+seconds);}
    public static double ReadWorker(string? id)=>id!=null&&Workers.TryGetValue(id,out var seconds)?seconds:0;
    public static void RecordTiming(string? id,double queue,double execution)
    { if(id!=null && Timings.ContainsKey(id))Timings.AddOrUpdate(id,(queue,execution),(_,sum)=>(sum.Queue+queue,sum.Execution+execution)); }
    public static (double Queue,double Execution) ReadTimings(string? id) => id!=null && Timings.TryGetValue(id,out var value)?value:default;
    public static void Append(object entry)
    {
        var line=JsonSerializer.Serialize(entry);
        lock(Gate)_pending=_pending.ContinueWith(_=>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                if(File.Exists(PathName)&&new FileInfo(PathName).Length>10_000_000)
                { var lines=File.ReadAllLines(PathName);File.WriteAllLines(PathName,lines.TakeLast(1000)); }
                File.AppendAllText(PathName,line+"\n");
                Volatile.Write(ref _lastWriteError, null);
            }
            catch (Exception ex) { Volatile.Write(ref _lastWriteError, ex.Message); }
        },CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
    }
    public static string ReadRecent(string documentKey)
    {
        try
        {
            Task pending;lock(Gate)pending=_pending;pending.Wait(TimeSpan.FromSeconds(2));
            if(!File.Exists(PathName))return "";
            return string.Join("\n",File.ReadLines(PathName).Where(line=>
            {try{using var json=JsonDocument.Parse(line);return json.RootElement.TryGetProperty("document_key",out var key)&&key.GetString()==documentKey;}catch(JsonException){return false;}}).TakeLast(200));
        }
        catch { return ""; }
    }
}
