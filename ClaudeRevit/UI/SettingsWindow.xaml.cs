using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class SettingsWindow : Window
{
    // Exact text shown at load: unchanged text means "no balance update" (string equality
    // — no format/epsilon coupling), and an exhausted balance shows 0.00 rather than an
    // empty box so an untouched Save can never be mistaken for "user cleared the field".
    private readonly string _initialBalanceText;

    // Suppresses the preset autofill while the ctor selects the persisted provider.
    private bool _loadingAltPreset;

    // Preset → (base URL, default model). The defaults are a starting point the user can
    // overwrite — e.g. swap deepseek-chat for deepseek-reasoner.
    private static (string Url, string Model) AltPreset(string tag) => tag switch
    {
        "deepseek" => ("https://api.deepseek.com/v1", "deepseek-chat"),
        "qwen" => ("https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        "openrouter" => ("https://openrouter.ai/api/v1", "deepseek/deepseek-chat-v3-0324:free"),
        "groq" => ("https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
        "ollama" => ("http://localhost:11434/v1", "qwen3"),
        "lmstudio" => ("http://localhost:1234/v1", ""),
        _ => ("", "")
    };

    public SettingsWindow()
    {
        InitializeComponent();
        var existing = ApiKeyStore.Load();
        if (!string.IsNullOrEmpty(existing)) ApiKeyBox.Password = existing;
        AllowCodeBox.IsChecked = SettingsStore.AllowCodeExecution;
        ConfirmOpsBox.IsChecked = SettingsStore.ConfirmOperations;

        _loadingAltPreset = true;
        foreach (ComboBoxItem item in AltProviderBox.Items)
            if ((string)item.Tag == SettingsStore.AltProvider)
            {
                AltProviderBox.SelectedItem = item;
                break;
            }
        if (AltProviderBox.SelectedItem == null) AltProviderBox.SelectedIndex = 0;
        _loadingAltPreset = false;

        AltBaseUrlBox.Text = SettingsStore.AltBaseUrl;
        AltModelBox.Text = SettingsStore.AltModel;
        AltKeyBox.Password = ApiKeyStore.LoadAlt() ?? "";

        _initialBalanceText = SettingsStore.BalanceUsd > 0
            ? System.Math.Max(0, SettingsStore.BalanceUsd - SettingsStore.SpentUsd)
                .ToString("F2", CultureInfo.InvariantCulture)
            : "";
        BalanceBox.Text = _initialBalanceText;
    }

    private void AltProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAltPreset) return;
        if (AltProviderBox.SelectedItem is not ComboBoxItem item) return;
        var (url, model) = AltPreset((string)item.Tag);
        // A user-picked preset overwrites the fields — that's the point of picking one.
        // "Custom" / "not used" clear them so stale values don't linger invisibly.
        AltBaseUrlBox.Text = url;
        AltModelBox.Text = model;
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

        // The Anthropic key is no longer mandatory: an alternative provider alone is a
        // valid setup (free/local models). But SOME backend must be usable.
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

        // An intentionally blank Anthropic key with a configured alt provider keeps any
        // previously stored key (deleting it would be surprising); it just isn't required.
        if (key.Length > 0)
            ApiKeyStore.Save(key);
        ApiKeyStore.SaveAlt(AltKeyBox.Password.Trim());
        SettingsStore.AltProvider = AltProviderBox.SelectedItem is ComboBoxItem sel ? (string)sel.Tag : "";
        SettingsStore.AltBaseUrl = altUrl;
        SettingsStore.AltModel = altModel;
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
