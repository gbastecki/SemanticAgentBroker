namespace SemanticAgentBroker.Api.Features.ReAct.Services;

/// <summary>
/// Mutable wrapper for tracking semaphore ownership across async boundaries.
/// Shared between the caller (RunTaskAsync) and the engine so the finally block
/// only releases the semaphore if it is actually held.
/// Uses atomic operations to prevent check-then-act race conditions during cancellation / shutdown.
/// </summary>
public class SemaphoreOwnershipState
{
    private int _isHeld = 1; // 1 = held, 0 = released

    public bool IsHeld
    {
        get => Volatile.Read(ref _isHeld) == 1;
        set => Volatile.Write(ref _isHeld, value ? 1 : 0);
    }

    /// <summary>
    /// Atomically transitions ownership from held to released.
    /// Returns true if this call changed the state from held to released.
    /// </summary>
    public bool TryRelease()
    {
        return Interlocked.CompareExchange(ref _isHeld, 0, 1) == 1;
    }

    /// <summary>
    /// Atomically transitions ownership from released to held.
    /// Returns true if this call changed the state from released to held.
    /// </summary>
    public bool TryAcquire()
    {
        return Interlocked.CompareExchange(ref _isHeld, 1, 0) == 0;
    }
}
