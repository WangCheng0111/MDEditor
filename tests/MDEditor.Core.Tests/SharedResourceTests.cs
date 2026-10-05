using MDEditor.Native.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class SharedResourceTests
{
    private sealed class Resource : IDisposable
    {
        private int _closes;
        internal int Closes => Volatile.Read(ref _closes);
        public void Dispose() => Interlocked.Increment(ref _closes);
    }
    [TestMethod]
    public void EvictionLeavesPresentedFrameAliveUntilItsLeaseEnds()
    {
        var value = new Resource(); var cache = new SharedResource<Resource>(value);
        var frame = cache.Acquire(); cache.Dispose();
        Assert.AreSame(value, frame.Value); Assert.AreEqual(0, value.Closes);
        frame.Dispose(); Assert.AreEqual(1, value.Closes);
    }
    [TestMethod]
    public void EveryFrameHasAnIndependentLease()
    {
        var value = new Resource(); using var cache = new SharedResource<Resource>(value);
        var first = cache.Acquire(); var second = cache.Acquire();
        cache.Dispose(); first.Dispose(); Assert.AreSame(value, second.Value); Assert.AreEqual(0, value.Closes);
        second.Dispose(); Assert.AreEqual(1, value.Closes);
    }
    [TestMethod]
    public void RepeatedFrameAndCacheDisposalClosesExactlyOnce()
    {
        var value = new Resource(); var cache = new SharedResource<Resource>(value); var frame = cache.Acquire();
        cache.Dispose(); cache.Dispose(); frame.Dispose(); frame.Dispose(); Assert.AreEqual(1, value.Closes);
    }
    [TestMethod]
    public void ReleasedLeaseCannotReadNativeResource()
    {
        using var cache = new SharedResource<Resource>(new()); var frame = cache.Acquire(); frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Value);
    }
    [TestMethod]
    public void ClosedCacheCannotCreateNewFramesEvenIfOneFrameSurvives()
    {
        var value = new Resource(); var cache = new SharedResource<Resource>(value); using var frame = cache.Acquire();
        cache.Dispose(); Assert.Throws<ObjectDisposedException>(() => cache.Acquire()); Assert.AreEqual(0, value.Closes);
    }
    [TestMethod]
    public void ConcurrentLeaseReleaseIsSafeWithCacheEviction()
    {
        var value = new Resource(); var cache = new SharedResource<Resource>(value);
        var frames = Enumerable.Range(0, 5000).Select(_ => cache.Acquire()).ToArray(); cache.Dispose();
        Parallel.ForEach(frames, frame => { Assert.AreSame(value, frame.Value); frame.Dispose(); frame.Dispose(); });
        Assert.AreEqual(1, value.Closes);
    }
    [TestMethod]
    public void AcquireAndCacheDisposalRaceNeverResurrectsAClosedResource()
    {
        for (var round = 0; round < 200; round++)
        {
            var value = new Resource(); var cache = new SharedResource<Resource>(value);
            Parallel.Invoke(() => cache.Dispose(), () =>
            {
                try { using var frame = cache.Acquire(); Assert.AreSame(value, frame.Value); Assert.AreEqual(0, value.Closes); }
                catch (ObjectDisposedException) { }
            });
            Assert.AreEqual(1, value.Closes);
        }
    }
    [TestMethod]
    public void NullOwnerIsRejected() => Assert.Throws<ArgumentNullException>(() => new SharedResource<Resource>(null!));
}
