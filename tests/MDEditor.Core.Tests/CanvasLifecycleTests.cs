using MDEditor.Native.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

// Source-linked platform-free ownership/geometry helpers; no Native runtime reference.
[TestClass]
public sealed class CanvasLifecycleTests
{
    [TestMethod]
    public void First_resource_set_is_owned_and_disposed_once()
    {
        var resource = new TestResource();
        var owner = new DeviceResourceOwner<TestResource>();
        owner.Replace(() => resource);
        Assert.AreSame(resource, owner.Current);
        owner.Dispose();
        owner.Dispose();
        Assert.IsNull(owner.Current);
        Assert.AreEqual(1, resource.DisposeCount);
    }

    [TestMethod]
    public void Recreating_resources_releases_previous_set()
    {
        using var owner = new DeviceResourceOwner<TestResource>();
        var first = new TestResource();
        var second = new TestResource();
        owner.Replace(() => first);
        owner.Replace(() => second);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(0, second.DisposeCount);
        Assert.AreSame(second, owner.Current);
    }

    [TestMethod]
    public void Failed_creation_keeps_previous_set_owned()
    {
        using var owner = new DeviceResourceOwner<TestResource>();
        var first = new TestResource();
        owner.Replace(() => first);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            owner.Replace(() => throw new InvalidOperationException("creation failed")));
        Assert.AreSame(first, owner.Current);
        Assert.AreEqual(0, first.DisposeCount);
    }

    [TestMethod]
    public void Disposed_owner_does_not_invoke_factory()
    {
        var owner = new DeviceResourceOwner<TestResource>();
        owner.Dispose();
        var invoked = false;
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            owner.Replace(() => { invoked = true; return new TestResource(); }));
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void Returning_same_resource_does_not_dispose_current_set()
    {
        using var owner = new DeviceResourceOwner<TestResource>();
        var first = new TestResource();
        owner.Replace(() => first);
        owner.Replace(() => first);
        Assert.AreEqual(0, first.DisposeCount);
    }

    [TestMethod]
    public void Null_factory_result_does_not_drop_existing_resources()
    {
        using var owner = new DeviceResourceOwner<TestResource>();
        var first = new TestResource();
        owner.Replace(() => first);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Replace(() => null!));
        Assert.AreSame(first, owner.Current);
    }

    [TestMethod]
    [DataRow(0d, 0d)]
    [DataRow(1d, 1d)]
    [DataRow(10d, 800d)]
    [DataRow(800d, 10d)]
    [DataRow(800d, 600d)]
    [DataRow(3840d, 2160d)]
    public void Scene_stays_inside_viewport_after_resize(double width, double height)
    {
        var bounds = CanvasSceneBounds.FromViewport(width, height);
        Assert.IsTrue(bounds.X >= 0 && bounds.Y >= 0);
        Assert.IsTrue(bounds.Width >= 0 && bounds.Height >= 0);
        Assert.IsTrue(bounds.X + bounds.Width <= width);
        Assert.IsTrue(bounds.Y + bounds.Height <= height);
        Assert.AreEqual(bounds, CanvasSceneBounds.FromViewport(width, height));
    }

    [TestMethod]
    public void Editor_background_has_no_inset_card_or_frame()
    {
        Assert.AreEqual(new CanvasSceneBounds(0, 0, 800, 600), CanvasSceneBounds.FromViewport(800, 600));
        Assert.AreEqual(new CanvasSceneBounds(0, 0, 1, 1), CanvasSceneBounds.FromViewport(1, 1));
    }

    [TestMethod]
    [DataRow(-1d, 100d)]
    [DataRow(100d, -1d)]
    [DataRow(double.NaN, 100d)]
    [DataRow(100d, double.PositiveInfinity)]
    [DataRow(double.MaxValue, 100d)]
    public void Invalid_viewport_is_rejected(double width, double height)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CanvasSceneBounds.FromViewport(width, height));
    }

    private sealed class TestResource : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
