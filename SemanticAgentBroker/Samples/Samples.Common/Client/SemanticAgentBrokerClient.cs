using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Samples.Common.Authentication;
using Samples.Common.Models;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;

namespace Samples.Common.Client;

/// <summary>
/// High-level strongly-typed client for integrating with Semantic Agent Broker.
/// Wraps REST API endpoints, token authentication, file streaming, and SignalR hub connections.
/// </summary>
public sealed class SemanticAgentBrokerClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TokenService _tokenService;
    private readonly AuthHandler _authHandler;
    private readonly HttpClientHandler _httpHandler;
    private readonly string _baseUrl;
    private readonly bool _acceptAnyServerCert;
    private readonly ILogger<SemanticAgentBrokerClient> _logger;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string BaseUrl => _baseUrl;
    public TokenService TokenService => _tokenService;

    public SemanticAgentBrokerClient(
        string baseUrl = "https://localhost:5100",
        string username = "admin",
        string password = "admin",
        string browserId = "sample-integration-client",
        bool acceptAnyServerCertificate = true,
        ILogger<SemanticAgentBrokerClient>? logger = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _acceptAnyServerCert = acceptAnyServerCertificate;
        _logger = logger ?? NullLogger<SemanticAgentBrokerClient>.Instance;

        _httpHandler = new HttpClientHandler();
        if (_acceptAnyServerCert)
        {
            _httpHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        // Dedicated client for token acquisition (does not use AuthHandler to avoid recursion)
        var authHttpClient = new HttpClient(_httpHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(_baseUrl)
        };
        _tokenService = new TokenService(authHttpClient, username, password, browserId);

        _authHandler = new AuthHandler(_tokenService)
        {
            InnerHandler = _httpHandler
        };

        _httpClient = new HttpClient(_authHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(_baseUrl)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Browser-Id", browserId);
    }

    // ── Task Execution ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and enqueues a new agent task. Supports optional atomic staging of initial workspace files.
    /// </summary>
    public async Task<AgentTaskRequest> CreateTaskAsync(
        AgentTaskRequest request,
        IEnumerable<(string FileName, Stream Content)>? initialFiles = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AgentName))
        {
            request.AgentName = "GeneralAssistant";
        }

        if (request.TaskId == Guid.Empty)
        {
            request.TaskId = Guid.NewGuid();
        }

        HttpResponseMessage response;

        if (initialFiles != null && initialFiles.Any())
        {
            using var formData = new MultipartFormDataContent();
            var jsonContent = new StringContent(JsonSerializer.Serialize(request, JsonOptions));
            jsonContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            formData.Add(jsonContent, "request");

            foreach (var (fileName, stream) in initialFiles)
            {
                var fileContent = new StreamContent(stream);
                formData.Add(fileContent, "files", fileName);
            }

            response = await _httpClient.PostAsync(ApiRoutes.TasksBase, formData, ct);
        }
        else
        {
            response = await _httpClient.PostAsJsonAsync(ApiRoutes.TasksBase, request, JsonOptions, ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Failed to create task (HTTP {(int)response.StatusCode} {response.StatusCode}): {errorBody}",
                null,
                response.StatusCode);
        }

        var created = await response.Content.ReadFromJsonAsync<AgentTaskRequest>(JsonOptions, ct);
        return created ?? request;
    }

    /// <summary>
    /// Retrieves details for a specific task.
    /// </summary>
    public async Task<TaskSessionDetailDto?> GetTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"/api/tasks/{taskId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TaskSessionDetailDto>(JsonOptions, ct);
    }

    /// <summary>
    /// Polls a task until it reaches a terminal state (Completed, Failed, or Cancelled).
    /// </summary>
    public async Task<TaskSessionDetailDto> AwaitTaskCompletionAsync(
        Guid taskId,
        TimeSpan timeout,
        TimeSpan pollInterval,
        Action<TaskSessionDetailDto>? onProgress = null,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var session = await GetTaskAsync(taskId, cts.Token);
            if (session == null)
            {
                throw new InvalidOperationException($"Task '{taskId}' was not found.");
            }

            onProgress?.Invoke(session);

            if (session.State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled)
            {
                return session;
            }

            await Task.Delay(pollInterval, cts.Token);
        }

        throw new TimeoutException($"Task '{taskId}' did not complete within the timeout of {timeout.TotalSeconds}s.");
    }

    /// <summary>
    /// Retrieves the append-only event log for a task.
    /// </summary>
    public async Task<List<TaskEvent>> GetTaskEventsAsync(Guid taskId, CancellationToken ct = default)
    {
        var result = await _httpClient.GetFromJsonAsync<List<TaskEvent>>($"/api/tasks/{taskId}/events", JsonOptions, ct);
        return result ?? new List<TaskEvent>();
    }

    /// <summary>
    /// Retrieves the conversation / snapshot chat history for a task.
    /// </summary>
    public async Task<List<ChatMessageDto>> GetChatHistoryAsync(Guid taskId, CancellationToken ct = default)
    {
        var result = await _httpClient.GetFromJsonAsync<List<ChatMessageDto>>($"/api/tasks/{taskId}/chat", JsonOptions, ct);
        return result ?? new List<ChatMessageDto>();
    }

    /// <summary>
    /// Retrieves the latest implementation plan for a task.
    /// </summary>
    public async Task<ImplementationPlan?> GetTaskPlanAsync(Guid taskId, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"/api/tasks/{taskId}/plan", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ImplementationPlan>(JsonOptions, ct);
    }

    // ── Task Management & Control ──────────────────────────────────────────────

    /// <summary>
    /// Pauses an active or queued task.
    /// </summary>
    public async Task<bool> PauseTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsync($"/api/tasks/{taskId}/pause", null, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Resumes a paused task.
    /// </summary>
    public async Task<bool> ResumeTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsync($"/api/tasks/{taskId}/resume", null, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Restarts a completed or failed task from scratch with a clean slate.
    /// </summary>
    public async Task<AgentTaskRequest?> RestartTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsync($"/api/tasks/{taskId}/restart", null, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AgentTaskRequest>(JsonOptions, ct);
    }

    /// <summary>
    /// Sends a follow-up continuation prompt to an existing task session.
    /// </summary>
    public async Task<AgentTaskRequest?> ProceedTaskAsync(Guid taskId, string followUpPrompt, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"/api/tasks/{taskId}/proceed", followUpPrompt, JsonOptions, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AgentTaskRequest>(JsonOptions, ct);
    }

    /// <summary>
    /// Cancels and deletes a task along with its associated workspace.
    /// </summary>
    public async Task<bool> DeleteTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        using var response = await _httpClient.DeleteAsync($"/api/tasks/{taskId}", ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Bulk deletes multiple tasks in a single request (up to 500 tasks per batch).
    /// </summary>
    public async Task<int> BulkDeleteTasksAsync(IEnumerable<Guid> taskIds, CancellationToken ct = default)
    {
        var req = new BulkDeleteRequestDto { TaskIds = taskIds.ToList() };
        using var response = await _httpClient.PostAsJsonAsync(ApiRoutes.TasksBulkDelete, req, JsonOptions, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Failed to bulk delete tasks ({response.StatusCode}): {error}", null, response.StatusCode);
        }
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return result.TryGetProperty("deleted", out var countProp) ? countProp.GetInt32() : req.TaskIds.Count;
    }

    /// <summary>
    /// Retrieves system-wide aggregated task statistics.
    /// </summary>
    public async Task<TaskStatsDto?> GetTaskStatsAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<TaskStatsDto>(ApiRoutes.TaskStats, JsonOptions, ct);
    }

    /// <summary>
    /// Retrieves a paginated list of tasks.
    /// </summary>
    public async Task<PagedResult<TaskSessionDetailDto>> ListTasksAsync(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var result = await _httpClient.GetFromJsonAsync<PagedResult<TaskSessionDetailDto>>($"/api/tasks?page={page}&pageSize={pageSize}", JsonOptions, ct);
        return result ?? new PagedResult<TaskSessionDetailDto>();
    }

    /// <summary>
    /// Retrieves all registered agents from the Broker.
    /// </summary>
    public async Task<List<AgentSummaryDto>> ListAgentsAsync(CancellationToken ct = default)
    {
        var result = await _httpClient.GetFromJsonAsync<List<AgentSummaryDto>>(ApiRoutes.AgentsBase, JsonOptions, ct);
        return result ?? new List<AgentSummaryDto>();
    }

    // ── Workspace Management ───────────────────────────────────────────────────

    /// <summary>
    /// Lists all files in a task workspace.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceFileDto>> ListWorkspaceFilesAsync(Guid taskId, CancellationToken ct = default)
    {
        var result = await _httpClient.GetFromJsonAsync<WorkspaceFileDto[]>($"/api/tasks/{taskId}/workspace", JsonOptions, ct);
        return result ?? Array.Empty<WorkspaceFileDto>();
    }

    /// <summary>
    /// Reads a text file from a task workspace.
    /// </summary>
    public async Task<string> GetWorkspaceTextFileAsync(Guid taskId, string fileName, CancellationToken ct = default)
    {
        return await _httpClient.GetStringAsync($"/api/tasks/{taskId}/workspace/{Uri.EscapeDataString(fileName)}", ct);
    }

    /// <summary>
    /// Downloads any file (binary or text) from a task workspace.
    /// </summary>
    public async Task<byte[]> DownloadWorkspaceFileAsync(Guid taskId, string fileName, CancellationToken ct = default)
    {
        return await _httpClient.GetByteArrayAsync($"/api/tasks/{taskId}/workspace/{Uri.EscapeDataString(fileName)}", ct);
    }

    /// <summary>
    /// Uploads a file to an existing task workspace.
    /// </summary>
    public async Task<bool> UploadWorkspaceFileAsync(Guid taskId, string fileName, Stream content, CancellationToken ct = default)
    {
        using var formData = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);
        formData.Add(fileContent, "files", fileName);

        using var response = await _httpClient.PostAsync($"/api/tasks/{taskId}/workspace/upload", formData, ct);
        return response.IsSuccessStatusCode;
    }

    // ── SignalR Hub Connectivity ───────────────────────────────────────────────

    /// <summary>
    /// Creates and configures a SignalR HubConnection to /loghub with automated Bearer authentication and browser fingerprinting.
    /// </summary>
    public HubConnection CreateSignalRConnection()
    {
        var hubUrl = $"{_baseUrl}/loghub";

        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = async () => await _tokenService.GetTokenAsync();
                options.Headers.Add("X-Browser-Id", _tokenService.BrowserId);

                if (_acceptAnyServerCert)
                {
                    options.HttpMessageHandlerFactory = _ => new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    };
                    options.WebSocketConfiguration = wsOptions =>
                    {
                        wsOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                    };
                }
            })
            .WithAutomaticReconnect();

        return builder.Build();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _authHandler.Dispose();
        _tokenService.Dispose();
        _httpHandler.Dispose();
    }
}
