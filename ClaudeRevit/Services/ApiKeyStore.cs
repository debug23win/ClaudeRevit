using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeRevit.Services;

// Stores the Anthropic API key encrypted with DPAPI (CurrentUser scope) instead of a
// plain-text environment variable. Keys encrypted on this machine can only be
// decrypted by the same Windows user.
public static class ApiKeyStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "apikey.bin");

    public static void Save(string apiKey)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public static string? Load()
    {
        var stored = LoadEncrypted();
        if (stored != null) return stored;

        // Migrate from the legacy plain-text environment variable: re-save encrypted
        // and remove the User-scope variable so the key no longer sits in the open.
        var legacy = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(legacy)) return null;
        try
        {
            Save(legacy);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null, EnvironmentVariableTarget.User);
        }
        catch { /* migration is best-effort; the key still works this session */ }
        return legacy;
    }

    private static string? LoadEncrypted()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(decrypted);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch
        {
            return null;
        }
    }
}
