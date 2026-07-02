using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class ChatPaneView : UserControl
{
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private readonly AnthropicChatService _service = new();
    private CancellationTokenSource? _cts;
    private string _selectedModel = "sonnet-5";

    public ChatPaneView()
    {
        InitializeComponent();
        DataContext = this;
        Messages.CollectionChanged += OnMessagesChanged;

        foreach (var m in HistoryStore.LoadUiMessages())
            Messages.Add(m);

        UsageTracker.Updated += UpdateUsageText;
        UpdateUsageText();

        SelectionService.Changed += OnSelectionChanged;
        OnSelectionChanged(SelectionService.Current);

        _service.ConfirmToolAsync = ConfirmToolAsync;
    }

    // Shows an Allow/Deny dialog before a destructive or arbitrary-code tool runs.
    private Task<bool> ConfirmToolAsync(string toolName, string input)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var header = toolName == "execute_csharp"
                ? "Claude wants to run C# code against your model:"
                : $"Claude wants to run '{toolName}' — this modifies your model:";
            var body = header + "\n\n" + input +
                       "\n\nAllow this operation? (You can always ⌃Z afterwards.)";
            var res = MessageBox.Show(
                Window.GetWindow(this), body, "Claude Revit — confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            tcs.SetResult(res == MessageBoxResult.Yes);
        }));
        return tcs.Task;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ChatMessage m in e.NewItems)
                m.PropertyChanged += (_, _) => ScheduleScroll();
        }
        ScheduleScroll();
    }

    private void ScheduleScroll() =>
        Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToBottom()), DispatcherPriority.Background);

    private void UpdateUsageText() =>
        Dispatcher.BeginInvoke(new Action(() => UsageText.Text = UsageTracker.Format()));

    private void OnSelectionChanged(SelectionService.SelectionInfo info)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (info.Ids.Count == 0)
            {
                SelectionPillBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                SelectionPillText.Text = "Selected: " + info.Description;
                SelectionPillBorder.Visibility = Visibility.Visible;
            }
        }));
    }

    // PreviewKeyDown, not KeyDown: with AcceptsReturn="True" the TextBox marks the
    // Enter KeyDown as handled internally, so a plain KeyDown handler never fires.
    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_cts == null) _ = SendAsync();
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) _cts.Cancel();
        else _ = SendAsync();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        Messages.Clear();
        _service.ClearHistory();
        UsageTracker.Reset();
        StatusText.Text = "";
        InputBox.Focus();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            _service.RecreateClient();
            StatusText.Text = "API key updated.";
        }
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelPicker.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            _selectedModel = tag;
    }

    private async Task SendAsync()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _cts != null) return;

        InputBox.Text = "";
        SendButton.Content = "Cancel";
        StatusText.Text = "Sending...";

        Messages.Add(new ChatMessage { Role = "user", Text = text });

        _cts = new CancellationTokenSource();
        try
        {
            await _service.SendAsync(Messages, _selectedModel, _cts.Token);
            StatusText.Text = "";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage { Role = "assistant", Text = $"[Error: {ex.Message}]" });
            StatusText.Text = "Error";
        }
        finally
        {
            _service.SaveHistory(Messages);
            _cts?.Dispose();
            _cts = null;
            SendButton.Content = "Send";
            InputBox.Focus();
        }
    }
}
