using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private string _selectedModel = "auto";
    private string _selectedAgent = "api";
    private bool _settingChoices;
    private bool _choicesReady;
    private bool _refreshingModels;
    private IReadOnlyList<CodexModel> _codexModels = Array.Empty<CodexModel>();
    private sealed record Choice(string Id, string Label);
    private static string L(string english, string russian) =>
        (SettingsStore.UiLanguage == "ru" || (SettingsStore.UiLanguage.Length == 0 && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"))
            ? russian : english;

    // An image attached for the next message (base64 + MIME), downscaled on attach.
    private string? _pendingImageBase64;
    private string? _pendingImageMime;

    public ChatPaneView()
    {
        InitializeComponent();
        _choicesReady = true;
        _settingChoices = true;
        _selectedAgent = SettingsStore.ChatAgent;
        AgentPicker.SelectedItem = AgentPicker.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == _selectedAgent);
        _settingChoices = false;
        ApplyAgentChoices();
        Loaded += async (_, _) => { if (_selectedAgent == "codex" && _codexModels.Count == 0) await RefreshModelsAsync(); };
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
        UpdateAssistantLabel();

        // Safety net: an unhandled exception on the WPF dispatcher normally takes the
        // whole Revit process down. Log every one; and if the fault originates in OUR
        // code, mark it handled so the chat pane can never crash Revit. Exceptions from
        // Revit itself or other add-ins are logged but left to their normal handling.
        Dispatcher.UnhandledException += OnDispatcherUnhandledException;

        _ = CheckForUpdateAsync();
    }

    private string? _updateUrl;

    // Best-effort, non-blocking: if GitHub has a newer release, reveal the footer link. Loaded DLLs
    // can't self-replace while Revit is open, so we only point the user at the installer.
    private async Task CheckForUpdateAsync()
    {
        try
        {
            var r = await UpdateChecker.CheckAsync();
            if (!r.UpdateAvailable || r.DownloadUrl == null) return;
            _updateUrl = r.DownloadUrl;
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateNotice.Text = $"⬆ {r.Latest} available";
                UpdateNotice.Visibility = Visibility.Visible;
            });
        }
        catch { /* never let an update check disturb the pane */ }
    }

    private void UpdateNotice_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateUrl)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Opening update URL failed", ex); }
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

    private void BenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        // Non-modal (Show, not ShowDialog): the benchmark drives real tool calls through the
        // ToolDispatcher external event, which only fires while Revit's main thread is idle — a
        // modal dialog would keep the thread in a nested loop and deadlock the run.
        var dlg = new BenchmarkWindow();
        var owner = Window.GetWindow(this);
        if (owner != null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.Show();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var codeWasOn = SettingsStore.AllowCodeExecution;
        var dlg = new SettingsWindow();
        var owner = Window.GetWindow(this);
        if (owner != null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() == true)
        {
            _service.RecreateClient();
            UpdateAltModelLabel();
            UpdateAssistantLabel();
            _codexModels = Array.Empty<CodexModel>();
            ApplyAgentChoices();

            // Turning code execution on now loads any previously-saved custom tools without a
            // Revit restart (LoadAll otherwise only runs at startup, so saved tools would stay
            // invisible until relaunch).
            if (!codeWasOn && SettingsStore.AllowCodeExecution)
            {
                try { Tools.DynamicToolLoader.LoadAll(); } catch (Exception ex) { Log.Error("Reload custom tools failed", ex); }
            }

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
        {
            _selectedModel = tag;
            UpdateAssistantLabel();
        }
    }

    private async void AgentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_choicesReady || _settingChoices || AgentPicker.SelectedItem is not ComboBoxItem item) return;
        _selectedAgent = (string)item.Tag;
        SettingsStore.ChatAgent = _selectedAgent;
        ApplyAgentChoices();
        UpdateAssistantLabel();
        if (_selectedAgent == "codex" && _codexModels.Count == 0) await RefreshModelsAsync();
    }

    private void ApplyAgentChoices()
    {
        if (!_choicesReady) return;
        _settingChoices = true;
        ModelPicker.Visibility = _selectedAgent == "api" ? Visibility.Visible : Visibility.Collapsed;
        McpChoices.Visibility = _selectedAgent == "api" ? Visibility.Collapsed : Visibility.Visible;
        RefreshModelsButton.Visibility = _selectedAgent == "codex" ? Visibility.Visible : Visibility.Collapsed;
        RefreshModelsButton.Content = L("Refresh models", "Обновить модели");
        McpModelPicker.ToolTip = L("Choose a model or type its full ID. Automatic uses the agent default.", "Выберите модель или введите её полный ID. «Автоматически» использует модель агента по умолчанию.");
        McpEffortPicker.ToolTip = L("Reasoning effort for this model.", "Глубина рассуждений для выбранной модели.");
        if (_selectedAgent != "api")
        {
            var selection = SettingsStore.GetAgentSelection(_selectedAgent);
            var choices = new List<Choice> { new("", L("Automatic", "Автоматически")) };
            if (_selectedAgent == "codex") choices.AddRange(_codexModels.Select(m => new Choice(m.Id, m.Name)));
            else choices.AddRange(new[] { new Choice("sonnet", "Claude Sonnet"), new Choice("opus", "Claude Opus"), new Choice("haiku", "Claude Haiku") });
            if (!string.IsNullOrEmpty(selection.Model) && !choices.Any(c => c.Id == selection.Model))
                choices.Add(new(selection.Model, selection.Model));
            McpModelPicker.ItemsSource = choices;
            McpModelPicker.SelectedValue = selection.Model ?? "";
        }
        _settingChoices = false;
        PopulateEfforts();
    }

    private void PopulateEfforts()
    {
        if (_selectedAgent == "api") return;
        _settingChoices = true;
        var selection = SettingsStore.GetAgentSelection(_selectedAgent);
        var choices = new List<Choice> { new("", L("Default", "По умолчанию")) };
        var model = string.IsNullOrEmpty(selection.Model)
            ? _codexModels.FirstOrDefault(m => m.IsDefault) ?? _codexModels.FirstOrDefault()
            : _codexModels.FirstOrDefault(m => m.Id == selection.Model);
        var levels = _selectedAgent == "codex" ? model?.Efforts ?? Array.Empty<string>()
            : new[] { "low", "medium", "high", "xhigh", "max" };
        choices.AddRange(levels.Select(level => new Choice(level, level switch
        {
            "none" => L("None", "Без дополнительных рассуждений"), "minimal" => L("Minimal", "Минимальная"),
            "low" => L("Low", "Низкая"), "medium" => L("Medium", "Средняя"), "high" => L("High", "Высокая"),
            "xhigh" => L("Very high", "Очень высокая"), "max" => L("Maximum", "Максимальная"), _ => level
        })));
        if (!string.IsNullOrEmpty(selection.Effort) && !choices.Any(c => c.Id == selection.Effort))
            choices.Add(new(selection.Effort, selection.Effort + L(" — unavailable", " — недоступно")));
        McpEffortPicker.ItemsSource = choices;
        McpEffortPicker.SelectedValue = selection.Effort ?? "";
        _settingChoices = false;
    }

    private void McpModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_choicesReady || _settingChoices || _selectedAgent == "api" || McpModelPicker.SelectedItem is not Choice choice) return;
        var selection = SettingsStore.GetAgentSelection(_selectedAgent);
        // A level chosen for a different model must not silently override the new model's default.
        SettingsStore.SaveAgentSelection(selection with { Model = choice.Id, Effort = "" });
        PopulateEfforts();
    }

    private void McpModelPicker_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CaptureTypedModel();

    private void CaptureTypedModel()
    {
        if (_settingChoices || _selectedAgent == "api") return;
        var model = McpModelPicker.SelectedItem is Choice choice && McpModelPicker.Text == choice.Label
            ? choice.Id : McpModelPicker.Text.Trim();
        var saved = SettingsStore.GetAgentSelection(_selectedAgent);
        if (model == saved.Model) return;
        SettingsStore.SaveAgentSelection(saved with { Model = model, Effort = "" });
        PopulateEfforts();
    }

    private void McpEffortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_choicesReady || _settingChoices || _selectedAgent == "api") return;
        if (McpEffortPicker.SelectedValue is string effort)
            SettingsStore.SaveAgentSelection(SettingsStore.GetAgentSelection(_selectedAgent) with { Effort = effort });
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private async Task RefreshModelsAsync()
    {
        if (_cts != null || _refreshingModels) return;
        CaptureTypedModel();
        _refreshingModels = true;
        SetAgentControlsEnabled(false);
        SendButton.IsEnabled = false;
        StatusText.Text = L("Loading models…", "Загрузка моделей…");
        try
        {
            _codexModels = await CodexModelCatalog.ReadAsync(SettingsStore.CodexExe, McpServer.ClientWorkDir(), CancellationToken.None);
            ApplyAgentChoices();
            StatusText.Text = L($"Codex: {_codexModels.Count} models", $"Codex: {_codexModels.Count} моделей");
        }
        catch (Exception ex) { StatusText.Text = "Codex: " + ex.Message; }
        finally { _refreshingModels = false; SetAgentControlsEnabled(true); SendButton.IsEnabled = true; }
    }

    private void SetAgentControlsEnabled(bool enabled)
    {
        AgentPicker.IsEnabled = ModelPicker.IsEnabled = McpModelPicker.IsEnabled = McpEffortPicker.IsEnabled = enabled;
        RefreshModelsButton.IsEnabled = SettingsButton.IsEnabled = BenchmarkButton.IsEnabled = ClearButton.IsEnabled = enabled;
    }

    // Keep the assistant name shown in the chat in sync with the selected model, so an
    // alt-provider reply (Grok, Gemini, a local model…) isn't labelled "Claude".
    private void UpdateAssistantLabel() =>
        ChatMessage.AssistantLabel =
            _selectedAgent == "codex" ? "Codex"
            : _selectedAgent == "claudecode" ? "Claude Code"
            : _selectedModel == "alt" ? (SettingsStore.AltModel.Length > 0 ? SettingsStore.AltModel : "Assistant")
            : "Claude";

    private async Task SendAsync()
    {
        var text = InputBox.Text.Trim();
        if ((string.IsNullOrEmpty(text) && _pendingImageBase64 == null) || _cts != null || _refreshingModels) return;
        CaptureTypedModel();
        var selection = _selectedAgent == "api" ? null : SettingsStore.GetAgentSelection(_selectedAgent);
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
        // The chat loop no longer runs on the UI thread, so this arrives from the thread pool:
        // touching StatusText directly would throw. Fire-and-forget on purpose — a status line is
        // not worth making the loop wait for the dispatcher.
        _service.OnRound = (r, max) => Dispatcher.BeginInvoke(new Action(() =>
            StatusText.Text = $"Working… round {r}/{max}"));

        Messages.Add(new ChatMessage { Role = "user", Text = image != null ? text + "  📎" : text });

        _service.SubscriptionMode = false;
        _cts = new CancellationTokenSource();
        SetAgentControlsEnabled(false);
        try
        {
            await _service.SendAsync(Messages, _selectedModel, _cts.Token, image, imageMime, selection);
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
            SetAgentControlsEnabled(true);
            InputBox.Focus();
        }
    }
}
