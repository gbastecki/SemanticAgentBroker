using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using SemanticAgentBroker.Client.Features.Auth;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Client.Features.SignalR.Implementations;

/// <inheritdoc />
public class SignalRService : ISignalRService, IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly string _hubUrl;
    private readonly IJSRuntime _jsRuntime;
    private readonly HttpClient _httpClient;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _isConnectingLoopRunning = 0;
    private int _disposed = 0;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _activeTaskSubscriptions = new();

    internal IReadOnlyCollection<Guid> ActiveTaskSubscriptions => _activeTaskSubscriptions.Keys.ToList();

    // Events
    public event Action<AgentTaskLog>? OnLogReceived;
    public event Action<TaskStateChangedEvent>? OnTaskStateChanged;
    public event Action<AgentThoughtEvent>? OnAgentThought;
    public event Action<PlanGeneratedEvent>? OnPlanGenerated;
    public event Action<ToolExecutionEvent>? OnToolExecution;
    public event Action<AgentActivityEvent>? OnAgentActivity;
    public event Action<HubConnectionState>? OnConnectionStateChanged;

    // Planning pipeline events
    public event Action<PlanReviewRequestedEvent>? OnPlanReviewRequested;
    public event Action<PlanRevisionEvent>? OnPlanRevision;
    public event Action<ContextCompactionEvent>? OnContextCompaction;
    public event Action<WorkspaceFileChangedEvent>? OnWorkspaceFileChanged;
    public event Action<TokenCountUpdatedEvent>? OnTokenCountUpdated;

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    public SignalRService(IConfiguration config, IJSRuntime jsRuntime, HttpClient httpClient, AuthenticationStateProvider authStateProvider)
    {
        var apiBaseUrl = config["ApiBaseUrl"] ?? "https://localhost:5100/";
        _hubUrl = $"{apiBaseUrl.TrimEnd('/')}/loghub";
        _jsRuntime = jsRuntime;
        _httpClient = httpClient;
        _authStateProvider = authStateProvider;
    }

    public async Task ConnectAsync()
    {
        if (_connection?.State == HubConnectionState.Connected) return;

        try
        {
            await _connectLock.WaitAsync(_cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (_connection?.State == HubConnectionState.Connected) return;

            if (_connection == null)
            {
                var browserId = await ((CustomAuthStateProvider)_authStateProvider).GetBrowserIdAsync();

                var finalUrl = _hubUrl;
                if (!string.IsNullOrEmpty(browserId))
                {
                    finalUrl = $"{_hubUrl}?browserId={Uri.EscapeDataString(browserId)}";
                }

                _connection = new HubConnectionBuilder()
                    .WithUrl(finalUrl, options =>
                    {
                        // Some clients can send headers during negotiate, but WebSockets might not
                        options.Headers["X-Browser-Id"] = browserId;
                        options.AccessTokenProvider = async () =>
                        {
                            var t = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "authToken");
                            return t;
                        };
                    })
                    .WithAutomaticReconnect(new InfiniteRetryPolicy())
                    .Build();

                // Register typed handlers
                _connection.On<AgentTaskLog>(HubMethodNames.ReceiveLog, log =>
                    OnLogReceived?.Invoke(log));

                _connection.On<TaskStateChangedEvent>(HubMethodNames.TaskStateChanged, evt =>
                    OnTaskStateChanged?.Invoke(evt));

                _connection.On<AgentThoughtEvent>(HubMethodNames.AgentThought, evt =>
                    OnAgentThought?.Invoke(evt));

                _connection.On<PlanGeneratedEvent>(HubMethodNames.PlanGenerated, evt =>
                    OnPlanGenerated?.Invoke(evt));

                _connection.On<ToolExecutionEvent>(HubMethodNames.ToolExecution, evt =>
                    OnToolExecution?.Invoke(evt));

                _connection.On<AgentActivityEvent>(HubMethodNames.AgentActivity, evt =>
                    OnAgentActivity?.Invoke(evt));

                // Planning pipeline event handlers
                _connection.On<PlanReviewRequestedEvent>(HubMethodNames.PlanReviewRequested, evt =>
                    OnPlanReviewRequested?.Invoke(evt));

                _connection.On<PlanRevisionEvent>(HubMethodNames.PlanRevision, evt =>
                    OnPlanRevision?.Invoke(evt));

                _connection.On<ContextCompactionEvent>(HubMethodNames.ContextCompaction, evt =>
                    OnContextCompaction?.Invoke(evt));

                _connection.On<WorkspaceFileChangedEvent>(HubMethodNames.WorkspaceFileChanged, evt =>
                    OnWorkspaceFileChanged?.Invoke(evt));

                _connection.On<TokenCountUpdatedEvent>(HubMethodNames.TokenCountUpdated, evt =>
                    OnTokenCountUpdated?.Invoke(evt));

                _connection.Reconnecting += _ =>
                {
                    OnConnectionStateChanged?.Invoke(HubConnectionState.Reconnecting);
                    return Task.CompletedTask;
                };

                _connection.Reconnected += async _ =>
                {
                    OnConnectionStateChanged?.Invoke(HubConnectionState.Connected);
                    await ResubscribeAllAsync();
                };

                _connection.Closed += error =>
                {
                    OnConnectionStateChanged?.Invoke(HubConnectionState.Disconnected);
                    _ = StartConnectionLoopAsync();
                    return Task.CompletedTask;
                };
            }

            if (_connection.State == HubConnectionState.Disconnected)
            {
                _ = StartConnectionLoopAsync();
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // ── Group Management ───────────────────────────────────────────────────

    private async Task StartConnectionLoopAsync()
    {
        if (Interlocked.CompareExchange(ref _isConnectingLoopRunning, 1, 0) == 1)
        {
            return;
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var conn = _connection;
                if (conn == null || conn.State != HubConnectionState.Disconnected)
                {
                    break;
                }

                try
                {
                    await conn.StartAsync(_cts.Token);
                    OnConnectionStateChanged?.Invoke(HubConnectionState.Connected);
                    await ResubscribeAllAsync();
                    return;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException || _cts.IsCancellationRequested)
                    {
                        break;
                    }

                    if (ex.ToString().Contains("401") || ex.ToString().Contains("Unauthorized"))
                    {
                        _ = ((CustomAuthStateProvider)_authStateProvider).MarkUserAsLoggedOut();
                        break;
                    }

                    try
                    {
                        await Task.Delay(5000, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isConnectingLoopRunning, 0);
        }
    }

    internal class InfiniteRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (retryContext.RetryReason != null)
            {
                var ex = retryContext.RetryReason;
                if (ex.Message.Contains("401") || (ex is HttpRequestException hre && hre.StatusCode == System.Net.HttpStatusCode.Unauthorized))
                {
                    return null; // Give up on automatic reconnect if token is invalid
                }
            }

            if (retryContext.PreviousRetryCount == 0) return TimeSpan.Zero;
            if (retryContext.PreviousRetryCount == 1) return TimeSpan.FromSeconds(2);
            if (retryContext.PreviousRetryCount == 2) return TimeSpan.FromSeconds(5);
            if (retryContext.PreviousRetryCount == 3) return TimeSpan.FromSeconds(10);
            return TimeSpan.FromSeconds(15);
        }
    }

    public async Task SubscribeToTaskAsync(Guid taskId)
    {
        _activeTaskSubscriptions[taskId] = 0;
        try
        {
            if (_connection?.State == HubConnectionState.Connected)
                await _connection.SendAsync(HubMethodNames.SubscribeToTask, taskId);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    public async Task UnsubscribeFromTaskAsync(Guid taskId)
    {
        _activeTaskSubscriptions.TryRemove(taskId, out _);
        try
        {
            if (_connection?.State == HubConnectionState.Connected)
                await _connection.SendAsync(HubMethodNames.UnsubscribeFromTask, taskId);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private async Task ResubscribeAllAsync()
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        foreach (var taskId in _activeTaskSubscriptions.Keys)
        {
            try
            {
                await _connection.SendAsync(HubMethodNames.SubscribeToTask, taskId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to resubscribe to task {taskId}: {ex.Message}");
            }
        }
    }

    // ── Outbound: User → Agent ─────────────────────────────────────────────

    public async Task<bool> SendInterruptionAsync(Guid taskId, string message)
    {
        try
        {
            var conn = _connection;
            if (conn?.State == HubConnectionState.Connected)
            {
                return await conn.InvokeAsync<bool>(HubMethodNames.SendInterruption, taskId, message);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
        return false;
    }

    public async Task ApproveStepAsync(Guid taskId)
    {
        try
        {
            var conn = _connection;
            if (conn?.State == HubConnectionState.Connected)
                await conn.SendAsync(HubMethodNames.ApproveStep, taskId);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    public async Task RejectStepAsync(Guid taskId, string feedback)
    {
        try
        {
            var conn = _connection;
            if (conn?.State == HubConnectionState.Connected)
                await conn.SendAsync(HubMethodNames.RejectStep, taskId, feedback);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    // ── Plan Review Actions ────────────────────────────────────────────────

    public async Task ApprovePlanAsync(Guid taskId)
    {
        try
        {
            var conn = _connection;
            if (conn?.State == HubConnectionState.Connected)
                await conn.SendAsync(HubMethodNames.ApprovePlan, taskId);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    public async Task ModifyPlanAsync(Guid taskId, string modifiedPlanMarkdown)
    {
        try
        {
            var conn = _connection;
            if (conn?.State == HubConnectionState.Connected)
                await conn.SendAsync(HubMethodNames.ModifyPlan, taskId, modifiedPlanMarkdown);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    public async Task RejectPlanAsync(Guid taskId, string feedback)
    {
        try
        {
            var conn = _connection;
            if (conn?.State == HubConnectionState.Connected)
                await conn.SendAsync(HubMethodNames.RejectPlan, taskId, feedback);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    public async Task AskPlanQuestionAsync(Guid taskId, string question)
    {
        try
        {
            var conn = _connection;
            if (conn?.State == HubConnectionState.Connected)
                await conn.SendAsync(HubMethodNames.AskPlanQuestion, taskId, question);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        try
        {
            await _connectLock.WaitAsync();
            try
            {
                if (_connection != null)
                {
                    await _connection.DisposeAsync();
                    _connection = null;
                }
            }
            finally
            {
                try { _connectLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
        catch (ObjectDisposedException) { }
        finally
        {
            _cts.Dispose();
            _connectLock.Dispose();
        }
    }
}
