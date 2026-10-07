using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeRevit.Services;

// Identifies "the same sign-in" for caching a subscription check.
//
// The checks (`claude auth status`, `codex login status`) used to run before every message,
// costing a process start each time. Caching them by time alone would break the guarantee they
// exist for: subscription mode must never run on API billing, so a switch from a ChatGPT/Claude
// login to an API key has to be noticed on the very next message. The fingerprint therefore
// covers everything a CLI reads its credentials from — the executable itself, its credential
// files, and every environment variable whose name says it carries auth or an endpoint — and
// any change to them forces a fresh check.
public static class AuthFingerprint
{
    private static readonly string[] Markers = { "AUTH", "API_KEY", "TOKEN", "BASE_URL", "USE_BEDROCK", "USE_VERTEX", "USE_FOUNDRY", "_HOME", "CONFIG_DIR" };

    public static string For(string executable, params string[] credentialFiles)
    {
        var sb = new StringBuilder();
        sb.Append(Stamp(executable));
        foreach (var file in credentialFiles) sb.Append('|').Append(Stamp(file));
        var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(e => (Name: e.Key?.ToString() ?? "", Value: e.Value?.ToString() ?? ""))
            .Where(e => Markers.Any(m => e.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Name, StringComparer.Ordinal);
        foreach (var (name, value) in env) sb.Append('|').Append(name).Append('=').Append(value);
        // Hashed: the key holds secrets' values and must never be logged or kept in clear.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Stamp(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? f.FullName + ":" + f.Length + ":" + f.LastWriteTimeUtc.Ticks : path + ":absent"; }
        catch { return path + ":unavailable"; }
    }
}
