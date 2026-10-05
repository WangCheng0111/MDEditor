using Microsoft.UI.Xaml;

namespace MDEditor;

public sealed partial class MainWindow
{
    private void InitializeEditorCanvas()
    {
        Activated += EditorCanvas_WindowActivated;
        Closed += EditorCanvas_WindowClosed;
    }

    private void EditorCanvas_WindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            EditorSurface.Invalidate();
    }

    private void EditorCanvas_WindowClosed(object sender, WindowEventArgs args)
    {
        Activated -= EditorCanvas_WindowActivated;
        Closed -= EditorCanvas_WindowClosed;
        EditorSurface.Dispose();
    }
}
