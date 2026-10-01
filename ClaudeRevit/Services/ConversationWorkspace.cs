using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeRevit.Services;

// An exclusive, reusable slot: separate Revit processes never write the same history or
// resume the same CLI session. A released slot can be restored after Revit restarts.
public sealed class ConversationWorkspace : IDisposable
{
    private readonly FileStream _lease;
    public string DirectoryPath { get; }
    public string HistoryPath => Path.Combine(DirectoryPath, "conversation.json");
    public string ClaudeSessionPath => Path.Combine(DirectoryPath, "claudecode-session.txt");
    public string CodexSessionPath => Path.Combine(DirectoryPath, "codex-session.txt");
    public string ClientDirectory => Path.Combine(DirectoryPath, "client");

    private ConversationWorkspace(string directory, FileStream lease)
    { DirectoryPath = directory; _lease = lease; }

    public static string ProjectKey(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    public static ConversationWorkspace Acquire(string root, string identity)
    {
        var project = Path.Combine(root, "conversations", ProjectKey(identity));
        for (var slot = 0; slot < 1024; slot++)
        {
            var directory = Path.Combine(project, slot.ToString("D4"));
            Directory.CreateDirectory(directory);
            try
            {
                var lease = new FileStream(Path.Combine(directory, ".lease"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return new ConversationWorkspace(directory, lease);
            }
            catch (IOException) { /* another process owns this slot */ }
        }
        throw new IOException("Too many active conversations for this document.");
    }

    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => _lease.Dispose();
}
