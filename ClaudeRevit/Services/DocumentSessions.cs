using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace ClaudeRevit.Services;

// All Revit document reads happen on its API thread. HTTP/CLI workers use these snapshots.
public static class DocumentSessions
{
    private sealed class DocumentIdentity { public string Id { get; } = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<Document, DocumentIdentity> Identities = new();
    private static readonly Dictionary<string, ConversationWorkspace> Workspaces = new();
    private static readonly string ProcessId = Guid.NewGuid().ToString("N");
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit");
    private static string _documentKey = "none";
    public static string CurrentDocumentKey => System.Threading.Volatile.Read(ref _documentKey);
    public static ConversationWorkspace CurrentWorkspace { get; private set; } = Workspace("idle:" + ProcessId);
    public static event Action<bool>? Changed;

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
    }
    private static void OnViewActivated(object? sender, ViewActivatedEventArgs e) => Update(e.CurrentActiveView?.Document);
    private static void OnIdling(object? sender, IdlingEventArgs e)
    { if (sender is UIApplication app) Update(app.ActiveUIDocument?.Document); }

    public static string Key(Document? document) => document == null ? "none" : Identities.GetValue(document, _ => new()).Id;
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
        var key = Key(document);
        var identity = document == null ? "idle:" + ProcessId : Identity(document, key);
        var workspace = Workspace(identity);
        if (key == CurrentDocumentKey && ReferenceEquals(workspace, CurrentWorkspace)) return;
        System.Threading.Volatile.Write(ref _documentKey, key);
        CurrentWorkspace = workspace;
        Changed?.Invoke(Tools.ToolContext.IsExecuting);
    }
}
