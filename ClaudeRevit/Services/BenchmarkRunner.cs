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
    public int? Quality { get; init; }
    public double? Speed { get; init; }
    public double? Score { get; init; }
    public double Seconds { get; init; }
    public int ReferenceSeconds { get; init; }
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
        Action<string> onStatus, Action<BenchmarkResult> onResult, CancellationToken ct, string? resultsPath = null)
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
                var before = await StatsAsync(task, false, documentKey, ct);
                var skip = BenchmarkEligibility.SkipReason(task, before);
                if (skip != null)
                {
                    var skipped = new BenchmarkResult { TaskId = task.Id, Title = task.Title, Model = execution.Tag, Verdict = "—", Reason = skip, ReferenceSeconds = task.ReferenceSeconds };
                    Append(skipped, execution, judge, runStamp, false, 0, 0, 0, 0, resultsPath, maxRoundsPerTask, maxSecondsPerTask, resetBetweenTasks);
                    onResult(skipped); continue;
                }
                var scopeStarted = false;
                try
                {
                if (resetBetweenTasks)
                {
                    documentKey = await ToolDispatcher.Instance.BenchmarkScopeAsync(true, documentKey, ct);
                    scopeStarted = true;
                    before = await StatsAsync(task, false, documentKey, ct);
                }
                if (DocumentSessions.CurrentDocumentKey != documentKey) throw new InvalidOperationException("Active document changed before the benchmark task.");
                var chat = new ChatService(ephemeral: true) { SubscriptionMode = false };
                var conversation = new ObservableCollection<ChatMessage> { new() { Role = "user", Text = task.Prompt +
                    "\nBenchmark: keep the current document active; do not save or close it, open another document, or change application/global settings. " +
                    "Use native tools and verify the actual result." } };
                chat.OnRound = (round, max) =>
                {
                    onStatus($"{task.Id} · {task.Title} · tool round/call {round}");
                    if (maxRoundsPerTask > 0 && round > maxRoundsPerTask) taskCts.Cancel();
                };
                string? error = null;
                var budgetStopped = false;
                if (maxSecondsPerTask > 0) taskCts.CancelAfter(TimeSpan.FromSeconds(maxSecondsPerTask));
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
                if (DocumentSessions.CurrentDocumentKey != documentKey) throw new InvalidOperationException("Active document changed during the benchmark. Grading stopped; only the owned scratch copy is closed without saving.");
                var after = await StatsAsync(task, true, documentKey, ct);
                onStatus($"{task.Id} · {task.Title} · grading…");
                var verdict = error != null ? new BenchmarkVerdict(false, 0, "Run error: " + Truncate(error, 200), true)
                    : await JudgeAsync(judgeChat, judge, task, before, after, finalText, ct);
                if (budgetStopped) verdict = verdict with { Reason = "[task budget reached] " + verdict.Reason };
                if (scopeStarted)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await ToolDispatcher.Instance.BenchmarkScopeAsync(false, documentKey, cleanup.Token);
                    scopeStarted = false;
                }
                var metrics = chat.LastTask;
                var points = BenchmarkScoring.Calculate(verdict, seconds, task.ReferenceSeconds);
                var row = new BenchmarkResult
                {
                    TaskId = task.Id, Title = task.Title, Model = metrics?.Model ?? execution.Tag,
                    Verdict = !verdict.Graded ? "?" : verdict.Pass ? "✓" : "✗",
                    Quality = points?.Quality, Speed = points?.Speed, Score = points?.Total,
                    Seconds = seconds, ReferenceSeconds = task.ReferenceSeconds,
                    Rounds = metrics?.Rounds ?? 0, Tokens = (metrics?.InputTokens ?? 0) + (metrics?.OutputTokens ?? 0),
                    Time = seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s", Reason = verdict.Reason
                };
                Append(row, execution, judge, runStamp, verdict.Pass, metrics?.InputTokens ?? 0, metrics?.OutputTokens ?? 0, row.Rounds, seconds, resultsPath, maxRoundsPerTask, maxSecondsPerTask, resetBetweenTasks);
                onResult(row);
                }
                finally
                {
                    if (scopeStarted)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await ToolDispatcher.Instance.BenchmarkScopeAsync(false, documentKey, cleanup.Token);
                    }
                }
            }
        }
        finally { ToolDispatcher.ForceSuppress = oldSuppression; Interlocked.Exchange(ref _running, 0); }
    }

    private static async Task<string> StatsAsync(BenchmarkTask task, bool completed, string documentKey, CancellationToken ct)
    {
        // Precise benchmark probe (counts rebar, DirectShapes, connections, wall lengths, level
        // elevations, floor areas…) — not the coarse get_model_statistics, which can't see those.
        try
        {
            var raw = await ToolDispatcher.Instance.BenchmarkProbeAsync(ct);
            using var initial = JsonDocument.Parse(raw);
            if (initial.RootElement.TryGetProperty("probe_error", out _) || !task.FamilyDocument ||
                !initial.RootElement.TryGetProperty("is_family_document", out var kind) || !kind.GetBoolean()) return raw;
            var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw)!;
            async Task Add(string key, string tool, Dictionary<string, JsonElement> input)
            {
                try
                {
                    var evidence = await ToolDispatcher.Instance.ExecuteAsync(tool, input, ct, documentKey);
                    using var parsed = JsonDocument.Parse(evidence);
                    values[key] = parsed.RootElement.Clone();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { values[key] = new { evidence_error = ex.Message }; }
            }
            await Add("family_structure", "analyze_family_structure", new()
                { ["max_depth"] = JsonSerializer.SerializeToElement(4), ["max_families"] = JsonSerializer.SerializeToElement(12) });
            if (completed && task.FlexScenarios != null)
            {
                var input = new Dictionary<string, JsonElement>();
                using var scenarios = JsonDocument.Parse(task.FlexScenarios);
                if (scenarios.RootElement.GetArrayLength() > 0) input["scenarios"] = scenarios.RootElement.Clone();
                await Add("independent_flex", "flex_family", input);
            }
            return JsonSerializer.Serialize(values);
        }
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
            "bounding-box size_m, materials), element identities/coordinates/host links, rebar centerlines/layouts, " +
            "schedule fields/rows, recursive family structure and independently executed family flex scenarios. " +
            "Judge by the DELTA between before and after. Unchanged element snapshots may be omitted explicitly. " +
            "Use 0-100 QUALITY ONLY: accuracy, completeness, native editability and successful size/variant tests. " +
            "Do not grade speed: it is calculated separately. Do not award credit for unavailable/truncated evidence. Treat the agent's " +
            "own summary as an UNVERIFIED claim — trust the probe over it; if the probe can't confirm the " +
            "claim, do not give credit for it. Reply with ONLY a JSON object, no prose: " +
            "{\"pass\": true|false, \"score\": <0-100>, \"reason\": \"<one sentence citing the probe delta>\"}.";
        try
        {
            (before, after) = BenchmarkGrading.SummarizePair(before, after);
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
        bool pass, long inTok, long outTok, int rounds, double seconds, string? resultsPath, int maxRounds, int maxSeconds, bool reset)
    {
        try
        {
            var path = resultsPath ?? ResultsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
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
                graded = r.Score.HasValue,
                skipped = r.Verdict == "—",
                pass,
                score = r.Score,
                quality_score = r.Quality,
                speed_score = r.Speed,
                scoring_version = BenchmarkScoring.Version,
                scoring_formula = "quality * (0.8 + 0.2 * min(1, reference_seconds / seconds))",
                reference_seconds = r.ReferenceSeconds,
                timing_scope = "modeller_and_tools_excluding_probe_judge_reset",
                task_suite_version = "v3.7.2",
                max_rounds = maxRounds,
                max_seconds = maxSeconds,
                reset_model = reset,
                rounds,
                input_tokens = inTok,
                output_tokens = outTok,
                seconds,
                reason = r.Reason
            };
            File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n");
        }
        catch (Exception ex) { Log.Error("Benchmark append failed", ex); }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max) + "…";
}
