using System.Globalization;
using System.Windows;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class SettingsWindow : Window
{
    // Exact text shown at load: unchanged text means "no balance update" (string equality
    // — no format/epsilon coupling), and an exhausted balance shows 0.00 rather than an
    // empty box so an untouched Save can never be mistaken for "user cleared the field".
    private readonly string _initialBalanceText;

    public SettingsWindow()
    {
        InitializeComponent();
        var existing = ApiKeyStore.Load();
        if (!string.IsNullOrEmpty(existing)) ApiKeyBox.Password = existing;
        AllowCodeBox.IsChecked = SettingsStore.AllowCodeExecution;
        ConfirmOpsBox.IsChecked = SettingsStore.ConfirmOperations;

        _initialBalanceText = SettingsStore.BalanceUsd > 0
            ? System.Math.Max(0, SettingsStore.BalanceUsd - SettingsStore.SpentUsd)
                .ToString("F2", CultureInfo.InvariantCulture)
            : "";
        BalanceBox.Text = _initialBalanceText;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Validate EVERYTHING before persisting anything, so Save is atomic and
        // DialogResult=true always means "settings were saved" (ChatPaneView recreates
        // the API client only on true).
        var key = ApiKeyBox.Password.Trim();
        if (string.IsNullOrEmpty(key))
        {
            MessageBox.Show(this,
                "API key cannot be empty.",
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

        ApiKeyStore.Save(key);
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
