using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

// API-thread session identity shared by caches and native reference tools. Different
// wrappers, Save As, equal titles/paths and hash collisions cannot confuse documents.
internal static class DocKey
{
    public static string For(Document doc) =>
        Services.DocumentSessions.Key(doc);
}
