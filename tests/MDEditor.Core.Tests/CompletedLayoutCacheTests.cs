using MDEditor.Native.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class CompletedLayoutCacheTests
{
    private sealed class Frame : IDisposable
    {
        public int Closes { get; private set; }
        public void Dispose() => Closes++;
    }
    [TestMethod]
    public void BorrowedFrameSurvivesSwitchAndIsReleasedOnceOnClear()
    {
        using var cache = new CompletedLayoutCache<Frame>(32);
        var first = new Frame(); var second = new Frame();
        cache.Remember(600.125, first); cache.Remember(300.0625, second);
        Assert.IsTrue(cache.TryGet(600.125, out var presented)); Assert.AreSame(first, presented);
        Assert.AreEqual(0, first.Closes); Assert.AreEqual(0, second.Closes);
        cache.Clear(); Assert.AreEqual(1, first.Closes); Assert.AreEqual(1, second.Closes);
        cache.Clear(); Assert.AreEqual(1, first.Closes);
    }
    [TestMethod]
    public void HitPromotesLruAndEvictionReleasesOnlyOldest()
    {
        using var cache = new CompletedLayoutCache<Frame>(2);
        var a = new Frame(); var b = new Frame(); var c = new Frame();
        cache.Remember(1, a); cache.Remember(2, b);
        Assert.IsTrue(cache.TryGet(1, out _)); cache.Remember(3, c);
        Assert.IsFalse(cache.TryGet(2, out _)); Assert.AreEqual(1, b.Closes);
        Assert.IsTrue(cache.TryGet(1, out _)); Assert.AreEqual(0, a.Closes);
        Assert.AreEqual(2, cache.Count);
    }
    [TestMethod]
    public void ReplacingSameKeyClosesOldButIdenticalOwnerDoesNotClose()
    {
        using var cache = new CompletedLayoutCache<Frame>(2);
        var a = new Frame(); var b = new Frame();
        cache.Remember(1, a); cache.Remember(1, a); Assert.AreEqual(0, a.Closes);
        cache.Remember(1, b); Assert.AreEqual(1, a.Closes); Assert.AreEqual(0, b.Closes);
        Assert.IsTrue(cache.TryGet(1, out var current)); Assert.AreSame(b, current);
    }
    [TestMethod]
    public void DeviceEpochClearDoesNotReuseForeignResource()
    {
        using var cache = new CompletedLayoutCache<Frame>(32);
        var old = new Frame(); cache.Remember(500, old); cache.Clear();
        Assert.IsFalse(cache.TryGet(500, out _)); Assert.AreEqual(1, old.Closes);
        var fresh = new Frame(); cache.Remember(500, fresh);
        Assert.IsTrue(cache.TryGet(500, out var result)); Assert.AreSame(fresh, result);
    }
    [TestMethod]
    public void DisposeIsIdempotentAndRejectsFurtherOwnership()
    {
        var cache = new CompletedLayoutCache<Frame>(2); var owned = new Frame(); cache.Remember(2, owned);
        cache.Dispose(); cache.Dispose(); Assert.AreEqual(1, owned.Closes);
        var callerOwned = new Frame();
        Assert.Throws<ObjectDisposedException>(() => cache.Remember(2, callerOwned));
        Assert.Throws<ObjectDisposedException>(() => cache.TryGet(2, out _)); Assert.AreEqual(0, callerOwned.Closes);
    }
    [TestMethod]
    public void InvalidCapacityWidthAndDuplicateOwnershipRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompletedLayoutCache<Frame>(0));
        using var cache = new CompletedLayoutCache<Frame>(2); var frame = new Frame();
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Remember(double.NaN, frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Remember(-1, frame));
        cache.Remember(1, frame);
        Assert.Throws<ArgumentException>(() => cache.Remember(2, frame));
        Assert.AreEqual(0, frame.Closes); Assert.AreEqual(1, cache.Count);
    }
    [TestMethod]
    public void EveryZoomFrameFitsWithinBoundAndWarmWidthSwitchDoesNotSolve()
    {
        using var cache = new CompletedLayoutCache<Frame>(32);
        var frames = new List<Frame>();
        for (var percent = 50; percent <= 300; percent += 10)
        {
            var frame = new Frame(); frames.Add(frame);
            cache.Remember(new MDEditor.Typesetting.Layout.DocumentViewport(1200, 600, percent).DocumentWidth, frame);
        }
        var requested = new MDEditor.Typesetting.Layout.DocumentViewport(1200, 600, 100);
        Assert.IsTrue(cache.TryGet(requested.DocumentWidth, out var frame100)); Assert.AreSame(frames[5], frame100);
        Assert.AreEqual(26, cache.Count); Assert.IsTrue(frames.All(f => f.Closes == 0));
    }

    [TestMethod]
    public void Weighted_budget_evicts_old_frames_and_keeps_one_oversized_current_frame()
    {
        using var cache = new CompletedLayoutCache<Frame>(32, _ => 40, 100);
        var first = new Frame(); var second = new Frame(); var third = new Frame();
        cache.Remember(1, first); cache.Remember(2, second); cache.Remember(3, third);
        Assert.AreEqual(2, cache.Count); Assert.AreEqual(80L, cache.EstimatedCost);
        Assert.AreEqual(1, first.Closes); Assert.AreEqual(0, second.Closes);
        cache.Clear(); Assert.AreEqual(0L, cache.EstimatedCost);

        using var huge = new CompletedLayoutCache<Frame>(32, _ => 200, 100);
        var current = new Frame(); huge.Remember(4, current);
        Assert.AreEqual(1, huge.Count); Assert.AreEqual(200L, huge.EstimatedCost);
        Assert.AreEqual(0, current.Closes);
    }
}
