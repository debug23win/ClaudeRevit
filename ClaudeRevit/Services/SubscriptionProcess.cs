using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// Resolve npm's official package entry point without sending paths or prompts through cmd.exe.
internal static class SubscriptionProcess
{
    internal static ProcessStartInfo Create(SubscriptionProviderOptions options, IReadOnlyList<string> arguments,
        string package, string command, bool interactive = false)
    {
        string executable = ResolveExecutable(options.Executable);
        string? entryPoint = null;
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            (executable, entryPoint) = ResolveNpmEntry(executable, package, command);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = options.WorkingDirectory, UseShellExecute = false, CreateNoWindow = !interactive,
            RedirectStandardInput = !interactive, RedirectStandardOutput = !interactive, RedirectStandardError = !interactive
        };
        if (!interactive)
        {
            info.StandardInputEncoding = new UTF8Encoding(false);
            info.StandardOutputEncoding = info.StandardErrorEncoding = Encoding.UTF8;
        }
        if (entryPoint != null) info.ArgumentList.Add(entryPoint);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        RemoveProviderOverrides(info);
        return info;
    }

    internal static void RemoveProviderOverrides(ProcessStartInfo info)
    {
        // Account OAuth remains in each CLI's credential store. Neither an inherited API key nor
        // an alternate base URL may silently change the requested subscription billing route.
        var prefixes = new[] { "OPENAI_", "ANTHROPIC_", "QWEN_", "DASHSCOPE_", "KIMI_", "MOONSHOT_", "DEEPSEEK_", "PERPLEXITY_" };
        foreach (var name in info.Environment.Keys.ToArray())
            if (!name.Equals("KIMI_SHELL_PATH", StringComparison.OrdinalIgnoreCase) &&
                (prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                name.Equals("GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase) || name.Equals("GOOGLE_API_KEY", StringComparison.OrdinalIgnoreCase)))
                info.Environment.Remove(name);
    }

    internal static string ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new IOException("No CLI executable was selected.");
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            string full = Path.GetFullPath(executable);
            if (!File.Exists(full)) throw new IOException("CLI executable was not found: " + full);
            return full;
        }
        var extensions = OperatingSystem.IsWindows() && Path.GetExtension(executable).Length == 0
            ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows())
        {
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
        }
        foreach (var directory in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var extension in extensions)
        {
            string path = Path.Combine(directory.Trim('"'), executable + extension);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new IOException("CLI executable was not found: " + executable + ". Install the official CLI and select its full path in Settings.");
    }

    internal static (string Executable, string EntryPoint) ResolveNpmEntry(string shim, string package, string command)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(shim))!;
        string packageDirectory = Path.Combine(directory, "node_modules", package.Replace('/', Path.DirectorySeparatorChar));
        string manifest = Path.Combine(packageDirectory, "package.json");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 128 * 1024)
            throw new IOException("The installed official npm package was not found beside " + shim + ": " + package);
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        var bin = SubscriptionJson.Child(json.RootElement, "bin");
        string? relative = bin.ValueKind == JsonValueKind.String ? bin.GetString() : SubscriptionJson.Text(bin, command);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new IOException("The npm package did not declare a valid CLI entry point: " + package);
        string entry = Path.GetFullPath(Path.Combine(packageDirectory, relative));
        string root = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        if (!entry.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(entry))
            throw new IOException("The CLI entry point is missing or lies outside the installed npm package.");
        string localNode = Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node");
        string node = File.Exists(localNode) ? localNode : ResolveExecutable(OperatingSystem.IsWindows() ? "node.exe" : "node");
        return (node, entry);
    }

    internal static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
    }

    internal static async Task FinishAsync(Process process, Task stderr)
    {
        Stop(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
        try { await stderr.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or IOException or ObjectDisposedException) { }
    }

    internal static async Task<string> DrainErrors(Process process)
    {
        // Drain bytes continuously with a bounded retained tail; ReadLineAsync alone allows a
        // malformed child to allocate an unbounded single stderr line.
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var retained = new StringBuilder();
        int count;
        while ((count = await process.StandardError.BaseStream.ReadAsync(bytes).ConfigureAwait(false)) != 0)
        {
            int length = decoder.GetChars(bytes, 0, count, chars, 0);
            if (retained.Length < 8192) retained.Append(chars, 0, Math.Min(length, 8192 - retained.Length));
        }
        return retained.ToString();
    }
}
