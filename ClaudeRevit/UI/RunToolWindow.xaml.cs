using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ClaudeRevit.Tools;

namespace ClaudeRevit.UI;

// Runs a saved custom tool directly, with no model call (0 tokens). Lists the tools created
// with save_tool, takes their arguments as JSON, dispatches on Revit's API thread and shows
// the result. This is the "macro" path — the same learned tool, invoked by hand.
public partial class RunToolWindow : Window
{
    public RunToolWindow()
    {
        InitializeComponent();
        foreach (var (name, _) in DynamicToolLoader.ListCustom())
            ToolBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });

        if (ToolBox.Items.Count > 0) ToolBox.SelectedIndex = 0;
        else
        {
            DescriptionText.Text = "No custom tools yet. Create one by asking Claude to save a proven " +
                                   "pattern (save_tool), then run it here without spending tokens.";
            RunButton.IsEnabled = false;
        }
    }

    private void ToolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolBox.SelectedItem is ComboBoxItem item && item.Tag is string name)
            DescriptionText.Text = ToolRegistry.Instance.Get(name)?.Description ?? "";
    }

    // async void: must NOT block the UI thread — the dispatcher runs the tool on Revit's API
    // thread (the same UI thread) via an ExternalEvent, so a blocking .Wait() would deadlock.
    // await keeps the thread free for the event to fire.
    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (ToolBox.SelectedItem is not ComboBoxItem item || item.Tag is not string name)
            return;

        IReadOnlyDictionary<string, JsonElement> args;
        try
        {
            var json = string.IsNullOrWhiteSpace(ArgsBox.Text) ? "{}" : ArgsBox.Text;
            args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                   ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception ex)
        {
            ResultBox.Text = "✗ Arguments are not valid JSON: " + ex.Message;
            return;
        }

        RunButton.IsEnabled = false;
        ResultBox.Text = "Running…";
        try
        {
            ResultBox.Text = await ToolDispatcher.Instance.ExecuteAsync(name, args, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ResultBox.Text = "✗ " + (ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
