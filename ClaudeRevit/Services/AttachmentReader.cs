using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ClaudeRevit.Services;

public sealed record AttachmentSection(string Name, string Text);
public sealed record AttachmentContent(string Kind, IReadOnlyList<AttachmentSection> Sections, string? Notice = null, bool Truncated = false);

// Pure managed parsers run on a worker, never in Revit's API event. No Office
// automation, macros, external XML entities or archive extraction are involved.
public static class AttachmentReader
{
    public const int MaxText = 1_000_000;
    private const int MaxXmlBytes = 16_000_000;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".html", ".htm", ".log", ".ini", ".toml", ".cs", ".py", ".ps1", ".sql", ".svg", ".dxf", ".ifc", ".addin", ".rfa.txt" };
    public static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static AttachmentContent Read(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (IsImage(path)) return new("image", Array.Empty<AttachmentSection>(), "Use read_attachment to inspect the image.");
        if (extension == ".pdf")
        {
            using var pdf = PdfDocument.Open(path);
            var sections = new List<AttachmentSection>(); var remaining = MaxText;
            for (var p = 1; p <= pdf.NumberOfPages && p <= 500 && remaining > 0; p++)
            {
                ct.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(pdf.GetPage(p));
                sections.Add(new("page " + p, text[..Math.Min(text.Length, remaining)])); remaining -= sections[^1].Text.Length;
            }
            return new("pdf", sections, sections.All(s => string.IsNullOrWhiteSpace(s.Text)) ? "No extractable text. This PDF may contain scans; OCR is not included." : null,
                pdf.NumberOfPages > sections.Count || remaining == 0);
        }
        if (extension is ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" or ".zip" or ".ifczip")
            return ReadArchive(path, extension, ct);
        using var stream = File.OpenRead(path);
        var signature = new byte[Math.Min(4096, (int)Math.Min(stream.Length, 4096))]; stream.ReadExactly(signature); stream.Position = 0;
        var bom = signature.Length >= 2 && ((signature[0] == 0xff && signature[1] == 0xfe) || (signature[0] == 0xfe && signature[1] == 0xff));
        var looksText = bom || (signature.All(b => b != 0) && signature.Count(b => b < 9 || b is > 13 and < 32) < Math.Max(1, signature.Length / 100));
        if (!looksText) return new("binary", Array.Empty<AttachmentSection>(), "Binary content is attached. Use its local_path with a suitable native import/open tool; no text parser is available for this format.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.UTF8;
        if (!bom)
        {
            try
            {
                // The sample may end halfway through a UTF-8 character.
                var decoder = new UTF8Encoding(false, true).GetDecoder();
                decoder.Convert(signature, new char[signature.Length], false, out _, out _, out _);
            }
            catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(1251); }
        }
        using var reader = new StreamReader(stream, encoding, true);
        var chars = new char[MaxText + 1]; var read = reader.ReadBlock(chars, 0, chars.Length);
        return new(TextExtensions.Contains(extension) ? "text" : "text_detected", new[] { new AttachmentSection("text", new string(chars, 0, Math.Min(read, MaxText))) }, Truncated: read > MaxText);
    }
    private static AttachmentContent ReadArchive(string path, string extension, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 10_000) throw new InvalidDataException("Archive has more than 10000 entries.");
        XDocument Xml(ZipArchiveEntry entry)
        {
            if (entry.Length > MaxXmlBytes) throw new InvalidDataException("XML entry exceeds 16 MB: " + entry.FullName);
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
            return XDocument.Load(reader);
        }
        var sections = new List<AttachmentSection>(); var budget = MaxText; var truncated = false;
        void Add(string name, string text)
        { if (budget == 0) { truncated = true; return; } var length = Math.Min(text.Length, budget); sections.Add(new(name, text[..length])); budget -= length; truncated |= length < text.Length; }
        var entries = zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal).ToArray();
        string TextXml(ZipArchiveEntry e) => string.Join("\n", Xml(e).Descendants().Where(n => n.Name.LocalName == "p").Select(n => string.Concat(n.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))));
        if (extension == ".docx")
        {
            foreach (var e in entries.Where(e => e.FullName == "word/document.xml" || e.FullName.StartsWith("word/header") || e.FullName.StartsWith("word/footer")))
            { ct.ThrowIfCancellationRequested(); Add(e.FullName, TextXml(e)); }
        }
        else if (extension == ".pptx")
        {
            foreach (var e in entries.Where(e => (e.FullName.StartsWith("ppt/slides/slide") || e.FullName.StartsWith("ppt/notesSlides/notesSlide")) && e.FullName.EndsWith(".xml")))
            { ct.ThrowIfCancellationRequested(); Add(e.FullName, string.Join("\n", Xml(e).Descendants().Where(n => n.Name.LocalName == "t").Select(n => n.Value))); }
        }
        else if (extension == ".xlsx")
        {
            var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
            var strings = sharedEntry == null ? Array.Empty<string>() : Xml(sharedEntry).Descendants().Where(n => n.Name.LocalName == "si").Select(n => string.Concat(n.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToArray();
            var names = new Dictionary<string, string>();
            var rels = zip.GetEntry("xl/_rels/workbook.xml.rels"); var workbook = zip.GetEntry("xl/workbook.xml");
            if (rels != null && workbook != null)
            {
                var targets = Xml(rels).Descendants().Where(n => n.Name.LocalName == "Relationship").ToDictionary(n => (string?)n.Attribute("Id") ?? "", n => (string?)n.Attribute("Target") ?? "");
                foreach (var sheet in Xml(workbook).Descendants().Where(n => n.Name.LocalName == "sheet"))
                { var id = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? ""; if (targets.TryGetValue(id, out var target)) names[target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target] = (string?)sheet.Attribute("name") ?? target; }
            }
            foreach (var e in entries.Where(e => e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml")))
            {
                ct.ThrowIfCancellationRequested(); var sb = new StringBuilder();
                foreach (var row in Xml(e).Descendants().Where(n => n.Name.LocalName == "row"))
                {
                    foreach (var cell in row.Elements().Where(n => n.Name.LocalName == "c"))
                    {
                        var value = cell.Elements().FirstOrDefault(n => n.Name.LocalName == "v")?.Value ?? string.Concat(cell.Descendants().Where(n => n.Name.LocalName == "t").Select(n => n.Value));
                        if ((string?)cell.Attribute("t") == "s" && int.TryParse(value, out var index) && index >= 0 && index < strings.Length) value = strings[index];
                        sb.Append((string?)cell.Attribute("r")).Append('=').Append(value).Append('\t');
                    }
                    sb.AppendLine(); if (sb.Length >= MaxText) { truncated = true; break; }
                }
                Add(names.GetValueOrDefault(e.FullName, e.FullName), sb.ToString());
            }
        }
        else if (extension is ".odt" or ".ods" or ".odp")
        {
            if (zip.GetEntry("content.xml") is { } e) Add("content", string.Join("\n", Xml(e).Descendants().Where(n => n.Name.LocalName is "p" or "h").Select(n => n.Value)));
        }
        else
        {
            foreach (var e in entries.Take(1000))
            {
                ct.ThrowIfCancellationRequested();
                if (TextExtensions.Contains(Path.GetExtension(e.FullName)) && e.Length <= 2_000_000)
                { using var reader = new StreamReader(e.Open()); var chars = new char[Math.Min(MaxText, Math.Max(1, budget)) + 1]; var count = reader.ReadBlock(chars, 0, chars.Length); Add(e.FullName, new string(chars, 0, Math.Min(count, chars.Length - 1))); truncated |= count == chars.Length; }
                else Add(e.FullName, $"[archive entry, {e.Length} bytes; no extracted text]");
            }
            truncated |= entries.Length > 1000;
        }
        return new(extension.TrimStart('.'), sections, "Text/cached cell values only; macros are not executed. PDF scans and embedded images are not rendered by this extractor.", truncated);
    }
}
