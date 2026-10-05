namespace MDEditor.Native.Rendering;

// Full editor background, independent of the document's text padding. All values are DIPs.
internal readonly record struct CanvasSceneBounds(double X, double Y, double Width, double Height)
{
    public static CanvasSceneBounds FromViewport(double width, double height)
    {
        if (!double.IsFinite(width) || width < 0 || width > float.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height < 0 || height > float.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(height));

        return new(0, 0, width, height);
    }
}
