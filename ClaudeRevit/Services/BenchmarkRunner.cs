using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeRevit.Tools;
using ClaudeRevit.UI;

namespace ClaudeRevit.Services;

// One row of benchmark output: how a given model did on a given task.
public sealed class BenchmarkResult
{
    public string TaskId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Model { get; init; } = "";
    public string Verdict { get; init; } = "?";   // ✓ / ✗ / ? (judge unavailable)
    public int Score { get; init; }                 // 0–100 from the judge
    public int Rounds { get; init; }
    public long Tokens { get; init; }
    public string Time { get; init; } = "";         // "12.3s"
    public string Reason { get; init; } = "";
}

// Runs the benchmark tasks against a chosen model and grades each with an impartial fixed-model
// judge. Every task runs in an isolated (ephemeral) ChatService so it never touches the user's
// chat history. Results are appended to benchmark_results.jsonl for later comparison across runs.
public static class BenchmarkRunner
{
    private sealed record Verdict(bool Pass, int Score, string Reason, bool Graded);

    private static string ResultsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "benchmark_results.jsonl");

    public static async Task RunAsync(
        string modelTag,
        IReadOnlyList<BenchmarkTask> tasks,
        string judgeModel,
        string runStamp,
        bool resetBetweenTasks,
        int maxRoundsPerTask,
        int maxSecondsPerTask,
        bool judgeViaClaudeCode,
        bool runViaSubscription,
        Action<string> onStatus,
        Action<BenchmarkResult> onResult,
        CancellationToken ct)
    {
        // Suppress Revit's modal warning/task dialogs for the whole unattended run (covers the
        // gaps between turns, e.g. the reset deletions). Restored in the finally.
        ToolDispatcher.ForceSuppress = true;

        // "claudecode" tests the MCP path end to end: the local `claude` CLI (on the subscription)
        // drives the Revit tools through our MCP server. runViaSubscription does the same for any
        // chosen model — the picked model is passed to the CLI via --model.
        var isClaudeCode = modelTag == "claudecode" || runViaSubscription;
        // The picked model → Claude Code --model alias (null = the CLI's default subscription model).
        var ccModelAlias = modelTag == "claudecode" ? null : ClaudeCodeBackend.ModelAlias(modelTag);
        // The judge can run on the subscription too (same claude.exe, no tools) so grading costs
        // nothing on the API — needed since the account may sit at $0. It still grades from the
        // objective probe only, so it stays impartial.
        string ccConfig = "", ccWorkDir = "", ccExe = "";
        if (isClaudeCode || judgeViaClaudeCode)
        {
            ccExe = SettingsStore.ClaudeCodeExe;
            ccWorkDir = McpServer.ClientWorkDir();
        }
        if (isClaudeCode)
        {
            McpServer.Start();
            ccConfig = McpServer.WriteClientConfig();
        }
        var judgeChat = new ChatService(ephemeral: true);

        try
        {
        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            onStatus($"{task.Id} · {task.Title} · starting…");

            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (maxSecondsPerTask > 0) taskCts.CancelAfter(TimeSpan.FromSeconds(maxSecondsPerTask));

            var baselineIds = resetBetweenTasks
                ? new HashSet<long>(await ToolDispatcher.Instance.AllElementIdsAsync(ct))
                : new HashSet<long>();
            var before = await StatsAsync(ct);

            // The model runs the task ONCE and recovers from any errors on its own — no judge hints.
            string finalText = "";
            string? error = null;
            string? ccDiag = null;
            var budgetStopped = false;
            long inTok = 0, outTok = 0;
            var rounds = 0;
            var sw = Stopwatch.StartNew();
            var modelUsed = modelTag;

            if (isClaudeCode)
            {
                if (!McpServer.IsRunning)
                    error = "MCP server not running (enable it / free the port) — needed for Claude Code.";
                else
                {
                    var sb = new StringBuilder();
                    try
                    {
                        var res = await ClaudeCodeBackend.RunAsync(
                            ccExe, task.Prompt, ccWorkDir, ccConfig, resumeSessionId: null,
                            "mcp__clauderevit__*",
                            onText: t => sb.Append(t),
                            onTool: name => onStatus($"{task.Id} · {task.Title} · {name}"),
                            taskCts.Token, model: ccModelAlias);
                        finalText = !string.IsNullOrEmpty(res.Text) ? res.Text : sb.ToString();
                        error = res.Error;
                        inTok = res.InputTokens; outTok = res.OutputTokens; rounds = res.NumTurns;
                        modelUsed = ccModelAlias != null ? $"claude-code:{ccModelAlias}" : "claude-code";
                        // Surface WHY a run did nothing: MCP connection status + result flag. A launch
                        // with no tool calls and no tokens usually means MCP never connected.
                        ccDiag = $"MCP: {res.McpStatus ?? "no init event"}" +
                                 (res.IsError ? "; result=ERROR" : res.Subtype is { } st ? $"; result={st}" : "");
                        if ((res.McpStatus == null ||
                             res.McpStatus.IndexOf("connect", StringComparison.OrdinalIgnoreCase) < 0) &&
                            outTok == 0)
                            ccDiag += " — no Revit tools reached, nothing built";
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw;
                        budgetStopped = true; finalText = sb.ToString();
                    }
                }
            }
            else
            {
                var chat = new ChatService(ephemeral: true);
                var conv = new ObservableCollection<ChatMessage>();
                chat.OnRound = (r, max) =>
                {
                    onStatus($"{task.Id} · {task.Title} · round {r}/{max}");
                    if (maxRoundsPerTask > 0 && r > maxRoundsPerTask)
                        try { taskCts.Cancel(); } catch { }
                };
                try
                {
                    conv.Add(new ChatMessage { Role = "user", Text = task.Prompt });
                    await chat.SendAsync(conv, modelTag, taskCts.Token);
                    finalText = conv.LastOrDefault(m => m.Role == "assistant")?.Text ?? "";
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    budgetStopped = true;
                    finalText = conv.LastOrDefault(m => m.Role == "assistant")?.Text ?? "";
                }
                catch (Exception ex) { error = ex.Message; Log.Error($"Benchmark task {task.Id} threw", ex); }

                var mm = chat.LastTask;
                inTok = mm?.InputTokens ?? 0; outTok = mm?.OutputTokens ?? 0; rounds = mm?.Rounds ?? 0;
                modelUsed = mm?.Model ?? modelTag;
            }
            var seconds = sw.Elapsed.TotalSeconds;

            var after = await StatsAsync(ct);
            onStatus($"{task.Id} · {task.Title} · grading…");
            var verdict = error != null
                ? new Verdict(false, 0, "Run error: " + Truncate(error, 200), true)
                : await JudgeAsync(judgeChat, judgeModel, task, before, after, finalText,
                                   judgeViaClaudeCode, ccExe, ccWorkDir, ct);
            if (budgetStopped)
                verdict = verdict with { Reason = "[stopped — task budget hit] " + verdict.Reason };
            if (ccDiag != null)
                verdict = verdict with { Reason = "[" + ccDiag + "] " + verdict.Reason };

            if (resetBetweenTasks)
                await ResetAsync(baselineIds, ct);

            var result = new BenchmarkResult
            {
                TaskId = task.Id,
                Title = task.Title,
                Model = modelUsed,
                Verdict = !verdict.Graded ? "?" : verdict.Pass ? "✓" : "✗",
                Score = verdict.Score,
                Rounds = rounds,
                Tokens = inTok + outTok,
                Time = $"{seconds:0.0}s",
                Reason = verdict.Reason
            };

            Append(result, modelTag, runStamp, verdict.Pass, inTok, outTok, rounds, seconds);
            onResult(result);
        }
        }
        finally { ToolDispatcher.ForceSuppress = false; }
    }

    // Delete everything the task added (ids not present at baseline). Best-effort: Revit cascades
    // dependent deletions, and some elements (e.g. the last level) may refuse — those are left.
    private static async Task ResetAsync(HashSet<long> baselineIds, CancellationToken ct)
    {
        try
        {
            var now = await ToolDispatcher.Instance.AllElementIdsAsync(ct);
            var added = now.Where(id => !baselineIds.Contains(id)).ToArray();
            if (added.Length == 0) return;
            var input = new Dictionary<string, JsonElement>
            {
                ["element_ids"] = JsonSerializer.SerializeToElement(added)
            };
            await ToolDispatcher.Instance.ExecuteAsync("delete_elements", input, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error("Benchmark reset failed", ex); }
    }

    private static async Task<string> StatsAsync(CancellationToken ct)
    {
        // Precise benchmark probe (counts rebar, DirectShapes, connections, wall lengths, level
        // elevations, floor areas…) — not the coarse get_model_statistics, which can't see those.
        try { return await ToolDispatcher.Instance.BenchmarkProbeAsync(ct); }
        catch (Exception ex) { return "{\"probe_error\":\"" + ex.Message + "\"}"; }
    }

    private static async Task<Verdict> JudgeAsync(
        ChatService chat, string judgeModel, BenchmarkTask task,
        string before, string after, string finalText,
        bool viaClaudeCode, string ccExe, string ccWorkDir, CancellationToken ct)
    {
        const string sys =
            "You are an impartial QA grader for a Revit modelling agent. Grade ONLY from the objective " +
            "before/after PROBE — precise counts by the relevant categories (walls with lengths_m, floors " +
            "with areas_m2, roofs, levels with elevations_m, grids, structural_columns, structural_framing, rebar, " +
            "area_reinforcement, path_reinforcement, structural_connections, doors, direct_shapes with " +
            "bounding-box size_m, materials). Judge by the DELTA between before and after. Treat the agent's " +
            "own summary as an UNVERIFIED claim — trust the probe over it; if the probe can't confirm the " +
            "claim, do not give credit for it. Reply with ONLY a JSON object, no prose: " +
            "{\"pass\": true|false, \"score\": <0-100>, \"reason\": \"<one sentence citing the probe delta>\"}.";
        var user =
            $"TASK:\n{task.Prompt}\n\nPASS CRITERIA:\n{task.Criteria}\n\n" +
            $"OBJECTIVE PROBE BEFORE:\n{Truncate(before, 4000)}\n\n" +
            $"OBJECTIVE PROBE AFTER:\n{Truncate(after, 4000)}\n\n" +
            $"AGENT'S CLAIMED RESULT (unverified):\n{Truncate(finalText, 1000)}\n\nGrade now.";
        try
        {
            // On the subscription (no API cost) the CLI has no system-prompt flag in the same shape,
            // so fold the grader instructions into the prompt; ParseVerdict tolerates surrounding prose.
            var raw = viaClaudeCode
                ? await ClaudeCodeBackend.CompleteAsync(ccExe, sys + "\n\n" + user, ccWorkDir, ct)
                : await chat.RawCompleteAsync(judgeModel, sys, user, ct);
            return ParseVerdict(raw);
        }
        catch (Exception ex)
        {
            return new Verdict(false, 0, "Judge unavailable: " + Truncate(ex.Message, 160), false);
        }
    }

    private static Verdict ParseVerdict(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return new Verdict(false, 0, "Judge returned no JSON.", false);
        try
        {
            using var doc = JsonDocument.Parse(raw.Substring(start, end - start + 1));
            var r = doc.RootElement;
            var pass = r.TryGetProperty("pass", out var p) &&
                       (p.ValueKind == JsonValueKind.True ||
                        (p.ValueKind == JsonValueKind.String && p.GetString()?.ToLowerInvariant() == "true"));
            var score = r.TryGetProperty("score", out var s) && s.TryGetInt32(out var sv) ? sv : (pass ? 100 : 0);
            var reason = r.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
            return new Verdict(pass, score, reason, true);
        }
        catch { return new Verdict(false, 0, "Judge JSON parse failed.", false); }
    }

    private static void Append(BenchmarkResult r, string modelTag, string runStamp,
        bool pass, long inTok, long outTok, int rounds, double seconds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
            var record = new
            {
                ts = runStamp,
                task = r.TaskId,
                title = r.Title,
                model = modelTag,
                models_used = r.Model,
                verdict = r.Verdict,
                pass,
                score = r.Score,
                rounds,
                input_tokens = inTok,
                output_tokens = outTok,
                seconds,
                reason = r.Reason
            };
            File.AppendAllText(ResultsPath, JsonSerializer.Serialize(record) + "\n");
        }
        catch (Exception ex) { Log.Error("Benchmark append failed", ex); }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max) + "…";
}
