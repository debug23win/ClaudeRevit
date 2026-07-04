using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

// Session identity of a document for caches. Title+PathName alone collides for two
// successive unsaved documents ("Project1" + empty path); the instance hash tells a
// reopened same-path document apart from the one a cache entry was built for.
internal static class DocKey
{
    public static string For(Document doc) =>
        doc.Title + "|" + doc.PathName + "|" + doc.GetHashCode();
}
