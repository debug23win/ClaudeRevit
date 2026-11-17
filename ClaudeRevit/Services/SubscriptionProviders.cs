using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Parsing;

namespace ClaudeRevit.Services;

// The integration owns this profile. CLI-owned OAuth/model sections are preserved across login.
internal static class KimiSubscriptionConfiguration
{
    private static readonly object Gate = new();
    internal static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit", "providers", "kimi");
    internal const string ToolsSection = "[tools]\nenabled = [\"mcp__clauderevit__*\"]\ndisabled = []\n";
    private const string InitialConfig = "# Managed by ClaudeRevit. Use the Kimi login button in Settings.\n" +
        "default_permission_mode = \"manual\"\nbuiltin_product_skills = false\nauto_session_title = false\n\n" + ToolsSection +
        "\n[watch]\nenabled = false\n\n[workspace]\nrequire_trust = true\n";

    internal static string Prepare(string home, string workingDirectory)
    {
        if (!Path.IsPathFullyQualified(home) || !Path.IsPathFullyQualified(workingDirectory))
            throw new ArgumentException("Subscription profile and working directory must be absolute paths.");
        // A private cwd excludes unrelated project hooks/MCP/settings and retains session separation.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workingDirectory))))[..24];
        string workspace = Path.Combine(home, "workspaces", hash);
        lock (Gate)
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(workspace);
            string configPath = Path.Combine(home, "config.toml");
            if (File.Exists(configPath) && new FileInfo(configPath).Length > 1024 * 1024) throw new IOException("The Kimi Code configuration is too large.");
            bool existed = File.Exists(configPath);
            string original = existed ? File.ReadAllText(configPath) : InitialConfig;
            string config = ProtectTools(original);
            if (Regex.IsMatch(config, @"(?m)^\s*\[\[\s*hooks(?:[.\]\s])") || File.Exists(Path.Combine(home, "mcp.json")))
                throw new IOException("Extra hooks or MCP servers were found in ClaudeRevit's dedicated Kimi profile. Remove them before connecting.");
            if (!existed || original != config)
            {
                // External CLI login is not covered by this process lock; do not overwrite a change.
                if (existed && File.ReadAllText(configPath) != original)
                    throw new IOException("The Kimi configuration changed during connection. Finish logging in, then reconnect.");
                string temp = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temp, config, new UTF8Encoding(false)); File.Move(temp, configPath, overwrite: true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        return workspace;
    }

    internal static string ProtectTools(string config)
    {
        ValidateToml(config);
        // TOML accepts [tools], [ tools ], ["tools"] and ['tools']. Appending a second
        // table to an unrecognized legal spelling makes invalid TOML; some CLI config readers
        // then use defaults. Normalize exactly one tools table and reject ambiguous structure.
        var headers = Regex.Matches(config, @"(?m)^[ \t]*(?<open>\[\[?)(?<key>[^\r\n]+?)(?<close>\]\]?)[ \t]*(?:#[^\r\n]*)?\r?$")
            .Cast<Match>().ToArray();
        Match? tools = null;
        foreach (var header in headers)
        {
            var (root, nested) = TableRoot(header.Groups["key"].Value);
            if (root == "hooks") throw new IOException("Hooks are not allowed in ClaudeRevit's dedicated Kimi profile.");
            if (root != "tools") continue;
            if (tools != null || nested || header.Groups["open"].Value != "[" || header.Groups["close"].Value != "]")
                throw new IOException("The Kimi profile contains duplicate or nested tools tables. Use one [tools] table before connecting.");
            tools = header;
        }
        // An inline/dotted root assignment can already define the same table. Rewriting only its
        // header would not remove those keys and could produce a duplicate invalid definition.
        string topLevel = headers.Length == 0 ? config : config[..headers[0].Index];
        foreach (string line in topLevel.Split('\n'))
        {
            int equals = FindAssignment(line);
            if (equals < 0) continue;
            if (TableRoot(line[..equals]).Root == "tools")
                throw new IOException("Inline or dotted tools settings are not supported in ClaudeRevit's Kimi profile. Use one [tools] table.");
        }
        int end = tools == null ? config.Length : headers.FirstOrDefault(header => header.Index > tools.Index)?.Index ?? config.Length;
        string protectedConfig = tools == null ? config.TrimEnd() + "\n\n" + ToolsSection
            : config[..tools.Index] + ToolsSection + "\n" + config[end..];
        ValidateToml(protectedConfig);
        return protectedConfig;
    }

    private static readonly TomlSerializerOptions TomlOptions = new() { MaxDepth = 64 };
    private static void ValidateToml(string config)
    {
        // The official CLI continues with defaults when TOML cannot be parsed. Validate the entire
        // document before and after changing our tool section, so malformed unrelated sections
        // cannot silently erase the restriction. Never include parser diagnostics: values may be secrets.
        try
        {
            if (!SyntaxParser.Parse(config, TomlOptions).HasErrors) return;
        }
        catch (TomlException) { }
        throw new IOException("The Kimi profile contains invalid TOML. Fix its config.toml before connecting; the CLI was not started.");
    }

    private static (string Root, bool Nested) TableRoot(string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new IOException("The Kimi profile contains an empty TOML key.");
        int end;
        string root;
        if (key[0] == '\'' || key[0] == '"')
        {
            char quote = key[0]; bool escaped = false; end = 1;
            for (; end < key.Length; end++)
            {
                char value = key[end];
                if (quote == '"' && !escaped && value == '\\') { escaped = true; continue; }
                if (!escaped && value == quote) break;
                escaped = false;
            }
            if (end == key.Length) throw new IOException("The Kimi profile contains an unterminated TOML key.");
            try { root = quote == '\'' ? key[1..end] : JsonSerializer.Deserialize<string>(key[..(end + 1)])!; }
            catch (JsonException error) { throw new IOException("Unsupported escaped TOML key in the Kimi profile. Use plain table names.", error); }
            end++;
        }
        else
        {
            end = 0;
            while (end < key.Length && (char.IsAsciiLetterOrDigit(key[end]) || key[end] is '_' or '-')) end++;
            if (end == 0) throw new IOException("Invalid TOML key in the Kimi profile.");
            root = key[..end];
        }
        string remaining = key[end..].Trim();
        if (remaining.Length > 0 && remaining[0] != '.')
            throw new IOException("Ambiguous TOML key in the Kimi profile.");
        return (root, remaining.Length > 0);
    }

    private static int FindAssignment(string line)
    {
        char quote = '\0'; bool escaped = false;
        for (int index = 0; index < line.Length; index++)
        {
            char value = line[index];
            if (quote != '\0')
            {
                if (quote == '"' && !escaped && value == '\\') { escaped = true; continue; }
                if (!escaped && value == quote) quote = '\0';
                escaped = false;
            }
            else if (value == '#') return -1;
            else if (value is '\'' or '"') quote = value;
            else if (value == '=') return index;
        }
        return -1;
    }

    internal static void ApplyEnvironment(ProcessStartInfo info, string home)
    {
        SubscriptionProcess.RemoveProviderOverrides(info);
        info.Environment["KIMI_CODE_HOME"] = home;
        info.Environment["KIMI_CODE_WATCH"] = "0";
        info.Environment["KIMI_CODE_TRUST_WORKSPACE"] = "0";
        info.Environment["KIMI_CODE_BUILTIN_PRODUCT_SKILLS"] = "0";
        info.Environment["KIMI_CODE_BACKGROUND_KEEP_ALIVE_ON_EXIT"] = "0";
    }
}
