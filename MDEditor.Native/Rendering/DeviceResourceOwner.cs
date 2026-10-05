namespace MDEditor.Native.Rendering;

/// <summary>Owns one device-resource set; used only on the canvas UI thread.</summary>
internal sealed class DeviceResourceOwner<T> : IDisposable where T : class, IDisposable
{
    private bool _disposed;
    public T? Current { get; private set; }

    public void Replace(Func<T> create)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(create);
        // Construct first: failed creation must not destroy the previously owned set.
        var replacement = create() ?? throw new InvalidOperationException("Resource factory returned null.");
        var previous = Current;
        Current = replacement;
        if (!ReferenceEquals(previous, replacement))
        {
            previous?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var previous = Current;
        Current = null;
        previous?.Dispose();
    }
}
