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
                DocumentHarness.Change("a", false);
                var pane = new ChatPaneView();
                CheckRepeatedDocumentWrappers(pane);
                DocumentHarness.Change("a", false);
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
                var attachmentRoot = Path.Combine(Path.GetTempPath(), "ClaudeRevit-pane-attachments-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(attachmentRoot);
                var firstFile = Path.Combine(attachmentRoot, "Первый.txt"); var secondFile = Path.Combine(attachmentRoot, "Second.csv");
                File.WriteAllText(firstFile, "Размер 3500 мм"); File.WriteAllText(secondFile, "diameter,16");
                var imageFile = Path.Combine(attachmentRoot, "reference.png");
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(Brushes.Blue, null, new Rect(0, 0, 2000, 1000));
                var sourceImage = new RenderTargetBitmap(2000, 1000, 96, 96, PixelFormats.Pbgra32); sourceImage.Render(visual);
                var imageEncoder = new PngBitmapEncoder(); imageEncoder.Frames.Add(BitmapFrame.Create(sourceImage));
                using (var imageStream = File.Create(imageFile)) imageEncoder.Save(imageStream);
                var encodedImage = await Task.Run(() => AttachmentImage.EncodePng(imageFile));
                using (var imageStream = new MemoryStream(encodedImage))
                {
                    var image = new PngBitmapDecoder(imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    Check(image.PixelWidth == 1568 && image.PixelHeight == 784, "Attached image was not resized/encoded for vision");
                }
                var noise = new byte[1568 * 1568 * 4]; new Random(17).NextBytes(noise);
                for (var i = 3; i < noise.Length; i += 4) noise[i] = 255;
                var noisyImage = BitmapSource.Create(1568, 1568, 96, 96, PixelFormats.Bgra32, null, noise, 1568 * 4);
                var noisyEncoder = new PngBitmapEncoder(); noisyEncoder.Frames.Add(BitmapFrame.Create(noisyImage));
                var noisePath = Path.Combine(attachmentRoot, "noise.png");
                using (var output = File.Create(noisePath)) noisyEncoder.Save(output);
                Check(new FileInfo(noisePath).Length > 5_000_000, "Dense image fixture did not exercise the image size cap");
                var smallNoise = await Task.Run(() => AttachmentImage.EncodePng(noisePath));
                Check(smallNoise.Length <= 5_000_000, "Dense attachment PNG exceeded the native image content cap");
                var attach = typeof(ChatPaneView).GetMethod("AttachFilesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                await (Task)attach.Invoke(pane, new object[] { new[] { firstFile, secondFile }, Path.Combine(attachmentRoot, "stage") })!;
                Check(((ItemsControl)pane.FindName("AttachmentList")).Items.Count == 2, "Multiple attachments were not staged during a turn");
                Check(((Button)pane.FindName("SendButton")).IsEnabled && ((Button)pane.FindName("StopButton")).Visibility == Visibility.Visible, "Supplement and Stop controls were not separate");
                ((TextBox)pane.FindName("InputBox")).Text = "Дополнение: высота 3500";
                await (Task)send.Invoke(pane, null)!;
                Check(!pending.IsCompleted, "Supplement cancelled or restarted the active turn");
                var paneService = (ChatService)typeof(ChatPaneView).GetProperty("_service", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pane)!;
                Check(paneService.Supplements.Single().Attachments.Count == 2 && paneService.Supplements.Single().Text.Contains("3500"), "Supplement lost its documents or text");
                Check(((ItemsControl)pane.FindName("AttachmentList")).Items.Count == 0, "Sent attachments remained in the draft");
                ((TextBox)pane.FindName("InputBox")).Text = "Unsent draft";
                for (var i = 0; i < 20; i++) DocumentHarness.Change("a", false);
                Check(!pending.IsCompleted, "Repeated document wrappers cancelled the active chat turn");
                ChatService.Pending.SetResult();
                await pending;
                Check(((TextBox)pane.FindName("InputBox")).Text == "Unsent draft", "Completing a turn erased an unsent supplement");
                Check(agent.IsEnabled && model.IsEnabled && effort.IsEnabled, "Selection was not restored after a turn");
                pane.Messages.Clear();
                pane.Messages.Add(new ChatMessage { Role = "user", Text = "Project A" });
                await (Task)attach.Invoke(pane, new object[] { new[] { firstFile }, Path.Combine(attachmentRoot, "stage") })!;
                DocumentHarness.Change("b", false);
                Check(((ItemsControl)pane.FindName("AttachmentList")).Items.Count == 0, "A's attachments leaked into B");
                Check(pane.Messages.Count == 0, "Another project's transcript was shown in B");
                agent.SelectedIndex=1;
                model.SelectedValue="sonnet";
                effort.SelectedValue="low";
                pane.Messages.Add(new ChatMessage { Role = "user", Text = "Project B" });
                DocumentHarness.Change("a", false);
                Check(((ItemsControl)pane.FindName("AttachmentList")).Items.Count == 1, "Returning to A lost its pending attachment");
                Check(pane.Messages.Single().Text == "Project A", "Returning to A lost its transcript");
                Check((string)model.SelectedValue=="gpt-test" && (string)effort.SelectedValue=="high","A's selected model/effort leaked from B");
                agent.SelectedIndex=1;
                Check((string)model.SelectedValue=="opus" && (string)effort.SelectedValue=="medium","A's other provider choices leaked from B");
                agent.SelectedIndex=2;
                ChatService.Pending = new();
                ((TextBox)pane.FindName("InputBox")).Text = "Pending A turn";
                var cancelled = (Task)send.Invoke(pane, null)!;
                var pendingA=ChatService.Pending;
                DocumentHarness.Change("b", false);
                Check(!cancelled.IsCompleted,"Switching tabs cancelled A");
                Check(((ComboBox)pane.FindName("AgentPicker")).IsEnabled,"A's busy state blocked B");
                ((TextBox)pane.FindName("InputBox")).Text="Draft B";
                pendingA.SetResult();await cancelled;
                Check(((TextBox)pane.FindName("InputBox")).Text=="Draft B","A's completion erased B's draft");
                Check(pane.Messages.Single().Text == "Project B", "A turn was saved into B");
                Check(ChatService.Saved["file:A"].Any(m => m.Text == "Pending A turn"), "A's cancelled transcript was lost");
                ChatService.Pending = new();
                ((TextBox)pane.FindName("InputBox")).Text = "Open family from B";
                var managed = (Task)send.Invoke(pane, null)!;
                DocumentHarness.Change("family", true);
                Check(!managed.IsCompleted, "A managed family transition cancelled its own task");
                ChatService.Pending.SetResult();
                await managed;
                Check(pane.Messages.Count == 0, "Family inherited B's conversation after the task finished");
                ChatService.Pending = null;
                if (args.Length > 0)
                {
                    await (Task)attach.Invoke(pane, new object[] { new[] { firstFile, secondFile }, Path.Combine(attachmentRoot, "stage") })!;
                    ChatService.Pending = new();
                    ((TextBox)pane.FindName("InputBox")).Text = "Проверь документы и подготовь семейство";
                    var visualTurn = (Task)send.Invoke(pane, null)!;
                    await (Task)attach.Invoke(pane, new object[] { new[] { firstFile, secondFile }, Path.Combine(attachmentRoot, "stage") })!;
                    ((TextBox)pane.FindName("InputBox")).Text = "Дополнение: высота 3500 мм";
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
                    ChatService.Pending.SetResult(); await visualTurn; ChatService.Pending = null;
                }
                Directory.Delete(attachmentRoot, true);
                Console.WriteLine("Pane checks passed: agent selection, multiple attachments, image decoding/resizing, live supplements, separate Stop, draft preservation, project isolation, independent runs and narrow layout.");
                await CheckBenchmark(args.FirstOrDefault());
                app.Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(1); }
        };
        app.Run();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void CheckRepeatedDocumentWrappers(ChatPaneView pane)
    {
        var events = new Autodesk.Revit.UI.UIControlledApplication();
        var state = new Autodesk.Revit.DB.DocumentState(); // unsaved document
        var app = new Autodesk.Revit.UI.UIApplication { ActiveUIDocument = new(state) };
        DocumentSessions.Initialize(events);
        events.Activate(state);
        var key = DocumentSessions.CurrentDocumentKey;
        var workspace = DocumentSessions.CurrentWorkspace;
        var input = (TextBox)pane.FindName("InputBox");
        input.Text = "Сложное вложенное семейство / nested family";
        var changes = 0;
        void Changed(bool managed) => changes++;
        DocumentSessions.Changed += Changed;
        try
        {
            for (var i = 0; i < 30; i++)
            {
                events.Idle(app);
                events.Activate(state);
                Check(DocumentSessions.Key(app.ActiveUIDocument.Document) == key,
                    "Same open document acquired a new key before copying the benchmark seed");
            }
            Check(changes == 0, "Repeated wrappers raised false document changes");
            Check(ReferenceEquals(workspace, DocumentSessions.CurrentWorkspace), "Unsaved document workspace changed while typing");
            Check(input.Text == "Сложное вложенное семейство / nested family", "Idling erased typed chat text");

            state.PathName = "C:/fixtures/seed.rvt";
            events.Idle(app);
            Check(DocumentSessions.CurrentDocumentKey == key, "Saving changed the open document key");
            changes = 0;
            for (var i = 0; i < 10; i++) events.Idle(app);
            Check(changes == 0 && DocumentSessions.CurrentDocumentKey == key, "Saved seed changed key on repeated access");
            var wrapper = app.ActiveUIDocument.Document;
            Check(!ReferenceEquals(wrapper, app.ActiveUIDocument.Document) &&
                DocumentSessions.Same(wrapper, app.ActiveUIDocument.Document),
                "Benchmark cleanup failed to recognize the active scratch document through another wrapper");
            var different = new Autodesk.Revit.DB.DocumentState { PathName = state.PathName };
            Check(DocumentSessions.Key(new(different)) != key, "Distinct documents with the same path/hash shared a key");
            Check(!DocumentSessions.Same(wrapper, new(different)), "Benchmark cleanup would reactivate the seed after a real document switch");
            var unsavedA = new Autodesk.Revit.DB.DocumentState();
            var unsavedB = new Autodesk.Revit.DB.DocumentState();
            Check(DocumentSessions.Key(new(unsavedA)) != DocumentSessions.Key(new(unsavedB)), "Distinct unsaved documents shared a key");
            state.Valid = false;
            Check(!DocumentSessions.Same(wrapper, new(state)) && DocumentSessions.Key(wrapper) == "none", "Closed document remained a valid target");
            Check(DocumentSessions.Key(new(new() { PathName = state.PathName })) != key, "Reopened document reused a retired key");
            Console.WriteLine("Document identity checks passed: repeated API wrappers, unsaved chat draft, saved benchmark seed, colliding hashes/paths and reopened documents.");
        }
        finally { DocumentSessions.Changed -= Changed; }
    }
    private static async Task CheckBenchmark(string? output)
    {
        var window = new BenchmarkWindow();
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        await System.Windows.Threading.Dispatcher.Yield();
        ComboBox Box(string name) => (ComboBox)window.FindName(name);
        var backend = Box("BackendBox"); var judgeBackend = Box("JudgeBackendBox");
        Check(((ComboBoxItem)backend.SelectedItem).Tag.Equals("codex") && ((ComboBoxItem)judgeBackend.SelectedItem).Tag.Equals("codex"), "Benchmark did not default to subscriptions");
        var models = Box("ModelBox"); var judge = Box("JudgeBox"); var effort = Box("EffortBox"); var judgeEffort = Box("JudgeEffortBox");
        models.SelectedIndex = 1; effort.SelectedItem = "low"; judge.SelectedIndex = 0; judgeEffort.SelectedItem = "high";
        ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(BenchmarkRunner.Execution == new BenchmarkExecution("codex", "gpt-other", "low"), "Benchmark did not pass selected modeller model/effort");
        Check(BenchmarkRunner.Judge == new BenchmarkExecution("codex", "gpt-test", "high"), "Benchmark did not pass independent judge model/effort");
        Check(!backend.IsEnabled && !judge.IsEnabled && !effort.IsEnabled, "Benchmark choices remain enabled while running");
        await Task.Run(() => BenchmarkRunner.Status!("background progress"));
        await System.Windows.Threading.Dispatcher.Yield();
        Check(((TextBlock)window.FindName("NowText")).Text.StartsWith("background progress"), "Background benchmark status was not marshalled to the UI");
        BenchmarkRunner.Result!(new BenchmarkResult { Verdict = "✓", Quality = 90, Speed = 50, Score = 81, Seconds = 120 });
        BenchmarkRunner.Result!(new BenchmarkResult { Verdict = "—" });
        BenchmarkRunner.Result!(new BenchmarkResult { Verdict = "?" });
        Check(((TextBlock)window.FindName("SummaryText")).Text.Contains("Total 81") &&
              ((TextBlock)window.FindName("SummaryText")).Text.Contains("1 skipped · 1 ungraded"), "Scores averaged skipped/ungraded tasks or were not displayed");
        var grid = (DataGrid)window.FindName("ResultsGrid");
        Check(new[] { "Quality", "Speed", "Total /100" }.All(h => grid.Columns.Any(c => (string)c.Header == h)), "Benchmark score columns missing");
        BenchmarkRunner.Pending.SetResult(); await System.Windows.Threading.Dispatcher.Yield();
        Check(backend.IsEnabled && (string)judgeEffort.SelectedItem == "high", "Benchmark altered effort after run");
        backend.SelectedIndex = 1; judgeBackend.SelectedIndex = 1;
        await System.Windows.Threading.Dispatcher.Yield();
        models.Text = "custom-claude-version"; judge.SelectedIndex = 1; judgeEffort.SelectedItem = "medium";
        BenchmarkRunner.Pending = new();
        ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(BenchmarkRunner.Execution?.Model == "custom-claude-version" && BenchmarkRunner.Execution.Backend == "claudecode", "Typed benchmark model was ignored");
        ((Button)window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await System.Windows.Threading.Dispatcher.Yield();
        Check(backend.IsEnabled, "Benchmark stop did not restore controls");
        backend.SelectedIndex = 2; judgeBackend.SelectedIndex = 2; await System.Windows.Threading.Dispatcher.Yield();
        Check(!effort.IsEnabled && !judgeEffort.IsEnabled, "API model retained subscription effort controls");
        if (output != null)
        {
            window.ShowActivated = false; window.Opacity = 0; window.Left = -20000; window.Top = -20000;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Show();
            backend.SelectedIndex = 0; judgeBackend.SelectedIndex = 0; await System.Windows.Threading.Dispatcher.Yield();
            foreach (var width in new[] { 780, 960 })
            {
                window.Width = width; window.Height = 700;
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(width, 660)); content.Arrange(new Rect(0, 0, width, 660)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, 660, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual(); using (var dc = background.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, 660));
                bitmap.Render(background); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"benchmark-{width}.png")); encoder.Save(stream);
            }
        }
        window.Close();
        Console.WriteLine("Benchmark UI checks passed: default subscriptions, independent modeller/judge choices, custom models, busy/stop controls, effort preservation, API separation and layout.");
    }
}
