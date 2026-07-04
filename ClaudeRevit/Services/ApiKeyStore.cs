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

    // ---- Alternative-provider key (DeepSeek / Qwen / OpenRouter…), same DPAPI scheme.
    // An empty key is a valid state: local Ollama / LM Studio need no key at all.

    private static string AltFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "apikey-alt.bin");

    public static void SaveAlt(string apiKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (File.Exists(AltFilePath)) File.Delete(AltFilePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(AltFilePath)!);
            File.WriteAllBytes(AltFilePath, ProtectedData.Protect(
                Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { Log.Error("ApiKeyStore.SaveAlt failed", ex); }
    }

    public static string? LoadAlt()
    {
        try
        {
            if (!File.Exists(AltFilePath)) return null;
            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(AltFilePath), null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(decrypted);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch
        {
            return null;
        }
    }
}
