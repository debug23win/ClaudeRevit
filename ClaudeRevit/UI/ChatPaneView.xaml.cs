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

public partial class ChatPaneView : UserControl, IDisposable
{
    private sealed class PaneSession
    {
        public string Key;
        public ChatService Service;
        public PaneSession(DocumentSessions.Snapshot? snapshot = null)
        { snapshot ??= DocumentSessions.Current; Key = snapshot.DocumentKey; Service = new(false, snapshot); }
        public ObservableCollection<ChatMessage> Messages = new();
        public CancellationTokenSource? Cancellation;
        public string Draft = "", Status = "", Agent = SettingsStore.ChatAgent, Model = "auto";
        public ObservableCollection<ChatAttachment> Attachments = new();
        public int LoadingFiles;
        public readonly SemaphoreSlim FileGate = new(1, 1);
        public Dictionary<string,McpAgentSelection> Choices = new()
        { ["codex"] = SettingsStore.GetAgentSelection("codex"), ["claudecode"] = SettingsStore.GetAgentSelection("claudecode") };
    }
    private PaneSession _session = new();
    private readonly Dictionary<string,PaneSession> _sessions = new();
    public ObservableCollection<ChatMessage> Messages => _session.Messages;
    private ChatService _service => _session.Service;
    private CancellationTokenSource? _cts { get => _session.Cancellation; set => _session.Cancellation = value; }
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

    public ChatPaneView()
    {
        _sessions[_session.Key] = _session;
        InitializeComponent();
        RefreshAttachments();
        RefreshSendControls();
        _choicesReady = true;
        _settingChoices = true;
        _selectedAgent = SettingsStore.ChatAgent;
        AgentPicker.SelectedItem = AgentPicker.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == _selectedAgent);
        _settingChoices = false;
        ApplyAgentChoices();
        Loaded += async (_, _) =>
        {
            // A tab may have changed while this independent dispatcher built XAML,
            // before it subscribed to document events. Reconcile on first display.
            SwitchDocumentHistory();
            if (_selectedAgent == "codex" && _codexModels.Count == 0) await RefreshModelsAsync();
        };
        DataContext = this;
        Messages.CollectionChanged += OnMessagesChanged;

        foreach (var m in _service.LoadUiMessages())
            Messages.Add(m);

        DocumentSessions.Changed += OnDocumentChanged;
        DocumentSessions.Closed += OnDocumentClosed;
        UsageTracker.Updated += UpdateUsageText;
        Tools.ToolDispatcher.ProgressChanged += OnToolProgress;
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

    private void OnDocumentChanged(bool toolTransition)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        var snapshot = DocumentSessions.Current;
        if (!Dispatcher.CheckAccess())
        { Dispatcher.BeginInvoke(new Action(() => ApplyDocumentChange(toolTransition, snapshot))); return; }
        ApplyDocumentChange(toolTransition, snapshot);
    }
    private void ApplyDocumentChange(bool toolTransition, DocumentSessions.Snapshot snapshot)
    {
        if (_disposed) return;
        if (toolTransition && _cts != null) return;
        SwitchDocumentHistory(snapshot);
    }
    private void OnDocumentClosed(string key)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnDocumentClosed(key))); return; }
        if (!_disposed && _sessions.TryGetValue(key, out var session)) session.Cancellation?.Cancel();
        PruneClosedSessions();
    }

    // A session per document used to live for the whole Revit session: reopening the same file a
    // few times left that many chat services in memory, each with its full history and any base64
    // images. A session whose document is closed is saved and dropped once it is neither the one
    // on screen nor still finishing a turn; reopening the file reloads its history from disk.
    private void PruneClosedSessions()
    {
        if (_disposed) return;
        foreach (var (key, session) in _sessions.ToArray())
        {
            if (ReferenceEquals(session, _session) || session.Cancellation != null) continue;
            if (key == "none" || DocumentSessions.Find(key) != null) continue;
            try { session.Service.SaveHistory(session.Messages); } catch (Exception ex) { Log.Error("Saving a closed document's chat failed", ex); }
            session.Messages.CollectionChanged -= OnMessagesChanged;
            _sessions.Remove(key);
        }
    }
    private bool _disposed;
    private void OnToolProgress(string documentKey, string stage)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || !_sessions.TryGetValue(documentKey, out var session) || session.Cancellation == null) return;
            session.Status = stage; if (ReferenceEquals(session, _session)) StatusText.Text = stage;
        }));
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        DocumentSessions.Changed -= OnDocumentChanged; DocumentSessions.Closed -= OnDocumentClosed;
        UsageTracker.Updated -= UpdateUsageText; SelectionService.Changed -= OnSelectionChanged;
        Tools.ToolDispatcher.ProgressChanged -= OnToolProgress;
        Dispatcher.UnhandledException -= OnDispatcherUnhandledException;
        foreach (var session in _sessions.Values)
        { session.Cancellation?.Cancel(); session.Service.SaveHistory(session.Messages); session.Messages.CollectionChanged -= OnMessagesChanged; }
    }
    private void SwitchDocumentHistory(DocumentSessions.Snapshot? snapshot = null)
    {
        snapshot ??= DocumentSessions.Current;
        var key = snapshot.DocumentKey;
        if (_session.Key == key)
        {
            if (_cts == null && !_service.WorkspaceMatches(snapshot))
            { _service.SaveHistory(Messages); _service.SwitchWorkspace(preserveHistory:true, snapshot:snapshot); _service.SaveHistory(Messages); }
            return;
        }
        CaptureTypedModel();
        _session.Draft = InputBox.Text; _session.Status = StatusText.Text;
        _session.Agent = _selectedAgent; _session.Model = _selectedModel;
        if (_selectedAgent != "api") _session.Choices[_selectedAgent] = SettingsStore.GetAgentSelection(_selectedAgent);
        _service.SaveHistory(Messages);
        if (!_sessions.TryGetValue(key,out var session))
        {
            session = new PaneSession(snapshot); _sessions[key] = session;
            foreach (var m in session.Service.LoadUiMessages()) session.Messages.Add(m);
            session.Messages.CollectionChanged += OnMessagesChanged;
            session.Service.ConfirmToolAsync = ConfirmToolAsync;
        }
        _session = session; _service.Activate();
        PruneClosedSessions();
        _selectedAgent = session.Agent; _selectedModel = session.Model;
        foreach(var selected in session.Choices.Values) SettingsStore.SaveAgentSelection(selected);
        _settingChoices = true;
        AgentPicker.SelectedItem = AgentPicker.Items.Cast<ComboBoxItem>().First(i=>(string)i.Tag == _selectedAgent);
        ModelPicker.SelectedItem = ModelPicker.Items.Cast<ComboBoxItem>().First(i=>(string)i.Tag == _selectedModel);
        _settingChoices = false; ApplyAgentChoices(); UpdateAssistantLabel();
        MessagesList.ItemsSource = Messages;
        InputBox.Text = session.Draft; StatusText.Text = session.Status;
        RefreshAttachments(); RefreshSendControls();
        SetAgentControlsEnabled(_cts == null);
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TaskJournal.ReadRecent(_session.Key) + "\n\n" + Log.ReadTail() + "\n\n" + string.Join("\n\n",Messages.Select(m=>$"{m.Role}: {m.Text}\n{m.AttachmentDisplay}"))); StatusText.Text = L("Log copied", "Журнал скопирован"); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ChatMessage m in e.NewItems)
                m.PropertyChanged += (_, _) => { if(ReferenceEquals(sender,Messages))ScheduleScroll(); };
        }
        if(ReferenceEquals(sender,Messages))ScheduleScroll();
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
            _ = SendAsync();
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsFileDropList())
        { e.Handled = true; _ = AttachFilesAsync(Clipboard.GetFileDropList().Cast<string>()); }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        _ = SendAsync();
    }
    private void StopButton_Click(object sender, RoutedEventArgs e)
    { if (_cts != null) { StatusText.Text = L("Stopping… waiting for Revit", "Остановка… ожидание Revit"); _cts.Cancel(); } }
    private void RefreshSendControls()
    {
        SendButton.Content = _cts == null ? L("Send", "Отправить") : L("Add", "Дополнить");
        SendButton.IsEnabled = !_refreshingModels && _session.LoadingFiles == 0;
        StopButton.Content = L("Stop", "Стоп"); StopButton.Visibility = _cts == null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RefreshAttachments()
    { AttachmentList.ItemsSource = _session.Attachments; AttachmentBorder.Visibility = _session.Attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible; }
    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: ChatAttachment attachment }) { _session.Attachments.Remove(attachment); RefreshAttachments(); } }
    private void Pane_PreviewDragOver(object sender, DragEventArgs e)
    { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } }
    private void Pane_PreviewDrop(object sender, DragEventArgs e)
    { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { e.Handled = true; _ = AttachFilesAsync(files); } }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        Messages.Clear();
        _service.ClearHistory();
        UsageTracker.Reset();
        StatusText.Text = "";
        InputBox.Focus();
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("Attach documents", "Прикрепить документы"), Multiselect = true,
            Filter = "All files|*.*|Documents|*.pdf;*.docx;*.xlsx;*.pptx;*.txt;*.csv;*.json;*.xml;*.zip|Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff|BIM and CAD|*.rfa;*.rvt;*.rte;*.ifc;*.dwg;*.dxf"
        };
        if (dlg.ShowDialog() != true) return;
        await AttachFilesAsync(dlg.FileNames);
    }
    private async Task AttachFilesAsync(IEnumerable<string> paths, string? storageRoot = null)
    {
        var session = _session; var errors = new List<string>();
        session.LoadingFiles++; RefreshSendControls();
        await session.FileGate.WaitAsync();
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    if (session.Attachments.Count >= AttachmentStore.MaxFiles) throw new IOException(L("Up to 20 files per message.", "До 20 файлов в одном сообщении."));
                    var size = new FileInfo(path).Length;
                    if (session.Attachments.Sum(a => a.Size) + size > AttachmentStore.MaxBatchBytes) throw new IOException(L("Attachments exceed 200 MB.", "Общий размер вложений превышает 200 МБ."));
                    session.Status = L("Reading ", "Читаю ") + Path.GetFileName(path);
                    if (ReferenceEquals(_session, session)) StatusText.Text = session.Status;
                    var attachment = await AttachmentStore.ImportAsync(path, root: storageRoot);
                    session.Attachments.Add(attachment);
                    if (ReferenceEquals(_session, session)) RefreshAttachments();
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            session.Status = errors.Count > 0 ? string.Join("\n", errors) : L("Files attached to your next message.", "Файлы прикреплены к следующему сообщению.");
            if (ReferenceEquals(_session, session)) StatusText.Text = session.Status;
        }
        finally { session.FileGate.Release(); session.LoadingFiles--; if (ReferenceEquals(_session, session)) RefreshSendControls(); }
    }

    private void RunToolButton_Click(object sender, RoutedEventArgs e)
    {
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

    private void SavePaneSelection(McpAgentSelection selected)
    {
        _session.Choices[selected.Agent] = selected;
        SettingsStore.SaveAgentSelection(selected);
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
        SavePaneSelection(selection with { Model = choice.Id, Effort = "" });
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
        SavePaneSelection(saved with { Model = model, Effort = "" });
        PopulateEfforts();
    }

    private void McpEffortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_choicesReady || _settingChoices || _selectedAgent == "api") return;
        if (McpEffortPicker.SelectedValue is string effort)
            SavePaneSelection(SettingsStore.GetAgentSelection(_selectedAgent) with { Effort = effort });
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
            _codexModels = await CodexModelCatalog.ReadAsync(SettingsStore.CodexExe, McpServer.ClientWorkDir(), CancellationToken.None, forceRefresh: true);
            ApplyAgentChoices();
            StatusText.Text = L($"Codex: {_codexModels.Count} models", $"Codex: {_codexModels.Count} моделей");
        }
        catch (Exception ex) { StatusText.Text = "Codex: " + ex.Message; }
        finally { _refreshingModels = false; SetAgentControlsEnabled(true); RefreshSendControls(); }
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
        var session = _session; var service = session.Service; var messages = session.Messages;
        var text = InputBox.Text.Trim();
        if ((string.IsNullOrEmpty(text) && session.Attachments.Count == 0) || _refreshingModels || session.LoadingFiles > 0) return;
        var attachments = session.Attachments.ToArray();
        if (string.IsNullOrEmpty(text)) text = L("Please inspect the attached documents.", "Посмотри приложенные документы.");
        var request = new ChatRequest(text, attachments);
        if (_cts != null)
        {
            if (_cts.IsCancellationRequested || !service.Supplement(request))
            { StatusText.Text = L("The turn is finishing; your draft is preserved. Send it when finished.", "Ответ завершается; черновик сохранён. Отправьте его после завершения."); return; }
            messages.Add(new ChatMessage { Role = "user", Text = text, Attachments = attachments });
            session.Attachments.Clear(); RefreshAttachments(); InputBox.Clear(); session.Draft = "";
            service.SaveHistory(messages);
            StatusText.Text = L("Additional request sent; applied at the next model/tool boundary.", "Уточнение отправлено; агент получит его между вызовами.");
            return;
        }
        CaptureTypedModel();
        var selection = _selectedAgent == "api" ? null : SettingsStore.GetAgentSelection(_selectedAgent);
        var assistantName=ChatMessage.AssistantLabel;
        session.Attachments.Clear(); RefreshAttachments();

        InputBox.Text = "";
        StatusText.Text = "Sending...";

        // Live round counter so long jobs show progress toward the per-message cap.
        // The chat loop no longer runs on the UI thread, so this arrives from the thread pool:
        // touching StatusText directly would throw. Fire-and-forget on purpose — a status line is
        // not worth making the loop wait for the dispatcher.
        void Status(string value) => Dispatcher.BeginInvoke(new Action(() =>
        { session.Status = value; if (ReferenceEquals(_session,session)) StatusText.Text = value; }));
        service.OnRound = (r,max) => Status($"Working… round {r}/{max}");
        service.OnStatus = Status;

        messages.Add(new ChatMessage { Role = "user", Text = text, Attachments = attachments });

        service.SubscriptionMode = false;
        var cancellation = new CancellationTokenSource(); session.Cancellation = cancellation;
        RefreshSendControls();
        SetAgentControlsEnabled(false);
        try
        {
            await service.SendAsync(messages, _selectedModel, cancellation.Token, mcpSelection: selection, attachments: attachments);
            Status("");
        }
        catch (OperationCanceledException)
        {
            Status("Cancelled");
        }
        catch (Exception ex)
        {
            messages.Add(new ChatMessage { Role = "assistant", AssistantName=assistantName, Text = $"[Error: {ex.Message}]" });
            Status("Error");
        }
        finally
        {
            service.SaveHistory(messages);
            cancellation.Dispose(); session.Cancellation = null;
            if (ReferenceEquals(_session,session))
            { SwitchDocumentHistory(); RefreshSendControls(); SetAgentControlsEnabled(true); InputBox.Focus(); }
            else PruneClosedSessions(); // a background turn for a now-closed document just ended
        }
    }
}
