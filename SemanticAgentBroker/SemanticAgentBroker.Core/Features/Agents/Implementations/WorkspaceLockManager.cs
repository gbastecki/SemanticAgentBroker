using System.Collections.Concurrent;

namespace SemanticAgentBroker.Core.Features.Agents.Implementations;

/// <summary>
/// Thread-safe manager for session-scoped whole-workspace and per-file locks.
/// </summary>
public class WorkspaceLockManager : IWorkspaceLockManager
{
    private sealed class SessionLock
    {
        public readonly SemaphoreSlim WorkspaceSemaphore = new(1, 1);
        public readonly ConcurrentDictionary<string, FileLockEntry> FileLocks = new(StringComparer.OrdinalIgnoreCase);
        public int ActiveFileLocks;
        public TaskCompletionSource? AllFileLocksReleasedTcs;
        public TaskCompletionSource? WorkspaceLockReleasedTcs;
        public bool IsWorkspaceLocked;
        public int RefCount = 1;
        public bool CleanupRequested;
    }

    private sealed class FileLockEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount = 1;
    }

    private readonly ConcurrentDictionary<Guid, SessionLock> _sessions = new();
    private readonly object _syncRoot = new();

    /// <summary>
    /// Acquires an exclusive lock on the entire workspace (if fileName is null/empty) or a specific file.
    /// </summary>
    /// <param name="sessionId">The task/session identifier.</param>
    /// <param name="fileName">Optional file name to lock exclusively within the session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="IDisposable"/> token that releases the lock upon disposal.</returns>
    public async Task<IDisposable> AcquireLockAsync(Guid sessionId, string? fileName = null, CancellationToken ct = default)
    {
        SessionLock sessionLock;
        lock (_syncRoot)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                existing.RefCount++;
                sessionLock = existing;
            }
            else
            {
                sessionLock = new SessionLock();
                _sessions[sessionId] = sessionLock;
            }
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            // Whole-workspace exclusive lock
            bool wsSemaphoreAcquired = false;
            try
            {
                await sessionLock.WorkspaceSemaphore.WaitAsync(ct);
                wsSemaphoreAcquired = true;
                ct.ThrowIfCancellationRequested();

                // Wait until all currently active file locks for this session are released
                Task? waitFilesTask = null;
                lock (_syncRoot)
                {
                    if (sessionLock.ActiveFileLocks > 0)
                    {
                        sessionLock.AllFileLocksReleasedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        waitFilesTask = sessionLock.AllFileLocksReleasedTcs.Task;
                    }
                    else
                    {
                        sessionLock.IsWorkspaceLocked = true;
                        sessionLock.WorkspaceLockReleasedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                if (waitFilesTask != null)
                {
                    try
                    {
                        await waitFilesTask.WaitAsync(ct);
                    }
                    catch
                    {
                        lock (_syncRoot)
                        {
                            sessionLock.IsWorkspaceLocked = false;
                            sessionLock.WorkspaceLockReleasedTcs?.TrySetResult();
                            sessionLock.WorkspaceLockReleasedTcs = null;
                            sessionLock.AllFileLocksReleasedTcs = null;
                        }
                        sessionLock.WorkspaceSemaphore.Release();
                        wsSemaphoreAcquired = false;
                        throw;
                    }
                }

                lock (_syncRoot)
                {
                    sessionLock.IsWorkspaceLocked = true;
                    sessionLock.WorkspaceLockReleasedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                ct.ThrowIfCancellationRequested();

                return new WorkspaceReleaser(this, sessionId, sessionLock);
            }
            catch (Exception ex)
            {
                if (wsSemaphoreAcquired)
                {
                    lock (_syncRoot)
                    {
                        sessionLock.IsWorkspaceLocked = false;
                        sessionLock.WorkspaceLockReleasedTcs?.TrySetResult();
                        sessionLock.WorkspaceLockReleasedTcs = null;
                        sessionLock.AllFileLocksReleasedTcs = null;
                    }
                    try
                    {
                        sessionLock.WorkspaceSemaphore.Release();
                    }
                    catch (ObjectDisposedException) { }
                }

                ReleaseSession(sessionId, sessionLock, acquired: false);
                if (ex is ObjectDisposedException && sessionLock.CleanupRequested)
                {
                    throw new OperationCanceledException("Session was cleaned up while acquiring lock.", ex, ct);
                }
                throw;
            }
        }
        else
        {
            // Per-file lock within session
            var normalizedName = fileName.Trim().Replace('\\', '/').ToLowerInvariant();
            FileLockEntry fileEntry;
            lock (_syncRoot)
            {
                if (sessionLock.FileLocks.TryGetValue(normalizedName, out var existingFile))
                {
                    existingFile.RefCount++;
                    fileEntry = existingFile;
                }
                else
                {
                    fileEntry = new FileLockEntry();
                    sessionLock.FileLocks[normalizedName] = fileEntry;
                }
            }

            bool activeCountIncremented = false;
            bool fileSemaphoreAcquired = false;
            try
            {
                // Wait if whole workspace is currently locked
                while (true)
                {
                    Task? waitWsTask = null;
                    lock (_syncRoot)
                    {
                        if (sessionLock.IsWorkspaceLocked && sessionLock.WorkspaceLockReleasedTcs != null)
                        {
                            waitWsTask = sessionLock.WorkspaceLockReleasedTcs.Task;
                        }
                        else
                        {
                            sessionLock.ActiveFileLocks++;
                            activeCountIncremented = true;
                            break;
                        }
                    }

                    if (waitWsTask != null)
                    {
                        await waitWsTask.WaitAsync(ct);
                    }
                }

                // Then acquire the specific file semaphore
                await fileEntry.Semaphore.WaitAsync(ct);
                fileSemaphoreAcquired = true;
                ct.ThrowIfCancellationRequested();
                return new FileReleaser(this, sessionId, sessionLock, normalizedName, fileEntry);
            }
            catch (Exception ex)
            {
                if (fileSemaphoreAcquired)
                {
                    try
                    {
                        fileEntry.Semaphore.Release();
                    }
                    catch (ObjectDisposedException) { }
                }

                if (activeCountIncremented)
                {
                    lock (_syncRoot)
                    {
                        sessionLock.ActiveFileLocks--;
                        if (sessionLock.ActiveFileLocks <= 0)
                        {
                            sessionLock.ActiveFileLocks = 0;
                            if (sessionLock.AllFileLocksReleasedTcs != null)
                            {
                                sessionLock.IsWorkspaceLocked = true;
                                sessionLock.WorkspaceLockReleasedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                sessionLock.AllFileLocksReleasedTcs.TrySetResult();
                                sessionLock.AllFileLocksReleasedTcs = null;
                            }
                        }
                    }
                }
                ReleaseFileEntry(sessionId, sessionLock, normalizedName, fileEntry, acquired: false);
                if (ex is ObjectDisposedException && sessionLock.CleanupRequested)
                {
                    throw new OperationCanceledException("Session was cleaned up while acquiring lock.", ex, ct);
                }
                throw;
            }
        }
    }

    private void ReleaseSession(Guid sessionId, SessionLock session, bool acquired)
    {
        if (acquired)
        {
            lock (_syncRoot)
            {
                session.IsWorkspaceLocked = false;
                session.WorkspaceLockReleasedTcs?.TrySetResult();
                session.WorkspaceLockReleasedTcs = null;
            }

            try
            {
                session.WorkspaceSemaphore.Release();
            }
            catch (ObjectDisposedException) { }
        }

        lock (_syncRoot)
        {
            session.RefCount--;
            if (session.RefCount <= 0)
            {
                if (session.ActiveFileLocks == 0 && (session.FileLocks.IsEmpty || session.CleanupRequested))
                {
                    if (_sessions.TryGetValue(sessionId, out var cur) && ReferenceEquals(cur, session))
                    {
                        _sessions.TryRemove(sessionId, out _);
                    }
                    DisposeSessionResources(session);
                }
            }
        }
    }

    private void ReleaseFileEntry(Guid sessionId, SessionLock session, string fileName, FileLockEntry fileEntry, bool acquired)
    {
        if (acquired)
        {
            try
            {
                fileEntry.Semaphore.Release();
            }
            catch (ObjectDisposedException) { }

            lock (_syncRoot)
            {
                session.ActiveFileLocks--;
                if (session.ActiveFileLocks <= 0)
                {
                    session.ActiveFileLocks = 0;
                    if (session.AllFileLocksReleasedTcs != null)
                    {
                        session.IsWorkspaceLocked = true;
                        session.WorkspaceLockReleasedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        session.AllFileLocksReleasedTcs.TrySetResult();
                        session.AllFileLocksReleasedTcs = null;
                    }
                }
            }
        }

        lock (_syncRoot)
        {
            fileEntry.RefCount--;
            if (fileEntry.RefCount <= 0)
            {
                session.FileLocks.TryRemove(fileName, out _);
                try { fileEntry.Semaphore.Dispose(); } catch { }
            }

            session.RefCount--;
            if (session.RefCount <= 0)
            {
                if (session.ActiveFileLocks == 0 && (session.FileLocks.IsEmpty || session.CleanupRequested))
                {
                    if (_sessions.TryGetValue(sessionId, out var cur) && ReferenceEquals(cur, session))
                    {
                        _sessions.TryRemove(sessionId, out _);
                    }
                    DisposeSessionResources(session);
                }
            }
        }
    }

    /// <summary>
    /// Cleans up and disposes all locks and semaphores associated with a session.
    /// If locks are currently held, cleanup is deferred until all locks are released to maintain mutual exclusion.
    /// </summary>
    /// <param name="sessionId">The session ID to clean up.</param>
    public void CleanupSession(Guid sessionId)
    {
        lock (_syncRoot)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.CleanupRequested = true;
                session.AllFileLocksReleasedTcs?.TrySetCanceled();
                session.WorkspaceLockReleasedTcs?.TrySetCanceled();

                if (session.RefCount <= 0 && session.ActiveFileLocks == 0)
                {
                    DisposeSessionResources(session);
                }
            }
        }
    }

    private static void DisposeSessionResources(SessionLock session)
    {
        foreach (var fileLock in session.FileLocks.Values)
        {
            try { fileLock.Semaphore.Dispose(); } catch { }
        }
        session.FileLocks.Clear();
        try { session.WorkspaceSemaphore.Dispose(); } catch { }
    }

    private sealed class WorkspaceReleaser : IDisposable
    {
        private readonly WorkspaceLockManager _manager;
        private readonly Guid _sessionId;
        private readonly SessionLock _session;
        private int _disposed;

        public WorkspaceReleaser(WorkspaceLockManager manager, Guid sessionId, SessionLock session)
        {
            _manager = manager;
            _sessionId = sessionId;
            _session = session;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _manager.ReleaseSession(_sessionId, _session, acquired: true);
            }
        }
    }

    private sealed class FileReleaser : IDisposable
    {
        private readonly WorkspaceLockManager _manager;
        private readonly Guid _sessionId;
        private readonly SessionLock _session;
        private readonly string _fileName;
        private readonly FileLockEntry _fileEntry;
        private int _disposed;

        public FileReleaser(WorkspaceLockManager manager, Guid sessionId, SessionLock session, string fileName, FileLockEntry fileEntry)
        {
            _manager = manager;
            _sessionId = sessionId;
            _session = session;
            _fileName = fileName;
            _fileEntry = fileEntry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _manager.ReleaseFileEntry(_sessionId, _session, _fileName, _fileEntry, acquired: true);
            }
        }
    }
}
