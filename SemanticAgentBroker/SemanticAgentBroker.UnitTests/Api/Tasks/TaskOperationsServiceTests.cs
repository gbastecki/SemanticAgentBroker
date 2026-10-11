using SemanticAgentBroker.Api.Features.Tasks.Services;

namespace SemanticAgentBroker.UnitTests.Api.Tasks;

[TestClass]
public class TaskOperationsServiceTests
{
    private TaskOperationsService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _service = new TaskOperationsService();
    }

    [TestMethod]
    public void RegisterTask_CreatesActiveCtsAndToken()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // Act
        var token = _service.RegisterTask(taskId);

        // Assert
        Assert.IsTrue(_service.IsTaskActive(taskId));
        Assert.IsFalse(token.IsCancellationRequested);

        // Cleanup
        _service.UnregisterTask(taskId);
        Assert.IsFalse(_service.IsTaskActive(taskId));
    }

    [TestMethod]
    public async Task PauseAndResumeTask_ControlsExecutionFlow()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Act 1: Pause task
        _service.PauseTask(taskId);
        Assert.IsTrue(await _service.IsPausedAsync(taskId, CancellationToken.None));

        // Act 2: Background wait should unblock when resumed
        bool unblocked = false;
        var waitTask = Task.Run(async () =>
        {
            await _service.WaitIfPausedAsync(taskId, CancellationToken.None);
            unblocked = true;
        });

        Assert.IsFalse(unblocked);

        // Act 3: Resume task
        _service.ResumeTask(taskId);
        await waitTask;

        // Assert
        Assert.IsTrue(unblocked);
        Assert.IsFalse(await _service.IsPausedAsync(taskId, CancellationToken.None));

        // Cleanup
        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task PauseTask_WhenTaskNotYetRegistered_SetsPauseGateAndBlocksWaitIfPausedAsync()
    {
        // Arrange
        var taskId = Guid.NewGuid();

        // Act 1: Pause task while still queued (not yet registered)
        _service.PauseTask(taskId);

        // Assert 1: Gate should be active
        Assert.IsTrue(await _service.IsPausedAsync(taskId, CancellationToken.None));

        // Act 2: Register task later (when worker pulls it)
        _service.RegisterTask(taskId);
        Assert.IsTrue(await _service.IsPausedAsync(taskId, CancellationToken.None));

        // Act 3: WaitIfPausedAsync blocks until resumed
        bool unblocked = false;
        var waitTask = Task.Run(async () =>
        {
            await _service.WaitIfPausedAsync(taskId, CancellationToken.None);
            unblocked = true;
        });

        await Task.Delay(50);
        Assert.IsFalse(unblocked);

        // Act 4: Resume
        _service.ResumeTask(taskId);
        await waitTask;
        Assert.IsTrue(unblocked);
        Assert.IsFalse(await _service.IsPausedAsync(taskId, CancellationToken.None));

        // Cleanup
        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public void CancelTask_CancelsRegisteredCancellationToken()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var token = _service.RegisterTask(taskId);

        // Act
        _service.CancelTask(taskId);

        // Assert
        Assert.IsTrue(token.IsCancellationRequested);

        // Cleanup
        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task StepApproval_ApproveAndReject_ResolvesGateCorrectly()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act 1: Approve step
        var approveTask = Task.Run(async () =>
        {
            await Task.Delay(20);
            _service.ApproveStep(taskId);
        });

        var approved = await _service.WaitForApprovalAsync(taskId, cts.Token);
        await approveTask;
        Assert.IsTrue(approved);

        // Act 2: Reject step with feedback
        var rejectTask = Task.Run(async () =>
        {
            await Task.Delay(20);
            _service.RejectStep(taskId, "Needs different parameters", "admin");
        });

        var rejected = await _service.WaitForApprovalAsync(taskId, cts.Token);
        await rejectTask;
        Assert.IsFalse(rejected);

        var feedback = _service.TryReadStepRejectionFeedback(taskId);
        Assert.IsNotNull(feedback);
        Assert.AreEqual("Needs different parameters", feedback.Value.Feedback);
        Assert.AreEqual("admin", feedback.Value.UserName);

        // Cleanup
        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task PlanDecision_ApproveModifyReject_ResolvesDecision()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act 1: Approve Plan
        var approveTask = Task.Run(async () =>
        {
            await Task.Delay(20);
            _service.ApprovePlan(taskId);
        });
        var decision1 = await _service.WaitForPlanDecisionAsync(taskId, cts.Token);
        await approveTask;
        Assert.AreEqual(PlanAction.Approve, decision1.Action);

        // Act 2: Modify Plan
        var modifyTask = Task.Run(async () =>
        {
            await Task.Delay(20);
            _service.ModifyPlan(taskId, "Updated plan markdown");
        });
        var decision2 = await _service.WaitForPlanDecisionAsync(taskId, cts.Token);
        await modifyTask;
        Assert.AreEqual(PlanAction.Modify, decision2.Action);
        Assert.AreEqual("Updated plan markdown", decision2.ModifiedPlan);

        // Act 3: Reject Plan
        var rejectTask = Task.Run(async () =>
        {
            await Task.Delay(20);
            _service.RejectPlan(taskId, "Do not run this plan");
        });
        var decision3 = await _service.WaitForPlanDecisionAsync(taskId, cts.Token);
        await rejectTask;
        Assert.AreEqual(PlanAction.Reject, decision3.Action);
        Assert.AreEqual("Do not run this plan", decision3.Feedback);

        // Act 4: Question Plan
        var questionTask = Task.Run(async () =>
        {
            await Task.Delay(20);
            _service.AskPlanQuestion(taskId, "Why are you using Python?");
        });
        var decision4 = await _service.WaitForPlanDecisionAsync(taskId, cts.Token);
        await questionTask;
        Assert.AreEqual(PlanAction.AskQuestion, decision4.Action);
        Assert.AreEqual("Why are you using Python?", decision4.Feedback);

        // Cleanup
        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task WaitForPlanDecision_DrainsStalePreviousDecisions()
    {
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Pre-load stale decisions (simulating rapid clicks before new plan generation)
        _service.RejectPlan(taskId, "stale reject");
        _service.ModifyPlan(taskId, "stale modify");

        // Clear stale decisions before new plan review cycle
        _service.ClearPlanDecisions(taskId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Now start waiting for a fresh decision
        var delayTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            _service.ApprovePlan(taskId);
        });

        var decision = await _service.WaitForPlanDecisionAsync(taskId, cts.Token);
        await delayTask;

        // Must receive the fresh Approve decision, not the stale Reject/Modify
        Assert.AreEqual(PlanAction.Approve, decision.Action);

        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public void Interruption_MultipleSequentialMessages_ReadInFifoOrder()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Act
        _service.SendInterruption(taskId, "First message", "user1");
        _service.SendInterruption(taskId, "Second message", "user2");

        // Assert - Read 1
        var msg1 = _service.TryReadInterruption(taskId);
        Assert.IsNotNull(msg1);
        Assert.AreEqual("First message", msg1.Message);
        Assert.AreEqual("user1", msg1.UserName);

        // Assert - Read 2
        var msg2 = _service.TryReadInterruption(taskId);
        Assert.IsNotNull(msg2);
        Assert.AreEqual("Second message", msg2.Message);
        Assert.AreEqual("user2", msg2.UserName);

        // Assert - Read 3 (empty)
        var msg3 = _service.TryReadInterruption(taskId);
        Assert.IsNull(msg3);

        // Cleanup
        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task CancelTask_ConcurrentWithUnregisterTask_DoesNotThrowObjectDisposedException()
    {
        // Arrange - Stress test simulating high-concurrency race between UI cancellations and background unregistrations
        const int iterations = 500;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, iterations).Select(async _ =>
        {
            var taskId = Guid.NewGuid();
            var token = _service.RegisterTask(taskId);

            // Concurrently unregister on one thread and cancel on another
            var t1 = Task.Run(() =>
            {
                try
                {
                    _service.CancelTask(taskId);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            var t2 = Task.Run(() =>
            {
                try
                {
                    _service.UnregisterTask(taskId);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            await Task.WhenAll(t1, t2);
        });

        // Act
        await Task.WhenAll(tasks);

        // Assert - zero exceptions, particularly no ObjectDisposedException
        Assert.IsEmpty(exceptions, $"Encountered {exceptions.Count} exceptions: {string.Join(", ", exceptions.Select(e => e.GetType().Name + ": " + e.Message))}");
    }

    [TestMethod]
    public async Task WaitForTaskCompletionAsync_WhenTaskIsUnregistered_ReturnsTrue()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        var unregisterTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            _service.UnregisterTask(taskId);
        });

        // Act
        var completed = await _service.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(2));
        await unregisterTask;

        // Assert
        Assert.IsTrue(completed);
        Assert.IsFalse(_service.IsTaskActive(taskId));
    }

    [TestMethod]
    public async Task ApproveStep_WhenNoGatePrepared_DoesNotBypassSubsequentWaitForApprovalAsync()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Act 1: User or stray message calls ApproveStep when engine is NOT waiting
        _service.ApproveStep(taskId);

        // Later, engine reaches dangerous action and prepares gate
        _service.PrepareApprovalGate(taskId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = _service.WaitForApprovalAsync(taskId, cts.Token);

        // Assert 1: waitTask must NOT complete immediately with true from stale approval
        await Task.Delay(50);
        Assert.IsFalse(waitTask.IsCompleted, "WaitForApprovalAsync must NOT use stale pre-completed approval.");

        // Act 2: Genuine user approval arrives
        _service.ApproveStep(taskId);
        var result = await waitTask;

        // Assert 2
        Assert.IsTrue(result);

        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task WaitForPlanDecisionAsync_WhenDecisionArrivesBeforeWait_DoesNotDiscardDecision()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Engine prepares for review cycle before broadcasting
        _service.ClearPlanDecisions(taskId);

        // User responds very quickly (before WaitForPlanDecisionAsync is invoked)
        _service.ApprovePlan(taskId);

        // Act: Engine awaits plan decision
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var decision = await _service.WaitForPlanDecisionAsync(taskId, cts.Token);

        // Assert: Decision must be preserved and returned
        Assert.AreEqual(PlanAction.Approve, decision.Action);

        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public void PrepareApprovalGate_ClearsPriorStepRejectionFeedback()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // User rejected previous step with feedback
        _service.RejectStep(taskId, "Stale feedback from previous step", "admin");

        // Act: Engine prepares approval gate for next step
        _service.PrepareApprovalGate(taskId);

        // Assert: Prior step feedback must be cleared
        var feedback = _service.TryReadStepRejectionFeedback(taskId);
        Assert.IsNull(feedback, "PrepareApprovalGate must clear prior rejection feedback.");

        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public void UnregisterTask_ClearsStepRejectionFeedback()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        _service.RejectStep(taskId, "Aborted task feedback", "admin");

        // Act
        _service.UnregisterTask(taskId);

        // Assert
        var feedback = _service.TryReadStepRejectionFeedback(taskId);
        Assert.IsNull(feedback, "UnregisterTask must clean up step rejection feedback.");
    }

    [TestMethod]
    public async Task WaitForPlanDecisionAsync_WhenTaskCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Act
        var waitTask = _service.WaitForPlanDecisionAsync(taskId, CancellationToken.None);
        _service.CancelTask(taskId);

        // Assert: Must throw OperationCanceledException rather than unhandled ChannelClosedException
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waitTask);

        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public void IsAwaitingApproval_AccuratelyTracksApprovalGateLifecycle()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Assert 1: Initially false
        Assert.IsFalse(_service.IsAwaitingApproval(taskId));

        // Act 1: Gate prepared
        _service.PrepareApprovalGate(taskId);
        Assert.IsTrue(_service.IsAwaitingApproval(taskId));

        // Act 2: Approved
        _service.ApproveStep(taskId);
        // Note: approval gate is cleared upon WaitForApprovalAsync completing or unregister
        _service.UnregisterTask(taskId);
        Assert.IsFalse(_service.IsAwaitingApproval(taskId));
    }

    [TestMethod]
    public void IsAwaitingPlanDecision_AccuratelyTracksPlanDecisionChannelLifecycle()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Assert 1: Initially false
        Assert.IsFalse(_service.IsAwaitingPlanDecision(taskId));

        // Act 1: Channel created on wait
        using var cts = new CancellationTokenSource();
        var waitTask = _service.WaitForPlanDecisionAsync(taskId, cts.Token);
        Assert.IsTrue(_service.IsAwaitingPlanDecision(taskId));

        // Act 2: Decision provided
        _service.ApprovePlan(taskId);
        _service.UnregisterTask(taskId);
        Assert.IsFalse(_service.IsAwaitingPlanDecision(taskId));
    }

    [TestMethod]
    public async Task UnregisterTask_WhenApprovalGatePending_CancelsApprovalGateWaiters()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Start waiting for approval without a token that cancels automatically
        var waitTask = _service.WaitForApprovalAsync(taskId, CancellationToken.None);

        // Act: UnregisterTask should cancel the approval gate rather than leaving it hanging
        _service.UnregisterTask(taskId);

        // Assert: waitTask must be canceled promptly
        var delayTask = Task.Delay(2000);
        var completedTask = await Task.WhenAny(waitTask, delayTask);
        Assert.AreEqual(waitTask, completedTask, "WaitForApprovalAsync should complete promptly when task is unregistered.");
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await waitTask);
    }

    [TestMethod]
    public async Task RegisterTask_WhenTaskAlreadyRegistered_CleansUpPreviousResourcesAndSignalsOldCompletionGate()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _service.RegisterTask(taskId);

        // Start waiting on the first completion gate
        var waitCompletionTask = _service.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5));

        // Act: Re-register the same taskId
        _service.RegisterTask(taskId);

        // Assert: The previous completion gate must have been signaled, completing immediately instead of 5s timeout
        var delayTask = Task.Delay(1000);
        var finished = await Task.WhenAny(waitCompletionTask, delayTask);
        Assert.AreEqual(waitCompletionTask, finished, "Previous completion gate must complete immediately upon re-registration.");
        Assert.IsTrue(await waitCompletionTask);

        // Cleanup
        _service.UnregisterTask(taskId);
    }

    [TestMethod]
    public async Task CancelTask_WhenCancellationCallbackThrows_StillReleasesPauseApprovalAndPlanDecisionGates()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var token = _service.RegisterTask(taskId);
        _service.PauseTask(taskId);
        _service.PrepareApprovalGate(taskId);
        using var planCts = new CancellationTokenSource();
        var planWaitTask = _service.WaitForPlanDecisionAsync(taskId, planCts.Token);

        // Register a callback that throws unhandled exception upon cancellation
        token.Register(() => throw new InvalidOperationException("Simulated callback failure"));

        // Act: CancelTask must not let AggregateException prevent pause/approval/plan gates from releasing
        _service.CancelTask(taskId);

        // Assert:
        // 1. Pause gate released
        Assert.IsFalse(await _service.IsPausedAsync(taskId, CancellationToken.None));

        // 2. Approval gate canceled/cleared
        Assert.IsFalse(_service.IsAwaitingApproval(taskId));

        // 3. Plan decision channel closed/canceled
        Assert.IsFalse(_service.IsAwaitingPlanDecision(taskId));

        // Cleanup
        _service.UnregisterTask(taskId);
    }
}

