using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class ChatPaneView : UserControl
{
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private readonly ChatService _service = new();
    private CancellationTokenSource? _cts;
    private string _selectedModel = "sonnet-5";

    // An image attached for the next message (base64 + MIME), downscaled on attach.
    private string? _pendingImageBase64;
    private string? _pendingImageMime;

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

        UpdateAltModelLabel();

        // Safety net: an unhandled exception on the WPF dispatcher normally takes the
        // whole Revit process down. Log every one; and if the fault originates in OUR
        // code, mark it handled so the chat pane can never crash Revit. Exceptions from
        // Revit itself or other add-ins are logged but left to their normal handling.
        Dispatcher.UnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var trace = e.Exception.ToString();
        Log.Error("Dispatcher unhandled exception (RenderMode/UI thread)", e.Exception);
        if (trace.Contains("ClaudeRevit"))
        {
            e.Handled = true;
            try
            {
                Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Text = "[Internal error in the Claude pane was caught and suppressed so Revit " +
                           "stays up: " + e.Exception.Message + ". Details logged to " +
                           "%AppData%\\ClaudeRevit\\log.txt.]"
                });
            }
            catch { }
        }
    }

    // Shows an Allow/Deny dialog before a destructive or arbitrary-code tool runs.
    // NB: this pane is a Revit dockable pane, so Window.GetWindow(this) is null —
    // passing that null as the MessageBox owner throws ArgumentNullException and
    // (before the dispatcher net) took Revit down. Call the ownerless overload.
    private Task<bool> ConfirmToolAsync(string toolName, string input)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var header = toolName == "execute_csharp"
                    ? "Claude wants to run C# code against your model:"
                    : toolName == "run_dynamo_python"
                        ? "Claude wants to run Python (via Dynamo) against your model:"
                        : $"Claude wants to run '{toolName}' — this modifies your model:";
                var body = header + "\n\n" + Truncate(input, 2000) +
                           "\n\nAllow this operation? (You can always ⌃Z afterwards.)";
                var res = MessageBox.Show(
                    body, "Claude Revit — confirm",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                tcs.SetResult(res == MessageBoxResult.Yes);
            }
            catch (Exception ex)
            {
                // Never let the confirmation dialog fault the turn — deny on error.
                Log.Error("ConfirmToolAsync dialog failed", ex);
                tcs.SetResult(false);
            }
        }));
        return tcs.Task;
    }

    private static string Truncate(string s, int max) => TextUtil.Truncate(s, max);

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ChatMessage m in e.NewItems)
                m.PropertyChanged += (_, _) => ScheduleScroll();
        }
        ScheduleScroll();
    }

    // Keep the newest message visible. ScrollIntoView on the virtualizing ListBox only
    // touches the viewport, so this stays cheap no matter how long the history is.
    private void ScheduleScroll() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Messages.Count > 0)
                MessagesList.ScrollIntoView(Messages[Messages.Count - 1]);
        }), DispatcherPriority.Background);

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

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach an image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _pendingImageBase64 = LoadDownscaledJpeg(dlg.FileName);
            _pendingImageMime = "image/jpeg";
            AttachButton.Content = "📎✓";
            StatusText.Text = "Image attached — it goes with your next message.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't read image: " + ex.Message;
        }
    }

    // Load an image, downscale so the longest side is ≤ 1568px (Anthropic's guidance), and
    // re-encode as JPEG — keeps the base64 small so it doesn't blow the token budget.
    private static string LoadDownscaledJpeg(string path)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(path);
        src.EndInit();

        var longest = Math.Max(src.PixelWidth, src.PixelHeight);
        BitmapSource bmp = longest > 1568
            ? new TransformedBitmap(src, new ScaleTransform(1568.0 / longest, 1568.0 / longest))
            : src;

        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private void RunToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SettingsStore.AllowCodeExecution)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Custom tools run arbitrary code — enable 'Allow Claude to run code' in Settings (⚙) first.",
                "Claude Revit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new RunToolWindow();
        var owner = Window.GetWindow(this);
        if (owner != null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow();
        var owner = Window.GetWindow(this);
        if (owner != null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() == true)
        {
            _service.RecreateClient();
            UpdateAltModelLabel();
            StatusText.Text = "Settings saved.";
        }
    }

    // The "Alt" picker entry shows which model it currently points at.
    private void UpdateAltModelLabel() =>
        AltModelItem.Content = SettingsStore.AltModel.Length > 0
            ? "Alt: " + SettingsStore.AltModel
            : "Alt model (set up in ⚙)";

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelPicker.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            _selectedModel = tag;
    }

    private async Task SendAsync()
    {
        var text = InputBox.Text.Trim();
        if ((string.IsNullOrEmpty(text) && _pendingImageBase64 == null) || _cts != null) return;
        if (string.IsNullOrEmpty(text)) text = "(see attached image)";

        // Take and clear the pending image so it rides with THIS message only.
        var image = _pendingImageBase64;
        var imageMime = _pendingImageMime;
        _pendingImageBase64 = null;
        _pendingImageMime = null;
        AttachButton.Content = "📎";

        InputBox.Text = "";
        SendButton.Content = "Cancel";
        StatusText.Text = "Sending...";

        // Live round counter so long jobs show progress toward the per-message cap.
        _service.OnRound = (r, max) => StatusText.Text = $"Working… round {r}/{max}";

        Messages.Add(new ChatMessage { Role = "user", Text = image != null ? text + "  📎" : text });

        _cts = new CancellationTokenSource();
        try
        {
            await _service.SendAsync(Messages, _selectedModel, _cts.Token, image, imageMime);
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
