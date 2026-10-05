using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace ClaudeRevit.Services;

// All Revit document reads happen on its API thread. HTTP/CLI workers use these snapshots.
public static class DocumentSessions
{
    private sealed record DocumentIdentity(Document Document, string Id);
    // Revit can return different managed wrappers for the same open native document.
    // ConditionalWeakTable/ReferenceEquals compare wrappers and falsely report a switch
    // on Idling, erasing unsaved chat drafts and rejecting queued benchmark/MCP work.
    // Use native Document.Equals; prune closed documents so reopened files get new keys.
    private static readonly List<DocumentIdentity> Identities = new();
    private static readonly Dictionary<string, ConversationWorkspace> Workspaces = new();
    private static readonly string ProcessId = Guid.NewGuid().ToString("N");
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit");
    public sealed record Snapshot(string DocumentKey, ConversationWorkspace Workspace);
    private static Snapshot _current = new("none", Workspace("idle:" + ProcessId));
    public static Snapshot Current => System.Threading.Volatile.Read(ref _current);
    public static string CurrentDocumentKey => Current.DocumentKey;
    public static ConversationWorkspace CurrentWorkspace => Current.Workspace;
    public static event Action<bool>? Changed;
    public static event Action<string>? Closed;

    private static ConversationWorkspace Workspace(string identity)
    {
        if (!Workspaces.TryGetValue(identity, out var workspace))
            Workspaces[identity] = workspace = ConversationWorkspace.Acquire(Root, identity);
        return workspace;
    }

    public static void Initialize(UIControlledApplication app)
    { app.ViewActivated += OnViewActivated; app.Idling += OnIdling; }
    public static void Shutdown(UIControlledApplication app)
    {
        app.ViewActivated -= OnViewActivated; app.Idling -= OnIdling;
        foreach (var workspace in Workspaces.Values) workspace.Dispose();
        Workspaces.Clear();
        Identities.Clear();
    }
    private static void OnViewActivated(object? sender, ViewActivatedEventArgs e) => Update(e.CurrentActiveView?.Document);
    private static void OnIdling(object? sender, IdlingEventArgs e)
    { if (sender is UIApplication app) Update(app.ActiveUIDocument?.Document); }

    public static bool Same(Document? left, Document? right) =>
        left != null && right != null && left.IsValidObject && right.IsValidObject && left.Equals(right);

    // API thread only: session keys identify open documents, independently of file paths,
    // titles, Save As, managed wrappers or hash collisions.
    public static string Key(Document? document)
    {
        for (var i = Identities.Count - 1; i >= 0; i--)
            if (!Identities[i].Document.IsValidObject)
            { var key=Identities[i].Id;Identities.RemoveAt(i);Closed?.Invoke(key); }
        if (document == null || !document.IsValidObject) return "none";
        foreach (var identity in Identities)
            if (Same(identity.Document, document)) return identity.Id;
        var id = Guid.NewGuid().ToString("N");
        Identities.Add(new(document, id));
        return id;
    }
    // API thread only. Bound jobs never fall back to whichever tab happens to be active.
    public static Document? Find(string key) => Identities.FirstOrDefault(i => i.Id == key && i.Document.IsValidObject)?.Document;

    private static string Identity(Document document, string key)
    {
        if (document.IsModelInCloud)
        {
            var path = document.GetCloudModelPath();
            return "cloud:" + path.GetProjectGUID() + ":" + path.GetModelGUID();
        }
        if (document.IsWorkshared && document.WorksharingCentralGUID != Guid.Empty)
            return "central:" + document.WorksharingCentralGUID;
        return !string.IsNullOrWhiteSpace(document.PathName)
            ? "file:" + document.PathName.Replace('/', '\\').Trim().ToUpperInvariant()
            : "unsaved:" + ProcessId + ":" + key;
    }
    public static void Update(Document? document)
    {
        if (document != null && !document.IsValidObject) document = null;
        var key = Key(document);
        var identity = document == null ? "idle:" + ProcessId : Identity(document, key);
        var workspace = Workspace(identity);
        if (key == CurrentDocumentKey && ReferenceEquals(workspace, CurrentWorkspace)) return;
        System.Threading.Volatile.Write(ref _current, new Snapshot(key, workspace));
        Changed?.Invoke(Tools.ToolContext.IsExecuting);
    }
}
