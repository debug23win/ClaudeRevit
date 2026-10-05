using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClaudeRevit.Services;

namespace ClaudeRevit.UI;

// An unowned top-level window has its own STA/input queue. Owning/parenting it to
// Revit would couple input queues again. No Revit API object is used on this thread.
public static class ChatWindowHost
{
    private static readonly object Gate = new();
    private static TaskCompletionSource<Window>? _ready;
    private static Dispatcher? _dispatcher;
    private static readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool _stopping;
    public static bool UsesWindow { get; private set; }
    public static void Initialize(bool usesWindow) { UsesWindow = usesWindow; }

    internal static Task<Window> GetWindowAsync()
    {
        lock (Gate)
        {
            if (_stopping) return Task.FromException<Window>(new InvalidOperationException("Revit is shutting down."));
            if (_ready != null) return _ready.Task;
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(Run) { IsBackground = true, Name = "ClaudeRevit.ChatUI" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            return _ready.Task;
        }
    }
    private static void Run()
    {
        ChatPaneView? view = null;
        Window? window = null;
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            lock (Gate) _dispatcher = dispatcher;
            view = new ChatPaneView();
            window = new Window
            {
                Title = "Claude Revit — Chat", Content = view, Width = 440, Height = 760,
                MinWidth = 280, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Closing += (_, e) => { if (!_stopping) { e.Cancel = true; window.Hide(); } };
            lock (Gate)
            {
                _ready!.TrySetResult(window);
                if (_stopping) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
            Dispatcher.Run();
        }
        catch (Exception ex) { lock (Gate) _ready?.TrySetException(ex); Log.Error("Independent chat UI failed", ex); }
        finally
        {
            try { view?.Dispose(); if (window != null) { _stopping = true; window.Close(); } }
            catch (Exception ex) { Log.Error("Independent chat cleanup failed", ex); }
            finally { Closed.TrySetResult(); }
        }
    }
    public static async Task ShowAsync(bool toggle = false)
    {
        try
        {
            var window = await GetWindowAsync().ConfigureAwait(false);
            await window.Dispatcher.InvokeAsync(() =>
            {
                if (_stopping) return;
                if (toggle && window.IsVisible) { window.Hide(); return; }
                window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            });
        }
        catch (Exception ex) { Log.Error("Opening independent chat failed", ex); }
    }
    public static FrameworkElement CreateLauncher()
    {
        var russian = SettingsStore.UiLanguage == "ru";
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10),
            Text = russian ? "Чат работает в отдельном окне и остаётся доступным во время скриптов. Режим можно изменить в настройках чата." :
                "Chat uses a separate window so it stays responsive during scripts. Change the interface mode in chat settings." });
        var button = new Button { Content = russian ? "Открыть чат" : "Open chat", Padding = new Thickness(10,6,10,6) };
        button.Click += async (_, _) => await ShowAsync(); panel.Children.Add(button); return panel;
    }
    public static void Shutdown()
    {
        lock (Gate)
        {
            _stopping = true;
            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        // Do not wait on Revit's API thread for chat/tool cleanup: that cleanup may
        // itself be waiting for a queued external event.
    }
    internal static Task ShutdownCompletion { get { lock (Gate) return _ready == null ? Task.CompletedTask : Closed.Task; } }
}
