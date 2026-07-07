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

    // The preset whose defaults currently "own" the URL/model fields — autofill only
    // replaces values the user hasn't customized (see AltProviderBox_SelectionChanged).
    private string _lastPresetTag = "";

    // Preset → (base URL, default model, typical context in K tokens; 0 = unknown).
    // The defaults are a starting point the user can overwrite — e.g. swap
    // deepseek-chat for deepseek-reasoner.
    private static (string Url, string Model, int ContextK) AltPreset(string tag) => tag switch
    {
        "gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", 1000),
        "openai" => ("https://api.openai.com/v1", "gpt-5-mini", 400),
        "deepseek" => ("https://api.deepseek.com/v1", "deepseek-chat", 64),
        "qwen" => ("https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus", 128),
        "openrouter" => ("https://openrouter.ai/api/v1", "deepseek/deepseek-chat-v3-0324:free", 64),
        "groq" => ("https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", 128),
        "ollama" => ("http://localhost:11434/v1", "qwen3", 8),
        "lmstudio" => ("http://localhost:1234/v1", "", 8),
        _ => ("", "", 0)
    };

    public SettingsWindow()
    {
        InitializeComponent();
        var existing = ApiKeyStore.Load();
        if (!string.IsNullOrEmpty(existing)) ApiKeyBox.Password = existing;
        AllowCodeBox.IsChecked = SettingsStore.AllowCodeExecution;
        ConfirmOpsBox.IsChecked = SettingsStore.ConfirmOperations;

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
        _lastPresetTag = SettingsStore.AltProvider;

        AltBaseUrlBox.Text = SettingsStore.AltBaseUrl;
        AltModelBox.Text = SettingsStore.AltModel;
        AltContextBox.Text = SettingsStore.AltContextK > 0
            ? SettingsStore.AltContextK.ToString(CultureInfo.InvariantCulture)
            : "";
        AltKeyBox.Password = ApiKeyStore.LoadAlt() ?? "";
        MaxRoundsBox.Text = SettingsStore.MaxToolRounds.ToString(CultureInfo.InvariantCulture);

        _initialBalanceText = SettingsStore.BalanceUsd > 0
            ? System.Math.Max(0, SettingsStore.BalanceUsd - SettingsStore.SpentUsd)
                .ToString("F2", CultureInfo.InvariantCulture)
            : "";
        BalanceBox.Text = _initialBalanceText;

        PopulateToolGroups();
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
        if (AltProviderBox.SelectedItem is not ComboBoxItem item) return;
        var tag = (string)item.Tag;
        var (url, model, contextK) = AltPreset(tag);
        var (oldUrl, oldModel, oldContextK) = AltPreset(_lastPresetTag);
        _lastPresetTag = tag;

        // Autofill must never destroy a user customization: only fields that are empty
        // or still equal to the PREVIOUS preset's defaults get replaced — merely
        // wheel-scrolling through the combo can't clobber a hand-edited model id.
        var curUrl = AltBaseUrlBox.Text.Trim();
        if (curUrl.Length == 0 || curUrl == oldUrl)
            AltBaseUrlBox.Text = url;
        var curModel = AltModelBox.Text.Trim();
        if (curModel.Length == 0 || curModel == oldModel)
            AltModelBox.Text = model;
        var curCtx = AltContextBox.Text.Trim();
        var oldCtxText = oldContextK > 0 ? oldContextK.ToString(CultureInfo.InvariantCulture) : "";
        if (curCtx.Length == 0 || curCtx == oldCtxText)
            AltContextBox.Text = contextK > 0 ? contextK.ToString(CultureInfo.InvariantCulture) : "";
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
        SettingsStore.AltContextK = contextK;
        SettingsStore.MaxToolRounds = maxRounds;

        var disabledGroups = new List<string>();
        foreach (var child in ToolGroupsPanel.Children)
            if (child is CheckBox cb && cb.IsChecked == false && cb.Tag is string cat)
                disabledGroups.Add(cat);
        SettingsStore.DisabledToolGroups = disabledGroups;

        SettingsStore.AllowCodeExecution = AllowCodeBox.IsChecked == true;
        SettingsStore.ConfirmOperations = ConfirmOpsBox.IsChecked == true;
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
