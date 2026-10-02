using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeRevit.Services;

// Revit's tab-separated FOP format predates ForgeTypeId. Preserve the source's
// data-type token; a NUMBER called "mass" must not become a MASS parameter.
public sealed record SharedParameterDefinition(Guid Guid, string Name, string DataType,
    string DataCategory, string Group, string Description, string Profile, string Source)
{
    public string? RecommendedGroup { get; init; }
    public bool? Visible { get; init; }
    public bool? UserModifiable { get; init; }
    public bool? HideWhenNoValue { get; init; }
}

public sealed record SharedParameterCatalog(string Source, string Sha256,
    IReadOnlyList<SharedParameterDefinition> Definitions, IReadOnlyList<string> Warnings)
{
    public const int MaxBytes = 8 * 1024 * 1024;

    public static SharedParameterCatalog Read(string path, string profile = "configured-fop")
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxBytes) throw new InvalidDataException("FOP exceeds the 8 MB limit.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bytes.Length + read > MaxBytes) throw new InvalidDataException("FOP exceeds the 8 MB limit.");
            bytes.Write(buffer, 0, read);
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd(), Path.GetFileName(path), profile) with { Sha256 = hash };
    }

    public static SharedParameterCatalog Parse(string text, string source, string profile)
    {
        if (text.Length > MaxBytes) throw new InvalidDataException("FOP exceeds the size limit.");
        var groups = new Dictionary<string, string>();
        var parameters = new List<(int Line, string[] Cells)>();
        var warnings = new List<string>();
        string[]? header = null;
        var lineNumber = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var cells = line.TrimStart('\uFEFF').Split('\t');
            if (cells[0] == "*PARAM") header = cells;
            else if (cells[0] == "GROUP" && cells.Length >= 3) groups[cells[1]] = cells[2];
            else if (cells[0] == "PARAM") parameters.Add((lineNumber, cells));
        }
        if (header == null || !header.Contains("GUID") || !header.Contains("NAME") || !header.Contains("DATATYPE"))
            throw new InvalidDataException("Not a Revit shared parameter file: PARAM header is missing or invalid.");
        string Cell(string[] cells, string column)
        {
            int index = Array.IndexOf(header, column);
            return index >= 0 && index < cells.Length ? cells[index] : "";
        }
        var definitions = new List<SharedParameterDefinition>();
        var seen = new HashSet<Guid>();
        foreach (var (line, cells) in parameters)
        {
            if (!Guid.TryParse(Cell(cells, "GUID"), out var guid) || guid == Guid.Empty ||
                string.IsNullOrWhiteSpace(Cell(cells, "NAME")) || string.IsNullOrWhiteSpace(Cell(cells, "DATATYPE")))
            { warnings.Add($"Line {line}: invalid GUID/name/data type; skipped."); continue; }
            if (!seen.Add(guid))
            { warnings.Add($"Line {line}: duplicate GUID {guid}; first definition retained."); continue; }
            var groupId = Cell(cells, "GROUP");
            bool? Flag(string column) => Cell(cells, column) switch { "1" => true, "0" => false, _ => null };
            definitions.Add(new(guid, Cell(cells, "NAME"), Cell(cells, "DATATYPE"),
                Cell(cells, "DATACATEGORY"), groups.GetValueOrDefault(groupId, groupId),
                Cell(cells, "DESCRIPTION"), profile, source)
            { Visible = Flag("VISIBLE"), UserModifiable = Flag("USERMODIFIABLE"), HideWhenNoValue = Flag("HIDEWHENNOVALUE") });
        }
        foreach (var duplicate in definitions.GroupBy(d => d.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            warnings.Add($"Name '{duplicate.Key}' has multiple GUIDs; resolve it by GUID.");
        return new(source, "", definitions, warnings);
    }
}

// Matching is by GUID, never by a translated name or a file's display title.
public static class StandardParameterMatching
{
    public sealed record Evidence(string Profile, int MatchedGuidCount, int NameHints,
        string Confidence, IReadOnlyList<string> Examples);

    public static IReadOnlyList<Evidence> Detect(IEnumerable<(Guid? Guid, string Name)> live,
        IEnumerable<SharedParameterDefinition> reference)
    {
        var parameters = live.ToList();
        var guids = parameters.Where(p => p.Guid.HasValue).Select(p => p.Guid!.Value).ToHashSet();
        var evidence = reference.GroupBy(d => d.Profile).Select(g =>
        {
            var matched = g.Where(d => guids.Contains(d.Guid)).DistinctBy(d => d.Guid).ToList();
            return new Evidence(g.Key, matched.Count, 0, "guid-match", matched.Take(5).Select(d => d.Name).ToList());
        }).Where(e => e.MatchedGuidCount > 0).ToList();
        var adsk = parameters.Select(p => p.Name).Where(n => n.StartsWith("ADSK_", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).ToList();
        if (adsk.Count > 0)
            evidence.Add(new("ADSK", 0, adsk.Count, "name-hint-only; edition and GUIDs require the actual ADSK FOP",
                adsk.Take(5).ToList()));
        return evidence;
    }
}
