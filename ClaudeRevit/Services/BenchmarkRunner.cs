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

// Runs the benchmark tasks against a chosen model and grades each with an independent chosen
// judge. Every task runs in an isolated (ephemeral) ChatService so it never touches the user's
// chat history. Results are appended to benchmark_results.jsonl for later comparison across runs.
public static class BenchmarkRunner
{
    private static int _running;

    private static string ResultsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "benchmark_results.jsonl");

    public static async Task RunAsync(
        BenchmarkExecution execution, IReadOnlyList<BenchmarkTask> tasks,
        BenchmarkExecution judge, string runStamp, bool resetBetweenTasks,
        int maxRoundsPerTask, int maxSecondsPerTask,
        Action<string> onStatus, Action<BenchmarkResult> onResult, CancellationToken ct)
    {
        _ = execution.Tag; _ = judge.Tag; // validate before suppression or any model changes
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("A benchmark is already running.");
        var oldSuppression = ToolDispatcher.ForceSuppress;
        ToolDispatcher.ForceSuppress = true;
        try
        {
            var judgeChat = new ChatService(ephemeral: true);
            foreach (var task in tasks)
            {
                ct.ThrowIfCancellationRequested();
                var documentKey = DocumentSessions.CurrentDocumentKey;
                if (string.IsNullOrWhiteSpace(documentKey) || documentKey == "none") throw new InvalidOperationException("Open a disposable test model before running the benchmark.");
                onStatus($"{task.Id} · {task.Title} · starting…");
                using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (maxSecondsPerTask > 0) taskCts.CancelAfter(TimeSpan.FromSeconds(maxSecondsPerTask));
                var baseline = resetBetweenTasks ? new HashSet<long>(await ToolDispatcher.Instance.AllElementIdsAsync(ct)) : new();
                var cleanupAttempted = false;
                try
                {
                var before = await StatsAsync(ct);
                if (DocumentSessions.CurrentDocumentKey != documentKey) throw new InvalidOperationException("Active document changed before the benchmark task.");
                var chat = new ChatService(ephemeral: true) { SubscriptionMode = false };
                var conversation = new ObservableCollection<ChatMessage> { new() { Role = "user", Text = task.Prompt } };
                chat.OnRound = (round, max) =>
                {
                    onStatus($"{task.Id} · {task.Title} · tool round/call {round}");
                    if (maxRoundsPerTask > 0 && round > maxRoundsPerTask) taskCts.Cancel();
                };
                string? error = null;
                var budgetStopped = false;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    // Exactly the pane's MCP/channel/drain/model-validation path. Explicit API
                    // selections never consult the pane's subscription toggle or old overrides.
                    await chat.SendAsync(conversation, execution.Tag, taskCts.Token, mcpSelection: execution.Agent);
                    error = chat.LastRunError;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { budgetStopped = true; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { error = ex.Message; Log.Error($"Benchmark task {task.Id}", ex); }
                var seconds = stopwatch.Elapsed.TotalSeconds;
                var finalText = conversation.LastOrDefault(m => m.Role == "assistant")?.Text ?? "";
                if (DocumentSessions.CurrentDocumentKey != documentKey) throw new InvalidOperationException("Active document changed during the benchmark. Grading and cleanup stopped to protect the other document.");
                var after = await StatsAsync(ct);
                onStatus($"{task.Id} · {task.Title} · grading…");
                var verdict = error != null ? new BenchmarkVerdict(false, 0, "Run error: " + Truncate(error, 200), true)
                    : await JudgeAsync(judgeChat, judge, task, before, after, finalText, ct);
                if (budgetStopped) verdict = verdict with { Reason = "[task budget reached] " + verdict.Reason };
                if (resetBetweenTasks)
                {
                    cleanupAttempted = true;
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await ResetAsync(baseline, documentKey, cleanup.Token);
                }
                var metrics = chat.LastTask;
                var row = new BenchmarkResult
                {
                    TaskId = task.Id, Title = task.Title, Model = metrics?.Model ?? execution.Tag,
                    Verdict = !verdict.Graded ? "?" : verdict.Pass ? "✓" : "✗", Score = verdict.Score,
                    Rounds = metrics?.Rounds ?? 0, Tokens = (metrics?.InputTokens ?? 0) + (metrics?.OutputTokens ?? 0),
                    Time = seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s", Reason = verdict.Reason
                };
                Append(row, execution, judge, runStamp, verdict.Pass, metrics?.InputTokens ?? 0, metrics?.OutputTokens ?? 0, row.Rounds, seconds);
                onResult(row);
                }
                finally
                {
                    if (resetBetweenTasks && !cleanupAttempted)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await ResetAsync(baseline, documentKey, cleanup.Token);
                    }
                }
            }
        }
        finally { ToolDispatcher.ForceSuppress = oldSuppression; Interlocked.Exchange(ref _running, 0); }
    }

    // Cleanup adds (including types), never edits/deletions of pre-existing objects.
    // Inspect the actual committed/rolled-back cascade and protect the baseline.
    private static async Task ResetAsync(HashSet<long> baselineIds, string documentKey, CancellationToken ct)
    {
            if (DocumentSessions.CurrentDocumentKey != documentKey) throw new InvalidOperationException("Document changed; benchmark cleanup stopped.");
            var now = await ToolDispatcher.Instance.AllElementIdsAsync(ct);
            var added = now.Where(id => !baselineIds.Contains(id)).ToArray();
            if (added.Length == 0) return;
            var input = new Dictionary<string, JsonElement>
            {
                ["element_ids"] = JsonSerializer.SerializeToElement(added)
            };
            var raw = await ToolDispatcher.Instance.ExecuteAsync("delete_elements_checked", input, ct, documentKey);
            using var preview = JsonDocument.Parse(raw);
            if (!preview.RootElement.TryGetProperty("deleted_ids", out var ids)) throw new InvalidOperationException("Benchmark cleanup preview failed: " + Truncate(raw, 300));
            var deleted = ids.EnumerateArray().Select(i => i.GetInt64()).ToArray();
            BenchmarkCleanup.ValidateCascade(baselineIds, added, deleted);
            input["preview"] = JsonSerializer.SerializeToElement(false);
            input["document_key"] = JsonSerializer.SerializeToElement(documentKey);
            input["expected_deleted_ids"] = JsonSerializer.SerializeToElement(deleted);
            raw = await ToolDispatcher.Instance.ExecuteAsync("delete_elements_checked", input, ct, documentKey);
            using var applied = JsonDocument.Parse(raw);
            if (!applied.RootElement.TryGetProperty("applied", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Benchmark cleanup failed; stop before another task: " + Truncate(raw, 300));
    }

    private static async Task<string> StatsAsync(CancellationToken ct)
    {
        // Precise benchmark probe (counts rebar, DirectShapes, connections, wall lengths, level
        // elevations, floor areas…) — not the coarse get_model_statistics, which can't see those.
        try { return await ToolDispatcher.Instance.BenchmarkProbeAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return JsonSerializer.Serialize(new { probe_error = ex.Message }); }
    }

    private static async Task<BenchmarkVerdict> JudgeAsync(
        ChatService chat, BenchmarkExecution judge, BenchmarkTask task,
        string before, string after, string finalText,
        CancellationToken ct)
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
        try
        {
            before = BenchmarkGrading.SummarizeProbe(before);
            after = BenchmarkGrading.SummarizeProbe(after);
        }
        catch (Exception ex) { return new(false, 0, ex.Message, false); }
        var user =
            $"TASK:\n{task.Prompt}\n\nPASS CRITERIA:\n{task.Criteria}\n\n" +
            $"OBJECTIVE PROBE BEFORE:\n{before}\n\n" +
            $"OBJECTIVE PROBE AFTER:\n{after}\n\n" +
            $"AGENT'S CLAIMED RESULT (unverified):\n{Truncate(finalText, 1000)}\n\nGrade now.";
        try
        {
            // On the subscription (no API cost) the CLI has no system-prompt flag in the same shape,
            // so fold the grader instructions into the prompt; ParseVerdict tolerates surrounding prose.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var prompt = sys + "\n\n" + user;
            var raw = judge.Backend switch
            {
                "claudecode" => await ClaudeCodeBackend.CompleteAsync(SettingsStore.ClaudeCodeExe, prompt,
                    McpServer.ClientWorkDir(), timeout.Token, model: judge.Model, effort: judge.Effort),
                "codex" => await CodexBackend.CompleteAsync(prompt, McpServer.ClientWorkDir(), timeout.Token,
                    model: judge.Model, effort: judge.Effort, executable: SettingsStore.CodexExe),
                "api" => await chat.RawCompleteAsync(judge.Tag, sys, user, timeout.Token),
                _ => throw new InvalidOperationException("Unknown judge backend.")
            };
            return BenchmarkGrading.Parse(raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new BenchmarkVerdict(false, 0, "Judge unavailable: " + Truncate(ex.Message, 160), false);
        }
    }

    private static void Append(BenchmarkResult r, BenchmarkExecution execution, BenchmarkExecution judge, string runStamp,
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
                model = execution.Tag,
                backend = execution.Backend,
                requested_model = execution.Model,
                effort = execution.Effort,
                judge_backend = judge.Backend,
                judge_model = judge.Model,
                judge_effort = judge.Effort,
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
