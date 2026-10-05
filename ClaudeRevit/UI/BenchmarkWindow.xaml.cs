using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class BenchmarkWindow : Window
{
    private readonly ObservableCollection<BenchmarkResult> _results = new();
    private CancellationTokenSource? _cts;

    // Live "now running" status. The stopwatch is reset on every status change; the DispatcherTimer
    // re-renders the elapsed seconds twice a second. That ticking clock is the liveness signal — if
    // it keeps advancing the UI thread is alive; if it freezes, Revit is stuck on a heavy op.
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _phase = new();
    private string _status = "idle";

    public BenchmarkWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => RenderStatus();
        _ready = true;
        Loaded += async (_, _) => await RefreshModelsAsync();
        Closed += (_, _) => { _cts?.Cancel(); _lifetime.Cancel(); };

    }

    private bool _ready;
    private int _loading;
    private readonly CancellationTokenSource _lifetime = new();
    private sealed record ModelOption(string Id, string Label, System.Collections.Generic.IReadOnlyList<string> Efforts, string DefaultEffort);
    private static string Backend(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "codex";
    private static BenchmarkExecution Selection(ComboBox backend, ComboBox model, ComboBox effort)
    {
        var option = model.SelectedItem as ModelOption;
        var id = option != null && model.Text == option.Label ? option.Id : model.Text.Trim();
        return new(Backend(backend), id.Length == 0 ? null : id,
            effort.SelectedItem as string is { Length: > 0 } level ? level : null);
    }
    private async void Backend_Changed(object sender, SelectionChangedEventArgs e)
    { if (_ready && _cts == null) await RefreshModelsAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    { if (_cts == null) await RefreshModelsAsync(); }
    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var box = (ComboBox)sender;
        var effort = box == ModelBox ? EffortBox : JudgeEffortBox;
        var option = box.SelectedItem as ModelOption;
        effort.ItemsSource = option?.Efforts ?? System.Array.Empty<string>();
        effort.SelectedItem = option?.DefaultEffort;
        effort.IsEnabled = _cts == null && (option?.Efforts.Count ?? 0) > 0;
    }
    private async Task RefreshModelsAsync()
    {
        if (_cts != null) return;
        var version = ++_loading;
        RunButton.IsEnabled = false;
        ModelStatusText.Text = "Loading available models…";
        try
        {
            System.Collections.Generic.IReadOnlyList<CodexModel> codex = System.Array.Empty<CodexModel>();
            if (Backend(BackendBox) == "codex" || Backend(JudgeBackendBox) == "codex")
                codex = await CodexModelCatalog.ReadAsync(SettingsStore.CodexExe, McpServer.ClientWorkDir(), _lifetime.Token, forceRefresh: true);
            if (version != _loading || _lifetime.IsCancellationRequested) return;
            void Fill(ComboBox backend, ComboBox box)
            {
                var old = (box.SelectedItem as ModelOption)?.Id;
                var options = Backend(backend) switch
                {
                    "codex" => codex.Select(m => new ModelOption(m.Id, m.Name, m.Efforts, m.DefaultEffort)).ToList(),
                    "claudecode" => new[] { "", "opus", "sonnet", "haiku" }.Select(id => new ModelOption(id, id == "" ? "CLI default" : id,
                        new[] { "low", "medium", "high" }, "high")).ToList(),
                    _ => new[] { "auto", "sonnet-5", "opus-4-8", "opus-5", "haiku-4-5", "alt" }
                        .Select(id => new ModelOption(id, id, System.Array.Empty<string>(), "")).ToList()
                };
                box.ItemsSource = options;
                box.SelectedItem = options.FirstOrDefault(m => m.Id == old) ??
                    options.FirstOrDefault(m => codex.Any(c => c.IsDefault && c.Id == m.Id)) ?? options.FirstOrDefault();
            }
            Fill(BackendBox, ModelBox); Fill(JudgeBackendBox, JudgeBox);
            ModelStatusText.Text = "";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _loading) ModelStatusText.Text = ex.Message; }
        finally { if (version == _loading && !_lifetime.IsCancellationRequested) RunButton.IsEnabled = true; }
    }

    private void SetStatus(string s)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => SetStatus(s))); return; }
        _status = s;
        _phase.Restart();
        RenderStatus();
    }

    private void RenderStatus() => NowText.Text = $"{_status}  ·  {_phase.Elapsed.TotalSeconds:0}s";

    // Select the same task suite and seed document for comparable runs.
    private System.Collections.Generic.IReadOnlyList<BenchmarkTask> SelectedTasks()
    {
        var tag = (TaskSetBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        return tag switch
        {
            "basic" => BenchmarkTasks.All.Where(t => t.Id.StartsWith("B")).ToList(),
            "composite" => BenchmarkTasks.All.Where(t => t.Id.StartsWith("L")).ToList(),
            "domain" => BenchmarkTasks.All.Where(t => t.Id.StartsWith("R") || t.Id.StartsWith("S")).ToList(),
            "docs" => BenchmarkTasks.All.Where(t => t.Id.StartsWith("D")).ToList(),
            "family" => BenchmarkTasks.All.Where(t => t.FamilyDocument).ToList(),
            // The discriminators: multi-step chains where a wrong intermediate result only shows at
            // the end, plus the domain tasks.
            "hard" => BenchmarkTasks.All.Where(t => t.Id is "L3" or "L4"
                        || t.Id.StartsWith("R") || t.Id.StartsWith("S")
                        || t.Id.StartsWith("D") || t.FamilyDocument || t.Id == "L5").ToList(),
            _ => BenchmarkTasks.All
        };
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var execution = Selection(BackendBox, ModelBox, EffortBox);
        var judge = Selection(JudgeBackendBox, JudgeBox, JudgeEffortBox);
        if (ModelStatusText.Text.Length > 0 && (execution.Backend == "codex" || judge.Backend == "codex"))
        { SummaryText.Text = "Refresh the Codex model catalog before running."; return; }
        if (!int.TryParse(RepeatBox.Text, out var repeats) || repeats is <1 or >10) { SummaryText.Text="Use 1..10 repetitions."; return; }
        if (repeats>1 && ResetBox.IsChecked!=true) { SummaryText.Text="Repeated comparisons require fresh document copies."; return; }
        var tasks = Enumerable.Range(0,repeats).SelectMany(_=>SelectedTasks()).ToList();
        if (tasks.Count == 0) return;
        if (!int.TryParse(MaxRoundsBox.Text, out var maxRounds) || maxRounds is < 0 or > 1000 ||
            !int.TryParse(MaxMinutesBox.Text, out var maxMinutes) || maxMinutes is < 0 or > 600)
        { SummaryText.Text = "Use 0..1000 rounds/tool calls and 0..600 minutes (0 = unlimited)."; return; }

        _results.Clear();
        SummaryText.Text = "Running…";
        RunButton.IsEnabled = false;
        BackendBox.IsEnabled = JudgeBackendBox.IsEnabled = ModelBox.IsEnabled = JudgeBox.IsEnabled = EffortBox.IsEnabled = JudgeEffortBox.IsEnabled = false;
        CancelButton.IsEnabled = true;
        NowPanel.Visibility = Visibility.Visible;
        SetStatus("starting…");
        _tick.Start();
        _cts = new CancellationTokenSource();

        // Stamp passed in (Date.Now is fine in app code) so every row of one run shares a run id.
        var stamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var passes = 0; long tokens = 0; double seconds = 0; var graded = 0; var skipped = 0;
        double qualitySum = 0, speedSum = 0, scoreSum = 0;

        var maxSeconds = maxMinutes > 0 ? maxMinutes * 60 : 0;

        try
        {
            await BenchmarkRunner.RunAsync(
                execution, tasks, judge,
                runStamp: stamp,
                resetBetweenTasks: ResetBox.IsChecked == true,
                maxRoundsPerTask: maxRounds, maxSecondsPerTask: maxSeconds,
                onStatus: SetStatus,
                onResult: r =>
                {
                    _results.Add(r);
                    tokens += r.Tokens;
                    seconds += r.Seconds;
                    if (r.Score != null) { graded++; qualitySum += r.Quality ?? 0; speedSum += r.Speed ?? 0; scoreSum += r.Score.Value; }
                    if (r.Verdict == "—") skipped++;
                    if (r.Verdict == "✓") passes++;
                    SummaryText.Text =
                        $"{_results.Count}/{tasks.Count} tasks · " +
                        $"{passes}/{graded} passed · {skipped} skipped · {_results.Count - graded - skipped} ungraded · " +
                        (graded > 0 ? $"Quality {qualitySum / graded:0.0} · Speed {speedSum / graded:0.0} · Total {scoreSum / graded:0.0}/100 · " : "") +
                        $"{tokens:N0} tokens · {seconds:0.0}s";
                },
                ct: _cts.Token);

            var distributions=BenchmarkStatistics.Group(_results);
            SummaryText.Text = "Done — " + SummaryText.Text;
            SummaryText.ToolTip=string.Join("\n",distributions.Select(g=>$"{g.Key.Split('|')[0]}: n={g.Distribution.Count}, median {g.Distribution.Median:0.0}s, p95 {g.Distribution.P95:0.0}s, pass {g.Distribution.PassRate:0.0}%"));
            BenchmarkRunner.WriteSummary(_results);
        }
        catch (OperationCanceledException) { SummaryText.Text = "Stopped. " + SummaryText.Text; }
        catch (Exception ex) { SummaryText.Text = "Error: " + ex.Message; }
        finally
        {
            _tick.Stop();
            NowPanel.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
            RunButton.IsEnabled = true;
            BackendBox.IsEnabled = JudgeBackendBox.IsEnabled = ModelBox.IsEnabled = JudgeBox.IsEnabled = true;
            EffortBox.IsEnabled = EffortBox.Items.Count > 0;
            JudgeEffortBox.IsEnabled = JudgeEffortBox.Items.Count > 0;
            CancelButton.IsEnabled = false;
        }
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_cts!=null) return;
        try { BenchmarkCalibration.Save(_results); SummaryText.Text="Calibration saved: "+BenchmarkCalibration.PathName; }
        catch(Exception ex) { SummaryText.Text=ex.Message; }
    }
    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}
