using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ClaudeRevit.Services;
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

        // Saved tools only auto-load at Revit startup, and only when code execution was on then.
        // If it's on now but nothing is loaded yet (e.g. the user just enabled it, or the loader
        // never ran), load the on-disk files now so the window is self-healing rather than blank.
        if (SettingsStore.AllowCodeExecution && DynamicToolLoader.ListCustom().Count == 0
            && DynamicToolLoader.ListSavedFiles().Count > 0)
        {
            try { DynamicToolLoader.LoadAll(); } catch { /* surfaced via the empty message below */ }
        }

        foreach (var (name, _) in DynamicToolLoader.ListCustom())
            ToolBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });

        if (ToolBox.Items.Count > 0) ToolBox.SelectedIndex = 0;
        else
        {
            RunButton.IsEnabled = false;
            var savedCount = DynamicToolLoader.ListSavedFiles().Count;
            if (!SettingsStore.AllowCodeExecution && savedCount > 0)
                DescriptionText.Text = $"You have {savedCount} saved tool(s), but code execution is off " +
                                       "so they aren't loaded. Enable 'Allow Claude to run code' in Settings " +
                                       "(gear icon) — they load automatically.";
            else if (savedCount > 0)
                DescriptionText.Text = $"{savedCount} saved tool file(s) found but none loaded — one may have " +
                                       "a compile error. Check %AppData%\\ClaudeRevit\\tools and the log.";
            else
                DescriptionText.Text = "No custom tools yet. These aren't pre-installed — Claude creates one " +
                                       "when you ask it to save a proven approach (e.g. \"save that as a tool\"). " +
                                       "Once saved, run it here with no model call (0 tokens), or Claude calls " +
                                       "it by name in chat.";
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
