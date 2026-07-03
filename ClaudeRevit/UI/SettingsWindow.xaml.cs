using System.Globalization;
using System.Windows;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class SettingsWindow : Window
{
    private readonly decimal _initialBalanceRemaining;

    public SettingsWindow()
    {
        InitializeComponent();
        var existing = ApiKeyStore.Load();
        if (!string.IsNullOrEmpty(existing)) ApiKeyBox.Password = existing;
        AllowCodeBox.IsChecked = SettingsStore.AllowCodeExecution;
        ConfirmOpsBox.IsChecked = SettingsStore.ConfirmOperations;

        // Show the current remaining estimate; only a CHANGED value resets the counter.
        _initialBalanceRemaining = SettingsStore.BalanceUsd > 0
            ? SettingsStore.BalanceUsd - SettingsStore.SpentUsd
            : 0;
        if (_initialBalanceRemaining > 0)
            BalanceBox.Text = _initialBalanceRemaining.ToString("F2", CultureInfo.InvariantCulture);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (string.IsNullOrEmpty(key))
        {
            MessageBox.Show(this,
                "API key cannot be empty.",
                "Claude Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApiKeyStore.Save(key);
        SettingsStore.AllowCodeExecution = AllowCodeBox.IsChecked == true;
        SettingsStore.ConfirmOperations = ConfirmOpsBox.IsChecked == true;

        var balanceText = BalanceBox.Text.Trim().Replace(',', '.');
        if (balanceText.Length == 0)
        {
            if (SettingsStore.BalanceUsd > 0) SettingsStore.SetBalance(0); // cleared → hide
        }
        else if (decimal.TryParse(balanceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance)
                 && balance >= 0)
        {
            // Treat an unchanged displayed remainder as "no update".
            if (System.Math.Abs(balance - _initialBalanceRemaining) > 0.005m)
                SettingsStore.SetBalance(balance);
        }
        else
        {
            MessageBox.Show(this,
                "Balance must be a number (e.g. 25.00) or empty.",
                "Claude Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
