namespace MDEditor.Native.Rendering;

/// <summary>A cache reference and independent frame leases. Last release closes the resource once.</summary>
internal sealed class SharedResource<T> : IDisposable where T : class, IDisposable
{
    internal sealed class State(T value)
    {
        internal readonly T Value = value;
        internal int References = 1;
    }
    private State? _state;
    internal SharedResource(T value) => _state = new(value ?? throw new ArgumentNullException(nameof(value)));
    internal Lease Acquire()
    {
        var state = Volatile.Read(ref _state) ?? throw new ObjectDisposedException(nameof(SharedResource<T>));
        while (true)
        {
            var count = Volatile.Read(ref state.References);
            if (count == 0) throw new ObjectDisposedException(nameof(SharedResource<T>));
            if (count == int.MaxValue) throw new OverflowException("Too many resource leases.");
            if (Interlocked.CompareExchange(ref state.References, count + 1, count) == count) return new(state);
        }
    }
    public void Dispose() => Release(Interlocked.Exchange(ref _state, null));
    private static void Release(State? state)
    {
        if (state is not null && Interlocked.Decrement(ref state.References) == 0) state.Value.Dispose();
    }
    internal sealed class Lease : IDisposable
    {
        private State? _state;
        internal Lease(State state) => _state = state;
        internal T Value => Volatile.Read(ref _state)?.Value ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose() => Release(Interlocked.Exchange(ref _state, null));
    }
}
