namespace MDEditor.Typesetting.Layout;

// Document-local DIP coordinates. Preview scale/DPI are deliberately outside the snapshot.
public readonly record struct LayoutPoint
{
    public double X { get; }
    public double Y { get; }
    public LayoutPoint(double x, double y)
    {
        LayoutValidation.Finite(x, nameof(x));
        LayoutValidation.Finite(y, nameof(y));
        X = x; Y = y;
    }
}

public readonly record struct LayoutRect
{
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public LayoutRect(double x, double y, double width, double height)
    {
        LayoutValidation.Finite(x, nameof(x));
        LayoutValidation.Finite(y, nameof(y));
        LayoutValidation.Nonnegative(width, nameof(width));
        LayoutValidation.Nonnegative(height, nameof(height));
        LayoutValidation.Finite(x + width, nameof(width));
        LayoutValidation.Finite(y + height, nameof(height));
        X = x; Y = y; Width = width; Height = height;
    }
    public bool Contains(LayoutRect other) => other.X >= X && other.Y >= Y &&
        other.Right <= Right && other.Bottom <= Bottom;
}

internal static class LayoutValidation
{
    public static System.Collections.Immutable.ImmutableArray<T> Freeze<T>(IEnumerable<T> values)
    {
        // Do not reuse caller-owned ImmutableArray storage (e.g. marshaled external backing arrays).
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>();
        foreach (var value in values) builder.Add(value);
        return builder.ToImmutable();
    }
    public static void Finite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name, "Must be finite.");
    }
    public static void Nonnegative(double value, string name)
    {
        Finite(value, name);
        if (value < 0) throw new ArgumentOutOfRangeException(name, "Must be nonnegative.");
    }
    public static void Positive(double value, string name)
    {
        Finite(value, name);
        if (value <= 0) throw new ArgumentOutOfRangeException(name, "Must be positive.");
    }
}
