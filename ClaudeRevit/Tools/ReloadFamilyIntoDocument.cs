using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Reloads a family that is OPEN for editing (a Family Editor document) back into another OPEN
// document, silently overwriting the existing family and its parameter values — no disk file and
// no interactive "family already exists" dialog. This is the natural finish to editing a nested
// loadable family: edit it in the Family Editor, then push it into the host project/family.
// Complements load_family (which loads a .rfa from a PATH and does not overwrite parameter values).
//
// Originally authored by the assistant via save_tool and promoted into the built-in set.
public class ReloadFamilyIntoDocument : IRevitTool
{
    public string Name => "reload_family_into_document";

    public string Description =>
        "Reloads a family that is currently OPEN in the Family Editor (the source) into another " +
        "currently-open document (the target), overwriting the existing family and its parameter " +
        "values with no interactive prompt. Both documents must already be open. Use this after " +
        "editing a nested/loadable family in the Family Editor — unlike load_family it needs no .rfa " +
        "path and silently overwrites. Documents are matched by a case-insensitive substring of their title.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["source_title_contains"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Substring of the open source (Family Editor) document's title."
            }),
            ["target_title_contains"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Substring of the open target document's title (where the family should be updated)."
            })
        },
        Required = ["source_title_contains", "target_title_contains"]
    };

    // LoadFamily manages its own transaction and must NOT run inside one; it still mutates the
    // target, so the dispatcher invalidates caches afterwards.
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var srcSub = input["source_title_contains"].GetString();
        var tgtSub = input["target_title_contains"].GetString();
        if (string.IsNullOrWhiteSpace(srcSub) || string.IsNullOrWhiteSpace(tgtSub))
            throw new InvalidOperationException("Both source_title_contains and target_title_contains are required.");

        var open = app.Application.Documents.Cast<Document>().ToList();

        Document Match(string sub, string which)
        {
            var hits = open.Where(d => d.Title.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 0)
                throw new InvalidOperationException(
                    $"No open {which} document matches '{sub}'. Open documents: " +
                    $"[{string.Join(", ", open.Select(d => d.Title))}].");
            if (hits.Count > 1)
                throw new InvalidOperationException(
                    $"'{sub}' matches {hits.Count} open documents ([{string.Join(", ", hits.Select(d => d.Title))}]) " +
                    "— use a more specific substring.");
            return hits[0];
        }

        var srcDoc = Match(srcSub, "source");
        var tgtDoc = Match(tgtSub, "target");

        if (!srcDoc.IsFamilyDocument)
            throw new InvalidOperationException(
                $"Source document '{srcDoc.Title}' is not a family document — open the family for editing first.");
        if (ReferenceEquals(srcDoc, tgtDoc))
            throw new InvalidOperationException("Source and target resolve to the same document.");

        var loaded = srcDoc.LoadFamily(tgtDoc, new SilentOverwriteLoadOptions());

        return JsonSerializer.Serialize(new
        {
            ok = true,
            source_title = srcDoc.Title,
            target_title = tgtDoc.Title,
            loaded_family = loaded?.Name,
            loaded_family_id = loaded?.Id.Value
        });
    }

    // Overwrites the existing family AND its parameter values, with no dialog.
    private sealed class SilentOverwriteLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
