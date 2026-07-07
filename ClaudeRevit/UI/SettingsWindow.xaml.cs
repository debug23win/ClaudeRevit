using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClaudeRevit.Services;
using ClaudeRevit.Tools;

namespace ClaudeRevit.UI;

public partial class SettingsWindow : Window
{
    // Exact text shown at load: unchanged text means "no balance update" (string equality
    // — no format/epsilon coupling), and an exhausted balance shows 0.00 rather than an
    // empty box so an untouched Save can never be mistaken for "user cleared the field".
    private readonly string _initialBalanceText;

    // True only while the constructor is loading saved values. Selecting the provider combo
    // during load must NOT autofill preset defaults (the ctor restores the saved URL/model
    // itself); only a real user pick should.
    private bool _initializing = true;

    // Preset → (base URL, default model, typical context in K tokens; 0 = unknown).
    // The defaults are a starting point the user can overwrite — e.g. swap
    // deepseek-chat for deepseek-reasoner.
    private static (string Url, string Model, int ContextK) AltPreset(string tag) => tag switch
    {
        "gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", 1000),
        "openai" => ("https://api.openai.com/v1", "gpt-5-mini", 400),
        "grok" => ("https://api.x.ai/v1", "grok-4.3", 256),
        "deepseek" => ("https://api.deepseek.com/v1", "deepseek-chat", 64),
        "qwen" => ("https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus", 128),
        "openrouter" => ("https://openrouter.ai/api/v1", "deepseek/deepseek-chat-v3-0324:free", 64),
        "groq" => ("https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", 128),
        "ollama" => ("http://localhost:11434/v1", "qwen3", 8),
        "lmstudio" => ("http://localhost:1234/v1", "", 8),
        _ => ("", "", 0)
    };

    // Optional reasoning-model default per preset (auto-escalation target). Empty = none.
    private static string AltReasoningPreset(string tag) => tag switch
    {
        "grok" => "grok-4.20-0309-reasoning",
        _ => ""
    };

    public SettingsWindow()
    {
        InitializeComponent();
        var existing = ApiKeyStore.Load();
        if (!string.IsNullOrEmpty(existing)) ApiKeyBox.Password = existing;
        AllowCodeBox.IsChecked = SettingsStore.AllowCodeExecution;
        ConfirmOpsBox.IsChecked = SettingsStore.ConfirmOperations;
        AutoAdvisorBox.IsChecked = SettingsStore.AutoUseAdvisor;
        SelectByTag(AutoExecBox, SettingsStore.AutoExecutorModel);
        SelectByTag(AutoAdvBox, SettingsStore.AutoAdvisorModel);
        TaskDiagBox.IsChecked = SettingsStore.ShowTaskDiagnostics;
        AltCompactToolsBox.IsChecked = SettingsStore.AltCompactTools;

        // Order matters: selecting the combo fires SelectionChanged (fields are empty →
        // preset defaults land), then the persisted values overwrite them — so the boxes
        // always end up showing what is actually saved.
        foreach (ComboBoxItem item in AltProviderBox.Items)
            if ((string)item.Tag == SettingsStore.AltProvider)
            {
                AltProviderBox.SelectedItem = item;
                break;
            }
        if (AltProviderBox.SelectedItem == null) AltProviderBox.SelectedIndex = 0;

        AltBaseUrlBox.Text = SettingsStore.AltBaseUrl;
        AltModelBox.Text = SettingsStore.AltModel;
        AltContextBox.Text = SettingsStore.AltContextK > 0
            ? SettingsStore.AltContextK.ToString(CultureInfo.InvariantCulture)
            : "";
        AltReasoningBox.Text = SettingsStore.AltReasoningModel;
        AltKeyBox.Password = ApiKeyStore.LoadAlt() ?? "";
        MaxRoundsBox.Text = SettingsStore.MaxToolRounds.ToString(CultureInfo.InvariantCulture);

        _initialBalanceText = SettingsStore.BalanceUsd > 0
            ? System.Math.Max(0, SettingsStore.BalanceUsd - SettingsStore.SpentUsd)
                .ToString("F2", CultureInfo.InvariantCulture)
            : "";
        BalanceBox.Text = _initialBalanceText;

        PopulateToolGroups();

        // Setting the selected item fires LanguageBox_SelectionChanged, which localizes the
        // whole window. All named elements already exist (post-InitializeComponent).
        foreach (ComboBoxItem it in LanguageBox.Items)
            if ((string)it.Tag == SettingsStore.UiLanguage) { LanguageBox.SelectedItem = it; break; }
        if (LanguageBox.SelectedItem == null) LanguageBox.SelectedIndex = 0;

        // Loading is done — from now on a provider pick autofills its preset.
        _initializing = false;
    }

    // Current UI language ("en"/"ru") reflected by the LanguageBox; persisted on Save.
    private string _lang = "en";

    private string L(string en, string ru) => _lang == "ru" ? ru : en;

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        if (box.SelectedItem == null) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box, string fallback) =>
        box.SelectedItem is ComboBoxItem it && it.Tag is string t ? t : fallback;

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is ComboBoxItem it && it.Tag is string tag)
        {
            _lang = tag;
            ApplyLanguage();
        }
    }

    // Every user-facing string is set here so the language can flip live. Tool-group names and
    // provider proper-nouns stay as-is (short technical labels).
    private void ApplyLanguage()
    {
        Title = L("Claude Revit — Settings", "Claude Revit — Настройки");
        LangLabel.Text = L("Interface language:", "Язык интерфейса:");

        ApiKeyHeader.Text = L("Anthropic API key", "API-ключ Anthropic");
        ApiKeyNote.Text = L(
            "Stored encrypted (Windows DPAPI, current user) in %AppData%\\ClaudeRevit. Get a key at console.anthropic.com → Settings → API Keys.",
            "Хранится зашифрованным (Windows DPAPI, текущий пользователь) в %AppData%\\ClaudeRevit. Ключ — на console.anthropic.com → Settings → API Keys.");

        AllowCodeBox.Content = L("Allow Claude to run code (C# / Dynamo Python)", "Разрешить выполнение кода (C# / Dynamo Python)");
        AllowCodeNote.Text = L(
            "Off by default. When enabled, Claude may run scripts against the full Revit API for actions no built-in tool covers. Leave this off unless you need it; arbitrary code can modify or delete anything in the model. Every turn is one undo step (Ctrl+Z).",
            "По умолчанию выключено. Включив, вы разрешаете выполнять скрипты со всем Revit API для действий, не покрытых готовыми инструментами. Не включайте без необходимости — произвольный код может изменить или удалить что угодно в модели. Каждый ход — один шаг отмены (Ctrl+Z).");

        ConfirmOpsBox.Content = L("Ask for confirmation before destructive / code operations", "Спрашивать подтверждение перед опасными операциями / кодом");
        ConfirmOpsNote.Text = L(
            "Off by default: an Allow/Deny dialog before deletions and script runs. Everything is still undoable with Ctrl+Z.",
            "По умолчанию выключено: диалог «Разрешить/Запретить» перед удалением и запуском скриптов. Всё равно отменяется через Ctrl+Z.");

        AutoAdvisorBox.Content = L(
            "Auto mode: consult the advisor mid-turn (recommended)",
            "Режим Auto: советоваться с advisor по ходу (рекомендуется)");
        AutoAdvisorNote.Text = L(
            "Applies to the “Auto” model. On (recommended): the executor below runs the whole turn and consults the advisor only when it needs a plan — the executor's cache stays warm all session and the advisor is billed only for the short advice. Off: the older behaviour — the whole turn switches to Opus on a hard task, error or long loop (costs more, drops the cache).",
            "Относится к модели «Auto». Вкл (рекомендуется): исполнитель ниже ведёт весь ход и советуется с advisor только когда нужен план — кэш исполнителя остаётся горячим всю сессию, а advisor оплачивается лишь за короткий совет. Выкл: прежнее поведение — весь ход переключается на Opus на сложной задаче, ошибке или длинном цикле (дороже, сбрасывает кэш).");
        AutoExecLabel.Text = L("Executor:", "Исполнитель:");
        AutoAdvLabel.Text = L("Advisor:", "Советник:");
        TaskDiagBox.Content = L(
            "Show per-task diagnostics (time, tokens, rounds)",
            "Показывать диагностику по задаче (время, токены, раунды)");
        TaskDiagNote.Text = L(
            "After each answer, print a line with the model(s) used, tool rounds, tokens (in/out) and wall-clock time — to compare how efficiently different models solve the same task.",
            "После каждого ответа выводить строку: использованные модели, раунды инструментов, токены (вход/выход) и время — чтобы сравнивать, насколько эффективно разные модели решают одну задачу.");

        BalanceLabel.Text = L("Account balance, USD:", "Баланс счёта, USD:");
        BalanceNote.Text = L(
            "Optional. Enter your current credit balance from console.anthropic.com — the chat pane shows it minus the estimated local spend (the API has no balance endpoint). Re-entering a value resets the counter.",
            "Необязательно. Введите текущий баланс с console.anthropic.com — панель чата покажет его за вычетом оценки локального расхода (у API нет запроса баланса). Повторный ввод сбрасывает счётчик.");

        AltHeader.Text = L("Alternative model (Gemini, ChatGPT, DeepSeek, Ollama…)", "Альтернативная модель (Gemini, ChatGPT, DeepSeek, Ollama…)");
        ProviderLabel.Text = L("Provider:", "Провайдер:");
        BaseUrlLabel.Text = L("Base URL:", "Base URL:");
        ModelLabel.Text = L("Model id:", "ID модели:");
        AltKeyLabel.Text = L("API key:", "API-ключ:");
        ContextLabel.Text = L("Context, K:", "Контекст, K:");
        ReasoningLabel.Text = L("Reasoning model:", "Reasoning-модель:");
        ReasoningNote.Text = L(
            "Optional. A stronger reasoning model (e.g. grok-4.20-0309-reasoning). When set, the fast model above handles routine work and this one kicks in automatically on complex tasks, errors or long multi-step jobs. Leave empty to always use the model above.",
            "Необязательно. Более сильная reasoning-модель (напр. grok-4.20-0309-reasoning). Если задана — быстрая модель выше работает по рутине, а эта включается автоматически на сложных задачах, при ошибках или длинных многошаговых операциях. Пусто = всегда модель выше.");
        AltNote.Text = L(
            "Optional. Any OpenAI-compatible endpoint; picking a preset fills in the URL, a default model and its typical context size. Local Ollama / LM Studio need no key. The model must support function calling. Select “Alt” in the chat pane's model dropdown to use it.",
            "Необязательно. Любой OpenAI-совместимый эндпоинт; выбор пресета подставит URL, модель по умолчанию и типичный размер контекста. Локальным Ollama / LM Studio ключ не нужен. Модель должна поддерживать вызов функций. Чтобы использовать — выберите «Alt» в списке моделей панели чата.");

        MaxRoundsLabel.Text = L("Max tool rounds per message:", "Макс. раундов инструментов на сообщение:");
        MaxRoundsNote.Text = L(
            "How many tool-call rounds Claude may take answering one message before it pauses and asks to continue. Default 24; raise it for long automated jobs. Range 1–200.",
            "Сколько раундов вызовов инструментов Claude может сделать на одно сообщение, прежде чем остановиться и спросить о продолжении. По умолчанию 24; поднимите для длинных автоматических задач. Диапазон 1–200.");

        ToolGroupsHeader.Text = L("Active tool groups (fewer = fewer tokens per request)", "Активные группы инструментов (меньше = меньше токенов на запрос)");
        ToolGroupsNote.Text = L(
            "Uncheck groups you don't need — each request gets smaller in tokens. Helps free / rate-limited providers (e.g. Gemini's free tier, where one request can hit the limit). All on by default.",
            "Снимите галочки с ненужных групп — каждый запрос станет меньше по токенам. Полезно для бесплатных / лимитированных провайдеров (напр. бесплатный тир Gemini, где одного запроса хватает до лимита). По умолчанию включены все.");

        HelpButton.Content = L("Help / Помощь", "Помощь / Help");
        CancelButton.Content = L("Cancel", "Отмена");
        SaveButton.Content = L("Save", "Сохранить");
    }

    // One checkbox per tool group (with its tool count). Unchecking a group drops those tools
    // from every request — the token-budget lever for rate-limited / free providers.
    private void PopulateToolGroups()
    {
        var disabled = new HashSet<string>(SettingsStore.DisabledToolGroups,
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var (category, count) in ToolCatalog.Summarize(ToolRegistry.Instance.All))
        {
            ToolGroupsPanel.Children.Add(new CheckBox
            {
                Content = $"{category} ({count})",
                Tag = category,
                IsChecked = !disabled.Contains(category),
                Width = 165,
                Margin = new Thickness(0, 2, 8, 2)
            });
        }
    }

    private void AltProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // During load the constructor restores the saved URL/model itself.
        if (_initializing) return;
        if (AltProviderBox.SelectedItem is not ComboBoxItem item) return;

        var tag = (string)item.Tag;
        var (url, model, contextK) = AltPreset(tag);

        // Picking a known provider fills in its endpoint, default model and typical context —
        // that is the whole point of the preset. "custom" / "— not used —" have no preset, so
        // leave whatever the user typed. The user can still edit any field afterwards.
        if (url.Length == 0) return;
        AltBaseUrlBox.Text = url;
        AltModelBox.Text = model;
        AltContextBox.Text = contextK > 0 ? contextK.ToString(CultureInfo.InvariantCulture) : "";
        AltReasoningBox.Text = AltReasoningPreset(tag);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Validate EVERYTHING before persisting anything, so Save is atomic and
        // DialogResult=true always means "settings were saved" (ChatPaneView recreates
        // the API client only on true).
        var key = ApiKeyBox.Password.Trim();
        var altUrl = AltBaseUrlBox.Text.Trim();
        var altModel = AltModelBox.Text.Trim();
        var altConfigured = altUrl.Length > 0 && altModel.Length > 0;

        // The Anthropic key is not mandatory: an alternative provider alone is a valid
        // setup (free/local models). But SOME backend must be usable.
        if (key.Length == 0 && !altConfigured)
        {
            MessageBox.Show(this,
                "Enter an Anthropic API key, or configure the alternative model " +
                "(base URL + model id) below.",
                "Claude Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if ((altUrl.Length > 0) != (altModel.Length > 0))
        {
            MessageBox.Show(this,
                "The alternative model needs both a base URL and a model id " +
                "(or leave both empty to disable it).",
                "Claude Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (altUrl.Length > 0 &&
            (!System.Uri.TryCreate(altUrl, System.UriKind.Absolute, out var uri) ||
             (uri.Scheme != "http" && uri.Scheme != "https")))
        {
            MessageBox.Show(this,
                "The alternative provider's base URL must be a full http(s) URL, " +
                "e.g. https://api.deepseek.com/v1",
                "Claude Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var contextText = AltContextBox.Text.Trim();
        int contextK = 0;
        if (contextText.Length > 0 &&
            (!int.TryParse(contextText, NumberStyles.Integer, CultureInfo.InvariantCulture, out contextK) ||
             contextK < 0))
        {
            MessageBox.Show(this,
                "Context size must be a whole number of thousands of tokens (e.g. 64), or empty.",
                "Claude Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var roundsText = MaxRoundsBox.Text.Trim();
        if (!int.TryParse(roundsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRounds) ||
            maxRounds < 1 || maxRounds > 200)
        {
            MessageBox.Show(this,
                "Max tool rounds must be a whole number between 1 and 200.",
                "Claude Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var balanceText = BalanceBox.Text.Trim();
        var balanceChanged = balanceText != _initialBalanceText;
        decimal balance = 0;
        if (balanceChanged && balanceText.Length > 0 &&
            (!decimal.TryParse(balanceText.Replace(',', '.'), NumberStyles.Number,
                 CultureInfo.InvariantCulture, out balance) || balance < 0))
        {
            MessageBox.Show(this,
                "Balance must be a number (e.g. 25.00) or empty.",
                "Claude Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The field is pre-filled with the stored key, so a blanked field is an explicit
        // "remove my key from this machine" — honor it (validation above already
        // guaranteed the alt provider covers the pane).
        if (key.Length > 0) ApiKeyStore.Save(key);
        else ApiKeyStore.Delete();
        ApiKeyStore.SaveAlt(AltKeyBox.Password.Trim());
        SettingsStore.AltProvider = AltProviderBox.SelectedItem is ComboBoxItem sel ? (string)sel.Tag : "";
        SettingsStore.AltBaseUrl = altUrl;
        SettingsStore.AltModel = altModel;
        SettingsStore.AltReasoningModel = AltReasoningBox.Text.Trim();
        SettingsStore.AltContextK = contextK;
        SettingsStore.MaxToolRounds = maxRounds;

        var disabledGroups = new List<string>();
        foreach (var child in ToolGroupsPanel.Children)
            if (child is CheckBox cb && cb.IsChecked == false && cb.Tag is string cat)
                disabledGroups.Add(cat);
        SettingsStore.DisabledToolGroups = disabledGroups;

        SettingsStore.AllowCodeExecution = AllowCodeBox.IsChecked == true;
        SettingsStore.ConfirmOperations = ConfirmOpsBox.IsChecked == true;
        SettingsStore.AutoUseAdvisor = AutoAdvisorBox.IsChecked == true;
        SettingsStore.AutoExecutorModel = TagOf(AutoExecBox, "sonnet-5");
        SettingsStore.AutoAdvisorModel = TagOf(AutoAdvBox, "opus-4-8");
        SettingsStore.ShowTaskDiagnostics = TaskDiagBox.IsChecked == true;
        SettingsStore.AltCompactTools = AltCompactToolsBox.IsChecked == true;
        SettingsStore.UiLanguage = _lang;
        if (balanceChanged)
            SettingsStore.SetBalance(balanceText.Length == 0 ? 0 : balance);

        DialogResult = true;
        Close();
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        new HelpWindow { Owner = this }.ShowDialog();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
