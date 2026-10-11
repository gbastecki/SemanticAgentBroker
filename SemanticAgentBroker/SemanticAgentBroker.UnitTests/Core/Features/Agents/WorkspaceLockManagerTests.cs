using SemanticAgentBroker.Core.Features.Agents.Implementations;

namespace SemanticAgentBroker.UnitTests.Core.Features.Agents;

[TestClass]
public class WorkspaceLockManagerTests
{
    [TestMethod]
    public async Task AcquireLockAsync_AllowsMutualExclusionOnSameKey()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();
        var lock1Acquired = new TaskCompletionSource();
        bool secondLockAcquired = false;

        // Act 1: Acquire first lock
        var lock1 = await lockManager.AcquireLockAsync(sessionId, "file.txt");
        lock1Acquired.SetResult();

        // Try to acquire lock2 in background with timeout
        var task = Task.Run(async () =>
        {
            await lock1Acquired.Task;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                using var lock2 = await lockManager.AcquireLockAsync(sessionId, "file.txt", cts.Token);
                secondLockAcquired = true;
            }
            catch (OperationCanceledException)
            {
                secondLockAcquired = false;
            }
        });

        await task;
        Assert.IsFalse(secondLockAcquired, "Second lock should not be acquired while first lock is held");

        // Act 2: Release first lock and acquire again
        lock1.Dispose();

        using (var lock3 = await lockManager.AcquireLockAsync(sessionId, "file.txt"))
        {
            Assert.IsNotNull(lock3);
        }
    }

    [TestMethod]
    public async Task AcquireLockAsync_DifferentSessionsDoNotBlockEachOther()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        // Act & Assert
        using var lock1 = await lockManager.AcquireLockAsync(session1, "file.txt");
        using var lock2 = await lockManager.AcquireLockAsync(session2, "file.txt");

        Assert.IsNotNull(lock1);
        Assert.IsNotNull(lock2);
    }

    [TestMethod]
    public async Task CleanupSession_CleansUpAllLocksForSession()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        // Acquire and release locks for both sessions
        (await lockManager.AcquireLockAsync(session1, "a.txt")).Dispose();
        (await lockManager.AcquireLockAsync(session1, "b.txt")).Dispose();
        (await lockManager.AcquireLockAsync(session2, "c.txt")).Dispose();

        // Act
        lockManager.CleanupSession(session1);

        // Assert: session2 lock still works cleanly
        using var lock2 = await lockManager.AcquireLockAsync(session2, "c.txt");
        Assert.IsNotNull(lock2);

        // session1 lock should be acquirable immediately with a fresh semaphore
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var lock1New = await lockManager.AcquireLockAsync(session1, "a.txt", cts.Token);
        Assert.IsNotNull(lock1New);
    }

    [TestMethod]
    public async Task AcquireLockAsync_SurvivesCleanupDuringAcquire()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        var lock1 = await lockManager.AcquireLockAsync(sessionId, "file.txt");

        var acquireStarted = new TaskCompletionSource();
        var acquireTask = Task.Run(async () =>
        {
            acquireStarted.SetResult();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var lock2 = await lockManager.AcquireLockAsync(sessionId, "file.txt", cts.Token);
            return true;
        });

        await acquireStarted.Task;
        await Task.Delay(50);

        // Act: Release and cleanup
        lock1.Dispose();
        lockManager.CleanupSession(sessionId);

        // Assert: background acquire should succeed gracefully via retry
        var acquired = await acquireTask;
        Assert.IsTrue(acquired);
    }

    [TestMethod]
    public async Task AcquireLockAsync_WholeWorkspaceLock_BlocksIndividualFileLocks()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // Act 1: Acquire whole-workspace lock (fileName = null)
        var wsLock = await lockManager.AcquireLockAsync(sessionId, null);

        // Act 2: Try to acquire a file lock while workspace lock is held
        bool fileLockAcquired = false;
        var fileLockTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                using var fileLock = await lockManager.AcquireLockAsync(sessionId, "data.csv", cts.Token);
                fileLockAcquired = true;
            }
            catch (OperationCanceledException)
            {
                fileLockAcquired = false;
            }
        });

        await fileLockTask;

        // Assert: File lock was blocked
        Assert.IsFalse(fileLockAcquired, "File lock should be blocked when whole-workspace lock is held");

        // Release workspace lock
        wsLock.Dispose();

        // Now file lock should succeed
        using var fileLock2 = await lockManager.AcquireLockAsync(sessionId, "data.csv");
        Assert.IsNotNull(fileLock2);
    }

    [TestMethod]
    public async Task AcquireLockAsync_IndividualFileLocks_BlockWholeWorkspaceLock()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // Act 1: Acquire file lock
        var fileLock = await lockManager.AcquireLockAsync(sessionId, "report.json");

        // Act 2: Try to acquire whole-workspace lock while file lock is held
        bool wsLockAcquired = false;
        var wsLockTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                using var wsLock = await lockManager.AcquireLockAsync(sessionId, null, cts.Token);
                wsLockAcquired = true;
            }
            catch (OperationCanceledException)
            {
                wsLockAcquired = false;
            }
        });

        await wsLockTask;

        // Assert: Workspace lock was blocked
        Assert.IsFalse(wsLockAcquired, "Whole-workspace lock should be blocked when an active file lock exists");

        // Release file lock
        fileLock.Dispose();

        // Now workspace lock should succeed
        using var wsLock2 = await lockManager.AcquireLockAsync(sessionId, null);
        Assert.IsNotNull(wsLock2);
    }

    [TestMethod]
    public async Task AcquireLockAsync_MultipleDifferentFiles_CanBeLockedConcurrently()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // Act: Acquire locks for two different files in the same session simultaneously
        using var lockA = await lockManager.AcquireLockAsync(sessionId, "fileA.txt");
        using var lockB = await lockManager.AcquireLockAsync(sessionId, "fileB.txt");

        // Assert: Both locks are held simultaneously
        Assert.IsNotNull(lockA);
        Assert.IsNotNull(lockB);
    }

    [TestMethod]
    public async Task AcquireLockAsync_CancelledWhileWaiting_CleansUpActiveFileLocksWithoutCorruptingState()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // 1. Thread 1 acquires lock on file1.txt
        var lock1 = await lockManager.AcquireLockAsync(sessionId, "file1.txt");

        // 2. Thread 2 attempts to acquire lock on file1.txt with a cancellation token
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        bool threwCancellation = false;
        try
        {
            await lockManager.AcquireLockAsync(sessionId, "file1.txt", cts.Token);
        }
        catch (OperationCanceledException)
        {
            threwCancellation = true;
        }

        Assert.IsTrue(threwCancellation, "Cancelled lock request should throw OperationCanceledException");

        // 3. Thread 1 releases lock
        lock1.Dispose();

        // 4. Now a whole-workspace lock should succeed cleanly (verifying ActiveFileLocks counter is 0 and no semaphore was leaked)
        using var wsLock = await lockManager.AcquireLockAsync(sessionId, null);
        Assert.IsNotNull(wsLock);

        // 5. Subsequent file lock acquisition should also work normally
        wsLock.Dispose();
        using var lockAgain = await lockManager.AcquireLockAsync(sessionId, "file1.txt");
        Assert.IsNotNull(lockAgain);
    }

    [TestMethod]
    public async Task AcquireLockAsync_CaseAndWhitespaceNormalization_EnforcesMutualExclusionOnSameFile()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // Act 1: Acquire lock with whitespace and upper case
        var lock1 = await lockManager.AcquireLockAsync(sessionId, "  SubFolder/MyFile.TXT  ");

        // Act 2: Attempt to acquire lock with trimmed lowercase version
        bool lock2Acquired = false;
        var task = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                using var lock2 = await lockManager.AcquireLockAsync(sessionId, "subfolder/myfile.txt", cts.Token);
                lock2Acquired = true;
            }
            catch (OperationCanceledException)
            {
                lock2Acquired = false;
            }
        });

        await task;

        // Assert - Normalized lock names must point to same file lock entry and block
        Assert.IsFalse(lock2Acquired, "Normalized file paths should target the same mutex.");

        lock1.Dispose();

        using var lock3 = await lockManager.AcquireLockAsync(sessionId, "subfolder/myfile.txt");
        Assert.IsNotNull(lock3);
    }

    [TestMethod]
    public async Task AcquireFileLock_WhileWorkspaceLockAwaitingDrain_DoesNotDeadlock()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // 1. Thread A acquires lock on file1.txt
        var file1Lock = await lockManager.AcquireLockAsync(sessionId, "file1.txt");

        // 2. Thread B requests workspace lock (null). It acquires WorkspaceSemaphore and waits for file locks to drain
        var wsLockStarted = new TaskCompletionSource();
        var wsLockTask = Task.Run(async () =>
        {
            wsLockStarted.SetResult();
            using var wsLock = await lockManager.AcquireLockAsync(sessionId, null);
        });

        await wsLockStarted.Task;
        await Task.Delay(50); // Give Thread B time to wait on file locks

        // 3. Thread A (or any thread) acquires file2.txt.
        // Buggy implementation deadlocks because WorkspaceSemaphore is held by Thread B
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var file2Lock = await lockManager.AcquireLockAsync(sessionId, "file2.txt", cts.Token);

        Assert.IsNotNull(file2Lock);

        file1Lock.Dispose();
        file2Lock.Dispose();
        await wsLockTask;
    }

    [TestMethod]
    public async Task WorkspaceLock_ConcurrentFileAcquire_DuringWorkspaceAcquire_IsBlocked()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // Act 1: Thread A acquires whole workspace lock (with 0 initial file locks)
        var wsLock = await lockManager.AcquireLockAsync(sessionId, null);

        // Act 2: Thread B attempts to acquire a file lock
        bool fileLockAcquired = false;
        var attemptTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                using var fileLock = await lockManager.AcquireLockAsync(sessionId, "file.txt", cts.Token);
                fileLockAcquired = true;
            }
            catch (OperationCanceledException)
            {
                fileLockAcquired = false;
            }
        });

        await attemptTask;

        // Assert: Thread B must be blocked while Thread A holds workspace lock
        Assert.IsFalse(fileLockAcquired, "File lock must not be acquired while whole workspace is locked");

        // Release workspace lock
        wsLock.Dispose();

        // Now file lock can be acquired
        using var postLock = await lockManager.AcquireLockAsync(sessionId, "file.txt");
        Assert.IsNotNull(postLock);
    }

    [TestMethod]
    public async Task WorkspaceLock_ActiveFileLockDrain_BlocksSubsequentFileLockUntilWorkspaceReleased()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // 1. Thread A acquires file1.txt
        var file1Lock = await lockManager.AcquireLockAsync(sessionId, "file1.txt");

        // 2. Thread B requests workspace lock
        var wsAcquired = false;
        var wsReadyToRelease = new TaskCompletionSource();
        var wsTask = Task.Run(async () =>
        {
            using var wsLock = await lockManager.AcquireLockAsync(sessionId, null);
            wsAcquired = true;
            await wsReadyToRelease.Task;
        });

        await Task.Delay(50); // Ensure Thread B is waiting for file locks to drain

        // 3. Thread A releases file1.txt - this unblocks Thread B
        file1Lock.Dispose();

        // Wait until Thread B acquires workspace lock
        for (int i = 0; i < 50 && !wsAcquired; i++)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(wsAcquired, "Workspace lock should have been acquired after file lock released");

        // 4. Thread C tries to acquire file2.txt while Thread B is holding workspace lock
        bool file2Acquired = false;
        var file2Task = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                using var file2Lock = await lockManager.AcquireLockAsync(sessionId, "file2.txt", cts.Token);
                file2Acquired = true;
            }
            catch (OperationCanceledException)
            {
                file2Acquired = false;
            }
        });

        await file2Task;
        Assert.IsFalse(file2Acquired, "File lock must be blocked while workspace lock is held");

        // Clean up
        wsReadyToRelease.SetResult();
        await wsTask;
    }

    [TestMethod]
    public async Task CancelledFileLock_DuringWorkspaceLockWait_MaintainsMutualExclusion()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // 1. Thread A holds file1.txt
        var file1Lock = await lockManager.AcquireLockAsync(sessionId, "file1.txt");

        // 2. Thread B attempts to acquire file1.txt as well, but with a CancellationToken that cancels quickly
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var threadBCancelled = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            try
            {
                using var bLock = await lockManager.AcquireLockAsync(sessionId, "file1.txt", cts.Token);
            }
            catch (OperationCanceledException)
            {
                threadBCancelled.SetResult();
            }
        });
        await threadBCancelled.Task;

        // 3. Thread C requests workspace lock while Thread A still holds file1.txt
        var wsAcquired = false;
        var wsReleaseTcs = new TaskCompletionSource();
        var wsTask = Task.Run(async () =>
        {
            using var wsLock = await lockManager.AcquireLockAsync(sessionId, null);
            wsAcquired = true;
            await wsReleaseTcs.Task;
        });

        // 4. Thread D tries to acquire file1.txt with cancellation while Thread C is waiting for workspace lock
        var ctsD = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var threadDCancelled = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            try
            {
                using var dLock = await lockManager.AcquireLockAsync(sessionId, "file1.txt", ctsD.Token);
            }
            catch (OperationCanceledException)
            {
                threadDCancelled.SetResult();
            }
        });
        await threadDCancelled.Task;

        // 5. Now Thread A releases file1.txt
        file1Lock.Dispose();

        for (int i = 0; i < 50 && !wsAcquired; i++)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(wsAcquired, "Workspace lock should be acquired");

        // 6. While Thread C holds workspace lock, verify a new file lock is blocked
        bool file2Acquired = false;
        var file2Task = Task.Run(async () =>
        {
            using var fileCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                using var file2Lock = await lockManager.AcquireLockAsync(sessionId, "file2.txt", fileCts.Token);
                file2Acquired = true;
            }
            catch (OperationCanceledException)
            {
                file2Acquired = false;
            }
        });

        await file2Task;
        Assert.IsFalse(file2Acquired, "File lock must not be acquired while workspace lock is active");

        // Cleanup
        wsReleaseTcs.SetResult();
        await wsTask;
    }

    [TestMethod]
    public async Task AcquireLockAsync_NormalizesSlashes_EnsuresMutualExclusionAcrossSeparatorStyles()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // 1. Acquire lock using POSIX forward slash
        var lockPosix = await lockManager.AcquireLockAsync(sessionId, "subfolder/data.txt");

        // 2. Attempt to acquire lock using Windows backslash with timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        bool backslashAcquired = false;
        try
        {
            using var lockWin = await lockManager.AcquireLockAsync(sessionId, "subfolder\\data.txt", cts.Token);
            backslashAcquired = true;
        }
        catch (OperationCanceledException)
        {
            backslashAcquired = false;
        }

        // Assert - forward slash and backslash targeting same path must be mutually exclusive
        Assert.IsFalse(backslashAcquired, "Acquisition with backslash must block when forward slash lock is held");

        // 3. Release first lock and ensure backslash acquisition now succeeds
        lockPosix.Dispose();

        using var lockWinAfter = await lockManager.AcquireLockAsync(sessionId, "subfolder\\data.txt");
        Assert.IsNotNull(lockWinAfter);
    }

    [TestMethod]
    public async Task CleanupSession_WhileAcquireLockInProgress_DoesNotThrowObjectDisposed()
    {
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var task1 = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    using var lk = await lockManager.AcquireLockAsync(sessionId, "test.txt", cts.Token);
                    await Task.Delay(1);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        var task2 = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    using var lk = await lockManager.AcquireLockAsync(sessionId, null, cts.Token);
                    await Task.Delay(1);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        var task3 = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                lockManager.CleanupSession(sessionId);
                await Task.Delay(1);
            }
        });

        await Task.WhenAll(task1, task2, task3);

        foreach (var ex in exceptions)
        {
            Assert.IsFalse(ex is ObjectDisposedException, $"Encountered ObjectDisposedException: {ex.Message}");
        }
    }

    [TestMethod]
    public async Task CleanupSession_WhileLockIsHeld_DeferredCleanupDisposesAfterLastRelease()
    {
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        var lock1 = await lockManager.AcquireLockAsync(sessionId, "file1.txt");
        lockManager.CleanupSession(sessionId);

        // Disposing the lock when CleanupRequested is true should safely dispose and remove the session
        lock1.Dispose();

        // New acquisition after all locks released should work cleanly with fresh session
        using var lock2 = await lockManager.AcquireLockAsync(sessionId, "file1.txt");
        Assert.IsNotNull(lock2);
    }

    [TestMethod]
    public async Task AcquireLockAsync_WorkspaceLock_CancelledRightAfterSemaphoreAcquired_ReleasesWorkspaceSemaphore()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // 1. Thread 1 holds workspace lock so Thread 2 queues on WorkspaceSemaphore
        var lock1 = await lockManager.AcquireLockAsync(sessionId, null);

        using var cts = new CancellationTokenSource();
        var thread2Started = new TaskCompletionSource();
        var thread2Task = Task.Run(async () =>
        {
            thread2Started.SetResult();
            await lockManager.AcquireLockAsync(sessionId, null, cts.Token);
        });

        await thread2Started.Task;
        await Task.Delay(50); // Thread 2 is now awaiting sessionLock.WorkspaceSemaphore

        // 2. Concurrently cancel cts and release lock1
        cts.Cancel();
        lock1.Dispose();

        // Thread 2 must throw OperationCanceledException
        bool thread2Canceled = false;
        try
        {
            await thread2Task;
        }
        catch (OperationCanceledException)
        {
            thread2Canceled = true;
        }
        Assert.IsTrue(thread2Canceled, "Thread 2 must throw OperationCanceledException when cancelled.");

        // 3. Thread 3 must be able to acquire workspace lock without deadlocking due to leaked semaphore
        using var cts3 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var lock3 = await lockManager.AcquireLockAsync(sessionId, null, cts3.Token);
        Assert.IsNotNull(lock3, "WorkspaceSemaphore must be released when acquisition is cancelled so subsequent locks do not deadlock.");
    }

    [TestMethod]
    public async Task AcquireLockAsync_FileLock_CancelledRightAfterSemaphoreAcquired_ReleasesFileSemaphore()
    {
        // Arrange
        var lockManager = new WorkspaceLockManager();
        var sessionId = Guid.NewGuid();

        // 1. Thread 1 holds file1.txt so Thread 2 queues on fileEntry.Semaphore
        var file1Lock = await lockManager.AcquireLockAsync(sessionId, "file1.txt");

        using var cts = new CancellationTokenSource();
        var thread2Started = new TaskCompletionSource();
        var thread2Task = Task.Run(async () =>
        {
            thread2Started.SetResult();
            await lockManager.AcquireLockAsync(sessionId, "file1.txt", cts.Token);
        });

        await thread2Started.Task;
        await Task.Delay(50); // Thread 2 is now awaiting fileEntry.Semaphore

        // 2. Concurrently cancel cts and release file1Lock
        cts.Cancel();
        file1Lock.Dispose();

        // Thread 2 must throw OperationCanceledException
        bool thread2Canceled = false;
        try
        {
            await thread2Task;
        }
        catch (OperationCanceledException)
        {
            thread2Canceled = true;
        }
        Assert.IsTrue(thread2Canceled, "Thread 2 must throw OperationCanceledException when cancelled.");

        // 3. Thread 3 must be able to acquire file1.txt without deadlocking due to leaked semaphore
        using var cts3 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var lock3 = await lockManager.AcquireLockAsync(sessionId, "file1.txt", cts3.Token);
        Assert.IsNotNull(lock3, "fileEntry.Semaphore must be released when acquisition is cancelled so subsequent file locks do not deadlock.");
    }
}


