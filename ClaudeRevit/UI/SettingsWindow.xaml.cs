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
    private bool _refreshingSessions;

    // Preset → (base URL, default model, typical context in K tokens; 0 = unknown).
    // The defaults are a starting point the user can overwrite — e.g. swap
    // deepseek-chat for deepseek-reasoner.
    private static (string Url, string Model, int ContextK) AltPreset(string tag) => tag switch
    {
        "gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", 1000),
        "openai" => ("https://api.openai.com/v1", "gpt-5-mini", 400),
        // GPT-5.6 / GPT-6 family: 1,050,000-token context, 128,000 max output. Identifiers are
        // used as-is — this family publishes no dated snapshot suffixes.
        "astra" => ("https://api.openai.com/v1", "gpt-6-astra", 1050),
        "sol" => ("https://api.openai.com/v1", "gpt-5.6-sol", 1050),
        "terra" => ("https://api.openai.com/v1", "gpt-5.6-terra", 1050),
        "luna" => ("https://api.openai.com/v1", "gpt-5.6-luna", 1050),
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
        SelectByTag(ChatUiModeBox, SettingsStore.ChatUiMode);
        ConfirmOpsBox.IsChecked = SettingsStore.ConfirmOperations;
        AutoAdvisorBox.IsChecked = SettingsStore.AutoUseAdvisor;
        SelectByTag(AutoExecBox, SettingsStore.AutoExecutorModel);
        SelectByTag(AutoAdvBox, SettingsStore.AutoAdvisorModel);
        TaskDiagBox.IsChecked = SettingsStore.ShowTaskDiagnostics;
        McpBox.IsChecked = SettingsStore.McpEnabled;
        McpPortBox.Text = SettingsStore.McpPort.ToString();
        ClaudeCodeExeBox.Text = SettingsStore.ClaudeCodeExe;
        CodexExeBox.Text = SettingsStore.CodexExe;
        CodexConfigBox.Text = CodexBackend.ConfigSnippet(McpServer.Url);
        McpModelOverrideBox.Text = SettingsStore.McpModelOverride;
        McpWhoText.Text = McpSession.Describe();
        RefreshMcpClients();
        McpSession.Changed += OnMcpClientsChanged;
        Closed += (_, _) => McpSession.Changed -= OnMcpClientsChanged;
        UpdateMcpConfig();
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

    // A ready-to-paste Claude Code / Desktop MCP config, plus the live server status.
    private void UpdateMcpConfig()
    {
        var port = int.TryParse(McpPortBox.Text, out var p) && p > 0 ? p : SettingsStore.McpPort;
        McpConfigBox.Text =
            "{\n  \"mcpServers\": {\n    \"clauderevit\": {\n" +
            "      \"type\": \"http\",\n" +
            $"      \"url\": \"http://127.0.0.1:{port}/mcp\",\n" +
            $"      \"headers\": {{ \"Authorization\": \"Bearer {SettingsStore.McpToken}\" }}\n" +
            "    }\n  }\n}";
        McpStatusText.Text = McpServer.IsRunning
            ? L($"running at {McpServer.Url}", $"работает на {McpServer.Url}")
            : McpServer.LastError is { } e
                ? L("not running — " + e, "не запущен — " + e)
                : L("not running", "не запущен");
    }

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

        ConfirmOpsBox.Content = L("Ask for confirmation before destructive operations", "Спрашивать подтверждение перед опасными операциями");
        ConfirmOpsNote.Text = L(
            "Off by default: an Allow/Deny dialog before deletions. Everything is still undoable with Ctrl+Z.",
            "По умолчанию выключено: диалог «Разрешить/Запретить» перед удалением. Всё равно отменяется через Ctrl+Z.");

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

        McpBox.Content = L(
            "Experimental: MCP server (drive Revit from Claude Code / Desktop on your subscription)",
            "Эксперимент: MCP-сервер (рулить Revit из Claude Code / Desktop по подписке)");
        McpNote.Text = L(
            "Exposes the Revit tools over a local MCP server so Claude Code / Claude Desktop — authenticated with your Claude Pro/Max subscription — can drive Revit, putting cost on the subscription instead of the pay-per-token API. In the pane, choose API, Claude Code · MCP or Codex · MCP. Security: the server listens only on 127.0.0.1 and requires the token below; anyone who has it can edit your model (and run C#). Paste the config below into Claude Code’s MCP settings. Non-Claude clients are supported too — the server detects them and emits a portable tool schema. For OpenAI models (GPT-5.6 Sol/Terra/Luna, GPT-6 Astra) note that MCP connections work only through the Responses API (v1/responses), not v1/chat/completions.",
            "Выставляет инструменты Revit через локальный MCP-сервер, чтобы Claude Code / Claude Desktop (авторизованные вашей подпиской Pro/Max) могли рулить Revit — стоимость идёт на подписку, а не на потокенный API. В панели выбирайте API, Claude Code · MCP или Codex · MCP. Безопасность: сервер слушает только 127.0.0.1 и требует токен ниже; у кого он есть — тот может править вашу модель (и запускать C#, если включено выполнение кода). Вставьте конфиг ниже в настройки MCP в Claude Code. Клиенты не на Claude тоже поддерживаются — сервер их распознаёт и отдаёт переносимую схему инструментов. Для моделей OpenAI (GPT-5.6 Sol/Terra/Luna, GPT-6 Astra) учтите: MCP-подключения работают только через Responses API (v1/responses), а не через v1/chat/completions.");
        McpPortLabel.Text = L("Port:", "Порт:");
        McpWhoLabel.Text = L("Currently driving Revit", "Кто сейчас управляет Revit");
        McpWhoText.Text = McpSession.Describe(_lang == "ru");
        McpResetSessionBtn.Content = L("Restart session", "Перезапустить сессию");
        McpAskModelBtn.Content = L("Ask for the model above", "Запросить модель сверху");
        McpResetNote.Text = L(
            "Restart session clears the pane CLI conversations. Model changes in the pane apply to the next message while resuming the conversation. The request button asks an external MCP client to change its model on its next tool call; the server cannot force that change.",
            "Перезапуск очищает сессии CLI панели. Выбор новой модели в панели применяется к следующему сообщению с продолжением диалога. Кнопка запроса просит внешнего MCP-клиента сменить модель на следующем вызове инструмента; сервер не может выполнить эту смену сам.");
        McpModelLabel.Text = L(
            "External model request / legacy benchmark override",
            "Запрос модели внешнему клиенту / переопределение бенчмарка");
        McpModelNote.Text = L(
            "Choose the pane agent, model and reasoning level directly in the chat. This field is for external-client requests and legacy benchmark runs. An old saved override is migrated only to its matching agent.",
            "Агента, модель и глубину рассуждений панели выбирайте прямо в чате. Это поле используется для запросов внешнему клиенту и прежних вызовов бенчмарка. Старое сохранённое значение переносится только к соответствующему агенту.");
        CodexNote.Text = L(
            "Set an explicit Codex CLI path if needed; otherwise the pane finds PATH or the Codex desktop installation. Sign in to Codex with ChatGPT once. The pane configures MCP automatically for each process. The snippet below is only for external Codex clients.",
            "При необходимости укажите полный путь к Codex CLI; иначе панель ищет его в PATH или в установке Codex. Один раз войдите в Codex через ChatGPT. Панель настраивает MCP для каждого процесса автоматически. Сниппет ниже нужен только внешним клиентам Codex.");
        ClaudeCodeExeLabel.Text = L(
            "Claude Code executable (for the in-pane / benchmark subscription path)",
            "Путь к Claude Code (для панели / бенчмарка по подписке)");
        ClaudeCodeExeNote.Text = L(
            "Leave as \"claude\" if it is on PATH. Revit’s process often can’t see it — if the benchmark says “Claude Code CLI not found”, put the full path here (run \"where claude\" in a terminal; usually %APPDATA%\\npm\\claude.cmd).",
            "Оставьте \"claude\", если он в PATH. Процесс Revit часто его не видит — если бенчмарк пишет «Claude Code CLI not found», впишите полный путь (в терминале выполните \"where claude\"; обычно %APPDATA%\\npm\\claude.cmd).");

        ToolGroupsHeader.Text = L("Active tool groups (fewer = fewer tokens per request)", "Активные группы инструментов (меньше = меньше токенов на запрос)");
        ToolGroupsNote.Text = L(
            "Uncheck groups you don't need — each request gets smaller in tokens. Helps free / rate-limited providers (e.g. Gemini's free tier, where one request can hit the limit). All on by default.",
            "Снимите галочки с ненужных групп — каждый запрос станет меньше по токенам. Полезно для бесплатных / лимитированных провайдеров (напр. бесплатный тир Gemini, где одного запроса хватает до лимита). По умолчанию включены все.");

        TabGeneral.Header = L("General", "Основное");
        ChatUiModeHeader.Text = L("Chat interface", "Режим окна чата");
        ChatWindowModeItem.Content = L("Separate window (recommended)", "Отдельное окно (рекомендуется)");
        ChatDockedModeItem.Content = L("Docked Revit pane", "Встроенная панель Revit");
        ChatUiModeNote.Text = L("Restart Revit to apply. The separate window stays responsive during scripts. A docked pane shares Revit's UI thread and pauses while an API operation runs.",
            "Применяется после перезапуска Revit. Отдельное окно остаётся отзывчивым во время скриптов. Встроенная панель использует поток Revit и приостанавливается на время вызова API.");
        TabModels.Header = L("Models", "Модели");
        TabMcp.Header = L("Subscription (MCP)", "Подписка (MCP)");
        TabTools.Header = L("Tools", "Инструменты");
        TabAbout.Header = L("About", "О программе");
        AboutHeader.Text = L("Version & updates", "Версия и обновления");
        VersionText.Text = L("Current version: ", "Текущая версия: ") + Services.UpdateChecker.CurrentVersion;
        CheckUpdateButton.Content = L("Check for updates", "Проверить обновления");
        AboutNote.Text = L(
            "Updates are notify-only: a loaded add-in can't replace its own DLL while Revit is open, so when a newer release exists the check offers the installer to download — close Revit, run it, reopen. The chat pane footer also shows an “update available” link when one is found.",
            "Обновления только уведомляют: загруженный аддин не может заменить свою DLL при открытом Revit, поэтому при наличии новой версии предлагается скачать установщик — закройте Revit, запустите его, откройте снова. В подвале панели чата тоже появляется ссылка «доступно обновление».");

        HelpButton.Content = L("Help / Помощь", "Помощь / Help");
        CancelButton.Content = L("Cancel", "Отмена");
        SaveButton.Content = L("Save", "Сохранить");
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateResultText.Text = L("Checking…", "Проверяю…");
        try
        {
            var r = await Services.UpdateChecker.CheckAsync();
            if (r.UpdateAvailable && r.DownloadUrl != null)
            {
                UpdateResultText.Text = L($"Update {r.Latest} available — opening download…",
                                          $"Доступно обновление {r.Latest} — открываю загрузку…");
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.DownloadUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("Opening update URL failed", ex); }
            }
            else if (r.Error != null)
                UpdateResultText.Text = L("Couldn't check: ", "Не удалось проверить: ") + r.Error;
            else
                UpdateResultText.Text = L("You're on the latest version.", "У вас последняя версия.");
        }
        catch (Exception ex) { UpdateResultText.Text = ex.Message; }
    }

    // One checkbox per tool group (with its tool count). Unchecking a group drops those tools
    // from every request — the token-budget lever for rate-limited / free providers.
    private void PopulateToolGroups()
    {
        var disabled = new HashSet<string>(SettingsStore.DisabledToolGroups,
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var (category, count) in ToolCatalog.Summarize(ToolRegistry.Instance.All))
        {
            if(category == "Code & learning") continue; // Always available.
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

        SettingsStore.ConfirmOperations = ConfirmOpsBox.IsChecked == true;
        SettingsStore.ChatUiMode = TagOf(ChatUiModeBox, "window");
        SettingsStore.AutoUseAdvisor = AutoAdvisorBox.IsChecked == true;
        SettingsStore.AutoExecutorModel = TagOf(AutoExecBox, "sonnet-5");
        SettingsStore.AutoAdvisorModel = TagOf(AutoAdvBox, "opus-4-8");
        SettingsStore.ShowTaskDiagnostics = TaskDiagBox.IsChecked == true;
        if (int.TryParse(McpPortBox.Text, out var mcpPort) && mcpPort is > 0 and < 65536)
            SettingsStore.McpPort = mcpPort;
        SettingsStore.McpEnabled = McpBox.IsChecked == true;
        SettingsStore.ClaudeCodeExe = ClaudeCodeExeBox.Text?.Trim() ?? "";
        SettingsStore.CodexExe = CodexExeBox.Text?.Trim() ?? "";
        SettingsStore.McpModelOverride = McpModelOverrideBox.Text?.Trim() ?? "";
        try { McpServer.ApplyFromSettings(); } catch (Exception ex) { Log.Error("MCP apply failed", ex); }
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

    // Two halves of "change who is driving", and they work differently on purpose.
    //
    // The CLI sessions are ours: dropping their ids makes the next message start fresh on whatever
    // model is selected now, which is the only thing that actually changes the model — a running
    // CLI session is pinned to the one it was started with. The self-reported identity is cleared
    // too, because after a restart it describes a session that no longer exists.
    private void OnMcpClientsChanged() => Dispatcher.BeginInvoke(new System.Action(RefreshMcpClients));
    private void RefreshMcpClients()
    {
        _refreshingSessions = true;
        McpClientPicker.ItemsSource = McpSession.All;
        McpClientPicker.SelectedItem = McpSession.Selected;
        McpWhoText.Text = McpSession.Describe(_lang == "ru");
        _refreshingSessions = false;
    }
    private void McpClientPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingSessions && McpClientPicker.SelectedItem is McpClientState state)
            McpSession.Select(state.Id);
    }

    private void McpResetSession_Click(object sender, RoutedEventArgs e)
    {
        McpSession.ResetIdentity();
        Services.ChatService.Current?.ResetCliSessions();
        McpWhoText.Text = McpSession.Describe(_lang == "ru");
        MessageBox.Show(this,
            L("The next subscription / Codex message starts a new CLI session on the currently " +
              "selected model. A connected external client is unaffected — restart it yourself to " +
              "change its model.",
              "Следующее сообщение по подписке / через Codex начнёт новую сессию CLI на выбранной " +
              "сейчас модели. На подключённого внешнего клиента это не влияет — чтобы сменить его " +
              "модель, перезапустите его сами."),
            "ClaudeRevit", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // The other half is a request, not a command: nothing in MCP lets a server change a client's
    // model, so the model is simply told what the user wants on its next tool call and asked to
    // re-identify itself. Saying that plainly is better than a button that appears to switch models
    // and silently does nothing.
    private void McpAskModel_Click(object sender, RoutedEventArgs e)
    {
        var wanted = McpModelOverrideBox.Text?.Trim() ?? "";
        if (wanted.Length == 0)
        {
            MessageBox.Show(this,
                L("Type the model id in the box above first.",
                  "Сначала впишите идентификатор модели в поле выше."),
                "ClaudeRevit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        McpSession.RequestModel(wanted);
        McpWhoText.Text = McpSession.Describe(_lang == "ru");
        MessageBox.Show(this,
            L($"The connected model will be told on its next tool call that you want '{wanted}', and " +
              "asked to report what it actually is. It cannot switch itself — expect it to tell you " +
              "to restart the client on that model.",
              $"Подключённой модели на следующем вызове инструмента сообщат, что вы хотите «{wanted}», " +
              "и попросят назвать, какая она на самом деле. Сама переключиться она не может — скорее " +
              "всего, попросит перезапустить клиент на этой модели."),
            "ClaudeRevit", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
