using Autodesk.Revit.UI;

namespace ClaudeRevit.UI;

public class ChatPaneProvider : IDockablePaneProvider
{
    private readonly System.Windows.FrameworkElement _view;

    public ChatPaneProvider(System.Windows.FrameworkElement view) => _view = view;

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _view;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right
        };
    }
}
