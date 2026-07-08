using System.Windows;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Title = $"Claude Revit {UpdateChecker.CurrentVersion} — Help / Помощь";
    }
}
