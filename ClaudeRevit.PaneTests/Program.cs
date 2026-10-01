using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeRevit.Services;
using ClaudeRevit.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            try
            {
                var pane = new ChatPaneView();
                var agent = (ComboBox)pane.FindName("AgentPicker");
                var model = (ComboBox)pane.FindName("McpModelPicker");
                var effort = (ComboBox)pane.FindName("McpEffortPicker");
                agent.SelectedIndex = 2;
                model.SelectedValue = "gpt-test";
                effort.SelectedValue = "high";
                Check(SettingsStore.GetAgentSelection("codex").Effort == "high", "Codex effort was not saved");
                agent.SelectedIndex = 1;
                model.SelectedValue = "opus";
                effort.SelectedValue = "medium";
                agent.SelectedIndex = 2;
                Check((string)model.SelectedValue == "gpt-test" && (string)effort.SelectedValue == "high", "Codex choices were not restored");
                model.SelectedValue = "gpt-other";
                Check((string)effort.SelectedValue == "", "Old model effort carried across models");
                model.Text = "custom-model-id";
                ((TextBox)pane.FindName("InputBox")).Text = "Test message";
                var send = typeof(ChatPaneView).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                await (Task)send.Invoke(pane, null)!;
                Check(ChatService.SentSelection?.Model == "custom-model-id", "Typed model was ignored on send");
                Check(ChatService.SentSelection?.Agent == "codex", "Wrong agent on send");
                Check(SettingsStore.GetAgentSelection("claudecode").Model == "opus", "Claude model was overwritten by Codex");
                model.SelectedValue = "gpt-test";
                effort.SelectedValue = "high";
                ChatService.Pending = new();
                ((TextBox)pane.FindName("InputBox")).Text = "Busy controls test";
                var pending = (Task)send.Invoke(pane, null)!;
                Check(!agent.IsEnabled && !model.IsEnabled && !effort.IsEnabled, "Selection stayed enabled during a turn");
                Check(!((Button)pane.FindName("SettingsButton")).IsEnabled, "Settings stayed enabled during a turn");
                ChatService.Pending.SetResult();
                await pending;
                Check(agent.IsEnabled && model.IsEnabled && effort.IsEnabled, "Selection was not restored after a turn");
                pane.Messages.Clear();
                pane.Messages.Add(new ChatMessage { Role = "user", Text = "Project A" });
                DocumentSessions.Change("b", false);
                Check(pane.Messages.Count == 0, "Another project's transcript was shown in B");
                pane.Messages.Add(new ChatMessage { Role = "user", Text = "Project B" });
                DocumentSessions.Change("a", false);
                Check(pane.Messages.Single().Text == "Project A", "Returning to A lost its transcript");
                ChatService.Pending = new();
                ((TextBox)pane.FindName("InputBox")).Text = "Pending A turn";
                var cancelled = (Task)send.Invoke(pane, null)!;
                DocumentSessions.Change("b", false);
                await cancelled;
                Check(pane.Messages.Single().Text == "Project B", "Cancelled A turn was saved into B");
                Check(ChatService.Saved["a"].Any(m => m.Text == "Pending A turn"), "A's cancelled transcript was lost");
                ChatService.Pending = new();
                ((TextBox)pane.FindName("InputBox")).Text = "Open family from B";
                var managed = (Task)send.Invoke(pane, null)!;
                DocumentSessions.Change("family", true);
                Check(!managed.IsCompleted, "A managed family transition cancelled its own task");
                ChatService.Pending.SetResult();
                await managed;
                Check(pane.Messages.Count == 0, "Family inherited B's conversation after the task finished");
                ChatService.Pending = null;
                if (args.Length > 0)
                {
                    Directory.CreateDirectory(args[0]);
                    foreach (var width in new[] { 380, 280 })
                    {
                        pane.Measure(new Size(width, 600));
                        pane.Arrange(new Rect(0, 0, width, 600));
                        pane.UpdateLayout();
                        var settings = (Button)pane.FindName("SettingsButton");
                        var bounds = settings.TransformToAncestor(pane).TransformBounds(new Rect(settings.RenderSize));
                        Check(bounds.Right <= width, "Toolbar clips in a narrow pane");
                        var bitmap = new RenderTargetBitmap(width, 600, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(pane);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(args[0], $"pane-{width}.png"));
                        encoder.Save(stream);
                    }
                }
                Console.WriteLine("Pane checks passed: agent selection, saved choices, busy controls, project history switching, cancellation on manual document change, managed family transition, narrow layout.");
                app.Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(1); }
        };
        app.Run();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
