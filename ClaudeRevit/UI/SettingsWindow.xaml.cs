using System.Windows;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var existing = ApiKeyStore.Load();
        if (!string.IsNullOrEmpty(existing)) ApiKeyBox.Password = existing;
        AllowCodeBox.IsChecked = SettingsStore.AllowCodeExecution;
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

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
