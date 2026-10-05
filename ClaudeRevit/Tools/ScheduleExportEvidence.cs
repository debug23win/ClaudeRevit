using System.Runtime.CompilerServices;
using System.IO;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using ClaudeRevit.Services;
namespace ClaudeRevit.Tools;

internal static class ScheduleExportEvidence
{
    private sealed record Export(string ScheduleUid, string Path, string Hash, int Rows);
    private static readonly ConditionalWeakTable<Document, Dictionary<string, Export>> Exports = new();
    public static void Record(Document doc, ViewSchedule schedule, string path)
    {
        var bytes = File.ReadAllBytes(path);
        var records = Exports.GetOrCreateValue(doc);
        if (records.Count >= 100) records.Remove(records.Keys.First());
        records[schedule.UniqueId] = new(schedule.UniqueId, Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)), CsvEvidence.Parse(File.ReadAllText(path)).Count);
    }
    public static object[] Probe(Document doc) => Exports.GetOrCreateValue(doc).Values.Select(e =>
    {
        try
        {
            var schedule = doc.GetElement(e.ScheduleUid) as ViewSchedule;
            if (schedule == null || !File.Exists(e.Path)) return (object)new { schedule_id = schedule?.Id.Value, verified = false, error = "Schedule or exported file is missing." };
            var bytes = File.ReadAllBytes(e.Path); if (bytes.Length > 10_000_000) throw new InvalidOperationException("Export exceeds evidence limit.");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var exported = CsvEvidence.Parse(File.ReadAllText(e.Path));
            var body = schedule.GetTableData().GetSectionData(SectionType.Body);
            if (body.NumberOfRows > 2000 || body.NumberOfColumns > 100) throw new InvalidOperationException("Schedule exceeds evidence limit.");
            var expected = new List<string[]>();
            for (int r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
                expected.Add(Enumerable.Range(body.FirstColumnNumber, body.NumberOfColumns).Select(c => schedule.GetCellText(SectionType.Body, r, c)).ToArray());
            bool matches = CsvEvidence.ContainsRows(exported, expected);
            return new { schedule_id = schedule.Id.Value, path = e.Path, sha256 = hash, exported_rows = exported.Count, native_rows = body.NumberOfRows, unchanged_since_export = hash == e.Hash, model_rows_match = matches, verified = hash == e.Hash && matches };
        }
        catch (Exception ex) { return (object)new { path = e.Path, verified = false, error = ex.Message }; }
    }).ToArray();
}
