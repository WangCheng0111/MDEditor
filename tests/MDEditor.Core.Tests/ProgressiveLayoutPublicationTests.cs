using MDEditor.Typesetting.Layout;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ProgressiveLayoutPublicationTests
{
    [TestMethod]
    public void Stale_edits_can_advance_the_visible_version_while_latest_continues()
    {
        Assert.IsTrue(ProgressiveLayoutPublication.CanPresentIntermediate(103, 108, 101,
            720, 720, 100, 100, true));
        Assert.IsTrue(ProgressiveLayoutPublication.CanPresentIntermediate(106, 108, 103,
            720, 720, 100, 100, true));
    }

    [TestMethod]
    public void Never_publish_a_regression_or_a_wrong_viewport()
    {
        Assert.IsFalse(ProgressiveLayoutPublication.CanPresentIntermediate(103, 108, 103,
            720, 720, 100, 100, true));
        Assert.IsFalse(ProgressiveLayoutPublication.CanPresentIntermediate(108, 108, 103,
            720, 720, 100, 100, true));
        Assert.IsFalse(ProgressiveLayoutPublication.CanPresentIntermediate(103, 108, 101,
            720, 721, 100, 100, true));
        Assert.IsFalse(ProgressiveLayoutPublication.CanPresentIntermediate(103, 108, 101,
            720, 720, 100, 110, true));
        Assert.IsFalse(ProgressiveLayoutPublication.CanPresentIntermediate(103, 108, 101,
            720, 720, 100, 100, false));
    }

    [TestMethod]
    public void A_worker_that_caught_up_to_the_latest_edit_can_complete_without_a_second_solve()
    {
        Assert.IsTrue(ProgressiveLayoutPublication.CanCompleteCurrent(108, 108,
            720, 720, 100, 100, true));
        Assert.IsFalse(ProgressiveLayoutPublication.CanCompleteCurrent(107, 108,
            720, 720, 100, 100, true));
        Assert.IsFalse(ProgressiveLayoutPublication.CanCompleteCurrent(108, 108,
            720, 721, 100, 100, true));
        Assert.IsFalse(ProgressiveLayoutPublication.CanCompleteCurrent(108, 108,
            720, 720, 100, 100, false));
    }
}
