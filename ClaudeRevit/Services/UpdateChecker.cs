using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// Reports the running add-in version and checks GitHub for a newer release. Loaded DLLs can't
// self-replace while Revit is open, so this never installs anything — it surfaces "an update is
// available" plus the installer URL, and the user updates on their own terms. All network work is
// best-effort and non-fatal: a failed check just leaves LatestVersion null.
public static class UpdateChecker
{
    private const string Owner = "debug23win";
    private const string Repo = "clauderevit";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // "v1.97" — from the assembly version stamped at release time; "dev" for a local build (0.0.0).
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v == null || (v.Major == 0 && v.Minor == 0)) return "dev";
                return v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";
            }
            catch { return "dev"; }
        }
    }

    public sealed class Result
    {
        public string Current = "";
        public string? Latest;         // "v1.98"
        public bool UpdateAvailable;
        public string? DownloadUrl;    // the installer .exe asset, or the release page
        public string? Error;
    }

    // Query the latest release. Cached for the process lifetime after the first success so the pane
    // can call it freely.
    private static Result? _cached;

    public static async Task<Result> CheckAsync()
    {
        if (_cached is { UpdateAvailable: true }) return _cached;

        var result = new Result { Current = CurrentVersion };
        if (result.Current == "dev") { result.Error = "local build"; return result; }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("ClaudeRevit-UpdateChecker");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { result.Error = $"HTTP {(int)resp.StatusCode}"; return result; }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) { result.Error = "no tag"; return result; }

            result.Latest = tag;
            result.DownloadUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        a.TryGetProperty("browser_download_url", out var u))
                    { result.DownloadUrl = u.GetString(); break; }
                }

            result.UpdateAvailable = IsNewer(tag!, result.Current);
            if (result.UpdateAvailable) _cached = result;
        }
        catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }

    // Compare "v1.98" vs "v1.97" numerically, component by component. Unparseable → not newer.
    private static bool IsNewer(string latest, string current)
    {
        var a = Parse(latest);
        var b = Parse(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    private static int[] Parse(string v)
    {
        v = v.TrimStart('v', 'V');
        var parts = v.Split('.');
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            nums[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return nums;
    }
}
