using SemanticAgentBroker.Api.Features.ReAct.Services;

namespace SemanticAgentBroker.UnitTests.Api.ReAct;

[TestClass]
public class SemaphoreOwnershipStateTests
{
    [TestMethod]
    public void Constructor_InitializesInHeldState()
    {
        var state = new SemaphoreOwnershipState();
        Assert.IsTrue(state.IsHeld);
    }

    [TestMethod]
    public void TryRelease_FirstCallReturnsTrue_SubsequentCallsReturnFalse()
    {
        var state = new SemaphoreOwnershipState();

        // First release succeeds
        Assert.IsTrue(state.TryRelease());
        Assert.IsFalse(state.IsHeld);

        // Second release fails (already released)
        Assert.IsFalse(state.TryRelease());
        Assert.IsFalse(state.IsHeld);
    }

    [TestMethod]
    public void TryAcquire_TransitionsFromReleasedToHeld()
    {
        var state = new SemaphoreOwnershipState();
        state.TryRelease();
        Assert.IsFalse(state.IsHeld);

        // Acquire succeeds
        Assert.IsTrue(state.TryAcquire());
        Assert.IsTrue(state.IsHeld);

        // Second acquire fails (already held)
        Assert.IsFalse(state.TryAcquire());
        Assert.IsTrue(state.IsHeld);
    }

    [TestMethod]
    public void IsHeld_GetterAndSetter_WorkCorrectly()
    {
        var state = new SemaphoreOwnershipState();
        state.IsHeld = false;
        Assert.IsFalse(state.IsHeld);

        state.IsHeld = true;
        Assert.IsTrue(state.IsHeld);
    }

    [TestMethod]
    public async Task TryRelease_MultithreadedRace_ExactlyOneThreadSucceeds()
    {
        const int iterations = 1000;
        int doubleReleaseCount = 0;

        for (int i = 0; i < iterations; i++)
        {
            var state = new SemaphoreOwnershipState();
            int successCount = 0;

            var t1 = Task.Run(() =>
            {
                if (state.TryRelease())
                    Interlocked.Increment(ref successCount);
            });

            var t2 = Task.Run(() =>
            {
                if (state.TryRelease())
                    Interlocked.Increment(ref successCount);
            });

            var t3 = Task.Run(() =>
            {
                if (state.TryRelease())
                    Interlocked.Increment(ref successCount);
            });

            await Task.WhenAll(t1, t2, t3);

            if (successCount != 1)
            {
                doubleReleaseCount++;
            }
        }

        Assert.AreEqual(0, doubleReleaseCount, "Exactly one thread must succeed in releasing the semaphore.");
    }

    [TestMethod]
    public async Task TryAcquire_MultithreadedRace_ExactlyOneThreadSucceeds()
    {
        const int iterations = 1000;
        int doubleAcquireCount = 0;

        for (int i = 0; i < iterations; i++)
        {
            var state = new SemaphoreOwnershipState();
            state.TryRelease(); // Release initial hold
            int successCount = 0;

            var t1 = Task.Run(() =>
            {
                if (state.TryAcquire())
                    Interlocked.Increment(ref successCount);
            });

            var t2 = Task.Run(() =>
            {
                if (state.TryAcquire())
                    Interlocked.Increment(ref successCount);
            });

            var t3 = Task.Run(() =>
            {
                if (state.TryAcquire())
                    Interlocked.Increment(ref successCount);
            });

            await Task.WhenAll(t1, t2, t3);

            if (successCount != 1)
            {
                doubleAcquireCount++;
            }
        }

        Assert.AreEqual(0, doubleAcquireCount, "Exactly one thread must succeed in acquiring the semaphore.");
    }
}
