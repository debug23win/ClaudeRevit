using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class BenchmarkWindow : Window
{
    private readonly ObservableCollection<BenchmarkResult> _results = new();
    private CancellationTokenSource? _cts;

    public BenchmarkWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var model = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

        _results.Clear();
        SummaryText.Text = "Running…";
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _cts = new CancellationTokenSource();

        // Stamp passed in (Date.Now is fine in app code) so every row of one run shares a run id.
        var stamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var passes = 0; long tokens = 0; double seconds = 0; var graded = 0;

        try
        {
            await BenchmarkRunner.RunAsync(
                model, BenchmarkTasks.All, judgeModel: "opus-4-8", runStamp: stamp,
                onResult: r =>
                {
                    _results.Add(r);
                    tokens += r.Tokens;
                    if (double.TryParse(r.Time.TrimEnd('s'), out var s)) seconds += s;
                    if (r.Verdict != "?") graded++;
                    if (r.Verdict == "✓") passes++;
                    SummaryText.Text =
                        $"{_results.Count}/{BenchmarkTasks.All.Count} tasks · " +
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
