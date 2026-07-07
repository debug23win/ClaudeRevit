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
    }

    private void SetStatus(string s)
    {
        _status = s;
        _phase.Restart();
        RenderStatus();
    }

    private void RenderStatus() => NowText.Text = $"{_status}  ·  {_phase.Elapsed.TotalSeconds:0}s";

    // The task subset to run — pick fewer to save tokens; you rarely need all 14 every time.
    private System.Collections.Generic.IReadOnlyList<BenchmarkTask> SelectedTasks()
    {
        var tag = (TaskSetBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        return tag switch
        {
            "basic" => BenchmarkTasks.All.Where(t => t.Id.StartsWith("B")).ToList(),
            "composite" => BenchmarkTasks.All.Where(t => t.Id.StartsWith("L")).ToList(),
            "domain" => BenchmarkTasks.All.Where(t => t.Id.StartsWith("R") || t.Id.StartsWith("S")).ToList(),
            "hard" => BenchmarkTasks.All.Where(t => t.Id is "L3" or "L4"
                        || t.Id.StartsWith("R") || t.Id.StartsWith("S")).ToList(),
            _ => BenchmarkTasks.All
        };
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var model = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var tasks = SelectedTasks();
        if (tasks.Count == 0) return;

        _results.Clear();
        SummaryText.Text = "Running…";
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        NowPanel.Visibility = Visibility.Visible;
        SetStatus("starting…");
        _tick.Start();
        _cts = new CancellationTokenSource();

        // Stamp passed in (Date.Now is fine in app code) so every row of one run shares a run id.
        var stamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var passes = 0; long tokens = 0; double seconds = 0; var graded = 0;

        int.TryParse(MaxRoundsBox.Text, out var maxRounds);
        int.TryParse(MaxMinutesBox.Text, out var maxMinutes);
        var maxSeconds = maxMinutes > 0 ? maxMinutes * 60 : 0;

        try
        {
            await BenchmarkRunner.RunAsync(
                model, tasks, judgeModel: "opus-4-8", runStamp: stamp,
                resetBetweenTasks: ResetBox.IsChecked == true,
                maxRoundsPerTask: maxRounds, maxSecondsPerTask: maxSeconds,
                judgeViaClaudeCode: JudgeViaCCBox.IsChecked == true,
                onStatus: SetStatus,
                onResult: r =>
                {
                    _results.Add(r);
                    tokens += r.Tokens;
                    if (double.TryParse(r.Time.TrimEnd('s'), out var s)) seconds += s;
                    if (r.Verdict != "?") graded++;
                    if (r.Verdict == "✓") passes++;
                    SummaryText.Text =
                        $"{_results.Count}/{tasks.Count} tasks · " +
                        $"{passes} passed{(graded < _results.Count ? $" ({_results.Count - graded} ungraded)" : "")} · " +
                        $"{tokens:N0} tokens · {seconds:0.0}s total";
                },
                ct: _cts.Token);

            SummaryText.Text = "Done — " + SummaryText.Text;
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
            CancelButton.IsEnabled = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}
