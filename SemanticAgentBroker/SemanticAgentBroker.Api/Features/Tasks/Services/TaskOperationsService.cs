using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SemanticAgentBroker.Api.Features.Tasks.Services;

// ── Plan Decision Types ────────────────────────────────────────────────────

public enum PlanAction
{
    Approve,
    Modify,
    Reject,
    AskQuestion
}

public record PlanDecision(PlanAction Action, string? Feedback = null, string? ModifiedPlan = null);

public record UserInterruption(string Message, string UserName);

/// <summary>
/// Manages per-task lifecycle controls: pause/resume, cancellation,
/// user interruption channels, step approval, and plan review gates.
/// Singleton service - state is in-memory and scoped to the process lifetime.
/// </summary>
public class TaskOperationsService
{
    // Per-task cancellation
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCts = new();

    // Per-task pause gate using TaskCompletionSource for race-free async waiting.
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _pauseGates = new();

    // Per-task user interruption channel
    private readonly ConcurrentDictionary<Guid, Channel<UserInterruption>> _interruptChannels = new();

    // Per-task step approval gate
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _approvalGates = new();

    // Per-task plan decision channel (Planning modes)
    private readonly ConcurrentDictionary<Guid, Channel<PlanDecision>> _planDecisionChannels = new();

    // Per-task completion gate for clean shutdown and cancellation synchronization
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _completionGates = new();

    /// <summary>Registers a new task and returns the CancellationToken for its execution.</summary>
    public CancellationToken RegisterTask(Guid taskId)
    {
        // Clean up any existing registration for this task to release prior resources and unblock any stale waiters.
        // NOTE: _pauseGates is deliberately NOT removed or signaled here, because a task may be paused
        // while still in the queue prior to RegisterTask being called.
        if (_activeCts.TryRemove(taskId, out var oldCts))
            oldCts.Dispose();
        if (_completionGates.TryRemove(taskId, out var oldCompGate))
            oldCompGate.TrySetResult(true);
        if (_interruptChannels.TryRemove(taskId, out var oldIntCh))
            oldIntCh.Writer.TryComplete();
        if (_approvalGates.TryRemove(taskId, out var oldAppGate))
            oldAppGate.TrySetCanceled();
        _stepRejectionFeedback.TryRemove(taskId, out _);
        if (_planDecisionChannels.TryRemove(taskId, out var oldPlanCh))
            oldPlanCh.Writer.TryComplete();
        _awaitingPlanDecision.TryRemove(taskId, out _);

        var cts = new CancellationTokenSource();
        _activeCts[taskId] = cts;
        _completionGates[taskId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Note: _pauseGates is not populated here. A missing gate means "Not Paused".
        _interruptChannels[taskId] = Channel.CreateUnbounded<UserInterruption>();
        _planDecisionChannels[taskId] = Channel.CreateUnbounded<PlanDecision>();
        return cts.Token;
    }

    /// <summary>Gets the cancellation token for an active task, or CancellationToken.None if not active.</summary>
    public CancellationToken GetTaskCancellationToken(Guid taskId)
    {
        return _activeCts.TryGetValue(taskId, out var cts) ? cts.Token : CancellationToken.None;
    }

    /// <summary>Unregisters a task, releasing all resources and signaling completion.</summary>
    public void UnregisterTask(Guid taskId)
    {
        // The CTS is not cancelled here - the task has already completed.
        // Just dispose it to release resources.
        if (_activeCts.TryRemove(taskId, out var cts))
        {
            cts.Dispose();
        }
        if (_completionGates.TryRemove(taskId, out var compGate))
            compGate.TrySetResult(true);
        if (_pauseGates.TryRemove(taskId, out var gate))
            gate.TrySetResult(true);
        if (_interruptChannels.TryRemove(taskId, out var ch))
            ch.Writer.TryComplete();
        if (_approvalGates.TryRemove(taskId, out var appGate))
            appGate.TrySetCanceled();
        _stepRejectionFeedback.TryRemove(taskId, out _);
        if (_planDecisionChannels.TryRemove(taskId, out var planCh))
            planCh.Writer.TryComplete();
        _awaitingPlanDecision.TryRemove(taskId, out _);
    }

    /// <summary>Waits asynchronously for an active task to finish execution or cancellation.</summary>
    public async Task<bool> WaitForTaskCompletionAsync(Guid taskId, TimeSpan timeout)
    {
        if (!_activeCts.ContainsKey(taskId))
            return true;

        if (_completionGates.TryGetValue(taskId, out var compGate))
        {
            var delayTask = Task.Delay(timeout);
            var completedTask = await Task.WhenAny(compGate.Task, delayTask);
            return completedTask == compGate.Task;
        }

        return true;
    }

    // ── Pause / Resume ─────────────────────────────────────────────────────

    public void PauseTask(Guid taskId)
    {
        // TryAdd safely ignores the call if already paused.
        // Can be called while task is active or still queued.
        _pauseGates.TryAdd(taskId, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public void ResumeTask(Guid taskId)
    {
        // If it was paused, removing it and setting Result allows execution to resume.
        if (_pauseGates.TryRemove(taskId, out var tcs))
            tcs.TrySetResult(true);
    }

    /// <summary>Blocks the calling task asynchronously if paused, until resumed or cancelled.</summary>
    public async Task WaitIfPausedAsync(Guid taskId, CancellationToken ct)
    {
        if (_pauseGates.TryGetValue(taskId, out var tcs))
        {
            try
            {
                // .WaitAsync allows cancellation without aborting the underlying shared TCS
                await tcs.Task.WaitAsync(ct);
            }
            catch (TimeoutException)
            {
                ct.ThrowIfCancellationRequested();
            }
            catch (TaskCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    // ── Cancellation ───────────────────────────────────────────────────────

    public void CancelTask(Guid taskId)
    {
        if (_activeCts.TryGetValue(taskId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Task already completed and CTS was disposed concurrently in UnregisterTask
            }
            catch (AggregateException)
            {
                // One or more cancellation callbacks threw an exception; continue cleanup
            }
        }
        // Also release pause gate so the loop can exit
        ResumeTask(taskId);
        // Also unblock any pending approval
        if (_approvalGates.TryRemove(taskId, out var tcs))
            tcs.TrySetCanceled();
        // Also unblock any pending plan decision
        if (_planDecisionChannels.TryRemove(taskId, out var planCh))
            planCh.Writer.TryComplete();
        _awaitingPlanDecision.TryRemove(taskId, out _);
    }

    // ── User Interruption ──────────────────────────────────────────────────

    public bool SendInterruption(Guid taskId, string message, string userName)
    {
        if (!IsTaskActive(taskId)) return false;
        if (_interruptChannels.TryGetValue(taskId, out var ch))
        {
            return ch.Writer.TryWrite(new UserInterruption(message, userName));
        }
        return false;
    }

    /// <summary>Non-blocking read of any pending user interruptions.</summary>
    public UserInterruption? TryReadInterruption(Guid taskId)
    {
        if (_interruptChannels.TryGetValue(taskId, out var ch))
        {
            if (ch.Reader.TryRead(out var msg))
                return msg;
        }
        return null;
    }

    // ── Step Approval ──────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<Guid, (string? Feedback, string? UserName)> _stepRejectionFeedback = new();

    /// <summary>Prepares an approval gate for the task before awaiting user decision.</summary>
    public void PrepareApprovalGate(Guid taskId)
    {
        _stepRejectionFeedback.TryRemove(taskId, out _);
        _approvalGates[taskId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Creates an approval gate and waits for user approval.</summary>
    public async Task<bool> WaitForApprovalAsync(Guid taskId, CancellationToken ct)
    {
        var tcs = _approvalGates.GetOrAdd(taskId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _approvalGates.TryRemove(taskId, out _);
        }
    }

    /// <summary>Checks whether a task is currently awaiting human step approval.</summary>
    public bool IsAwaitingApproval(Guid taskId) => _approvalGates.ContainsKey(taskId);

    /// <summary>Reads any feedback provided when rejecting a step.</summary>
    public (string? Feedback, string? UserName)? TryReadStepRejectionFeedback(Guid taskId)
    {
        if (_stepRejectionFeedback.TryRemove(taskId, out var item))
            return item;
        return null;
    }

    /// <summary>User approves the current step.</summary>
    public void ApproveStep(Guid taskId)
    {
        if (!IsTaskActive(taskId)) return;
        if (_approvalGates.TryGetValue(taskId, out var tcs))
        {
            tcs.TrySetResult(true);
        }
    }

    /// <summary>User rejects the current step with optional feedback.</summary>
    public void RejectStep(Guid taskId, string? feedback, string userName)
    {
        if (!IsTaskActive(taskId)) return;

        if (!string.IsNullOrEmpty(feedback))
            _stepRejectionFeedback[taskId] = (feedback, userName);

        if (_approvalGates.TryGetValue(taskId, out var tcs))
        {
            tcs.TrySetResult(false);
        }
    }

    private readonly ConcurrentDictionary<Guid, bool> _awaitingPlanDecision = new();

    /// <summary>Checks whether a task is currently awaiting human plan decision.</summary>
    public bool IsAwaitingPlanDecision(Guid taskId) => _awaitingPlanDecision.ContainsKey(taskId);

    /// <summary>Clears any pending plan decisions from previous review cycles.</summary>
    public void ClearPlanDecisions(Guid taskId)
    {
        if (_planDecisionChannels.TryGetValue(taskId, out var ch))
        {
            while (ch.Reader.TryRead(out _)) { }
        }
    }

    /// <summary>Waits for the user's plan decision from the channel.</summary>
    public async Task<PlanDecision> WaitForPlanDecisionAsync(Guid taskId, CancellationToken ct)
    {
        var ch = _planDecisionChannels.GetOrAdd(taskId, _ => Channel.CreateUnbounded<PlanDecision>());
        _awaitingPlanDecision[taskId] = true;
        try
        {
            return await ch.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            // Channel closed during task cancellation or unregistration - map to OperationCanceledException
            throw new OperationCanceledException(ct.IsCancellationRequested ? ct : new CancellationToken(true));
        }
        finally
        {
            _awaitingPlanDecision.TryRemove(taskId, out _);
        }
    }

    /// <summary>User approves the implementation plan.</summary>
    public void ApprovePlan(Guid taskId)
    {
        if (!IsTaskActive(taskId)) return;
        if (_planDecisionChannels.TryGetValue(taskId, out var ch))
            ch.Writer.TryWrite(new PlanDecision(PlanAction.Approve));
    }

    /// <summary>User modifies the implementation plan with direct markdown edits.</summary>
    public void ModifyPlan(Guid taskId, string modifiedPlanMarkdown)
    {
        if (!IsTaskActive(taskId)) return;
        if (_planDecisionChannels.TryGetValue(taskId, out var ch))
            ch.Writer.TryWrite(new PlanDecision(PlanAction.Modify, ModifiedPlan: modifiedPlanMarkdown));
    }

    /// <summary>User rejects the plan with feedback for AI revision.</summary>
    public void RejectPlan(Guid taskId, string feedback)
    {
        if (!IsTaskActive(taskId)) return;
        if (_planDecisionChannels.TryGetValue(taskId, out var ch))
            ch.Writer.TryWrite(new PlanDecision(PlanAction.Reject, Feedback: feedback));
    }

    /// <summary>User asks a question about the plan.</summary>
    public void AskPlanQuestion(Guid taskId, string question)
    {
        if (!IsTaskActive(taskId)) return;
        if (_planDecisionChannels.TryGetValue(taskId, out var ch))
            ch.Writer.TryWrite(new PlanDecision(PlanAction.AskQuestion, Feedback: question));
    }

    /// <summary>Checks if a task is currently registered and active.</summary>
    public bool IsTaskActive(Guid taskId) => _activeCts.ContainsKey(taskId);

    /// <summary>Checks if a task is currently paused.</summary>
    public Task<bool> IsPausedAsync(Guid taskId, CancellationToken ct) => Task.FromResult(_pauseGates.ContainsKey(taskId));
}
