using System;
using System.IO;
using System.Text;

namespace ClaudeRevit.Services;

// Best-effort append-only log at %AppData%\ClaudeRevit\log.txt. Its purpose is
// diagnostic: when Revit crashes, the tail of this file shows exactly what the
// plugin was doing (last tool, arguments, and any managed exception + stack) so a
// crash can be pinpointed instead of guessed at from Revit's journal.
public static class Log
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "log.txt");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static string ReadTail()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return "ClaudeRevit: no diagnostic log yet.";
                using var stream = new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                if (stream.Length>200_000) stream.Seek(-200_000,SeekOrigin.End);
                using var reader = new StreamReader(stream); var text=reader.ReadToEnd();
                return System.Text.RegularExpressions.Regex.Replace(text,@"(?i)(bearer\s+|(?:api[_-]?key|token)\s*[:=]\s*""?)[^\s"",}]+","$1[redacted]");
            }
        }
        catch (Exception ex) { return "Log unavailable: " + ex.Message; }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(level).Append("] ").Append(message);
            if (ex != null)
                sb.Append('\n').Append(ex);
            sb.Append('\n');

            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                // keep the file from growing without bound
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000)
                    File.WriteAllText(FilePath, "");
                File.AppendAllText(FilePath, sb.ToString());
            }
        }
        catch { /* logging must never throw */ }
    }
}
