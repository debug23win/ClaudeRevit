using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeRevit.Services;

// Stores API keys encrypted with DPAPI (CurrentUser scope) instead of plain-text
// environment variables. Keys encrypted on this machine can only be decrypted by the
// same Windows user. Two slots: the Anthropic key and the alternative-provider key.
public static class ApiKeyStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "apikey.bin");

    private static string AltFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "apikey-alt.bin");

    public static void Save(string apiKey) => SaveTo(FilePath, apiKey);

    // Clearing the key field in Settings must actually remove the stored key —
    // there is no other UI way to revoke a rotated/leaked key from this machine.
    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) { Log.Error("ApiKeyStore.Delete failed", ex); }
    }

    public static string? Load()
    {
        var stored = LoadFrom(FilePath);
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

    // ---- Alternative-provider key (DeepSeek / Qwen / OpenRouter…). An empty key is a
    // valid state: local Ollama / LM Studio need no key at all.

    public static void SaveAlt(string apiKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (File.Exists(AltFilePath)) File.Delete(AltFilePath);
                return;
            }
            SaveTo(AltFilePath, apiKey);
        }
        catch (Exception ex) { Log.Error("ApiKeyStore.SaveAlt failed", ex); }
    }

    public static string? LoadAlt() => LoadFrom(AltFilePath);

    // ---- The one DPAPI scheme both slots share.

    private static void SaveTo(string path, string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser));
    }

    private static string? LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(decrypted);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch
        {
            return null;
        }
    }
}
