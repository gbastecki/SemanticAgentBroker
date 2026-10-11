using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using SemanticAgentBroker.Shared.Domain;

using Microsoft.AspNetCore.Components.Authorization;

namespace SemanticAgentBroker.Client.Pages;

/// <summary>
/// Dashboard page providing an overview of system active tasks and allowing to initiate new ones.
/// </summary>
public partial class Home
{
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    private bool showModal = false;
    private string userPrompt = "";
    private ExecutionMode selectedMode = ExecutionMode.Planned;
    private bool debugMode = false;
    private bool traceMode = false;
    private bool isPrivate = false;
    private List<SelectedFileModel> selectedFiles = new();

    private class SelectedFileModel
    {
        public string Name { get; set; } = "";
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = "";
        public long Size => Content.LongLength;
    }
    private long maxUploadSizeMb = 10;
    private bool isSubmitting = false;

    private List<TaskSession>? sessions;
    private List<PluginInfo>? plugins;
    private List<TaskUiModel>? availableTasks;
    private TaskUiModel? selectedTask;

    private int activeCount, completedCount, failedCount, totalCount, pluginCount;

    private class PluginInfo
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsEnabled { get; set; }
    }

    private class TaskUiModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string ScheduleCron { get; set; } = "";
        public bool IsEnabled { get; set; }
        public string PluginId { get; set; } = "";
        public string PluginName { get; set; } = "";
        public List<string> AllowedOptions { get; set; } = new();
    }

    private Action<TaskStateChangedEvent>? _stateChangedHandler;
    private bool _isDisposed;
    private bool _isRefreshing;

    protected override async Task OnInitializedAsync()
    {
        _stateChangedHandler = _ => InvokeAsync(async () => { if (!_isDisposed) await RefreshTasks(); });
        SignalR.OnTaskStateChanged += _stateChangedHandler;
        await Task.WhenAll(RefreshTasks(), LoadPluginsAndTasks(), LoadUploadLimit());
    }

    public ValueTask DisposeAsync()
    {
        _isDisposed = true;
        if (_stateChangedHandler != null)
            SignalR.OnTaskStateChanged -= _stateChangedHandler;
        return ValueTask.CompletedTask;
    }

    private async Task LoadUploadLimit()
    {
        try
        {
            var config = await Http.GetFromJsonAsync<Dictionary<string, long>>("api/config/upload-limit");
            if (!_isDisposed && config != null && config.TryGetValue("maxUploadSizeMb", out var limit))
                maxUploadSizeMb = limit;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private async Task RefreshTasks()
    {
        if (_isDisposed || _isRefreshing) return;
        _isRefreshing = true;

        try
        {
            var tasksTask = Http.GetFromJsonAsync<PaginatedResult>("api/tasks?page=1&pageSize=100");
            var statsTask = Http.GetFromJsonAsync<TaskStatsDto>("api/tasks/stats");

            var result = await tasksTask;
            if (!_isDisposed && result?.Items != null)
            {
                sessions = result.Items;
                totalCount = result.TotalCount;
                activeCount = sessions.Count(t => t.State is TaskState.InProgress or TaskState.AwaitingApproval or TaskState.AwaitingInput or TaskState.Planning or TaskState.PlanReview or TaskState.Queued);
                completedCount = sessions.Count(t => t.State == TaskState.Completed);
                failedCount = sessions.Count(t => t.State == TaskState.Failed);
            }

            try
            {
                var stats = await statsTask;
                if (!_isDisposed && stats != null)
                {
                    totalCount = stats.TotalCount;
                    activeCount = stats.ActiveCount;
                    completedCount = stats.CompletedCount;
                    failedCount = stats.FailedCount;
                }
            }
            catch { /* fallback to page-based counts if stats endpoint is unavailable */ }

            if (!_isDisposed)
            {
                StateHasChanged();
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task LoadPluginsAndTasks()
    {
        try
        {
            plugins = await Http.GetFromJsonAsync<List<PluginInfo>>("api/plugins");
            pluginCount = plugins?.Count ?? 0;
            availableTasks = new List<TaskUiModel>();

            if (plugins != null)
            {
                foreach (var p in plugins.Where(x => x.IsEnabled))
                {
                    try
                    {
                        var pluginTasks = await Http.GetFromJsonAsync<List<TaskUiModel>>($"api/plugins/{p.Id}/tasks");
                        if (pluginTasks != null)
                        {
                            foreach (var t in pluginTasks.Where(x => x.IsEnabled))
                            {
                                t.PluginId = p.Id;
                                t.PluginName = p.DisplayName;
                                availableTasks.Add(t);
                            }
                        }
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                }
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private string taskSearchTerm = "";
    private IEnumerable<TaskUiModel> FilteredAvailableTasks
    {
        get
        {
            if (availableTasks == null) return Enumerable.Empty<TaskUiModel>();
            var sorted = availableTasks.OrderBy(t => t.Name);
            if (string.IsNullOrWhiteSpace(taskSearchTerm)) return sorted;
            var terms = taskSearchTerm.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Select(t => t.ToLowerInvariant()).ToList();
            return sorted.Where(t => terms.All(term => 
                t.Name.ToLowerInvariant().Contains(term) || 
                t.Description.ToLowerInvariant().Contains(term) ||
                t.PluginName.ToLowerInvariant().Contains(term)));
        }
    }

    private void ShowPromptModal(TaskUiModel task)
    {
        selectedTask = task;
        userPrompt = "";
        selectedMode = task.AllowedOptions.Contains("AutoExecution") ? ExecutionMode.Auto : ExecutionMode.Planned;
        debugMode = false;
        traceMode = false;
        isPrivate = false;
        selectedFiles.Clear();
        errorMessage = null;
        showModal = true;
    }

    private async Task HandleFilesSelected(InputFileChangeEventArgs e)
    {
        IReadOnlyList<IBrowserFile> files;
        try
        {
            files = e.GetMultipleFiles(10);
        }
        catch (InvalidOperationException)
        {
            errorMessage = "A maximum of 10 files can be selected at once.";
            return;
        }

        long maxAllowedSize = maxUploadSizeMb * 1024 * 1024;
        foreach (var file in files)
        {
            if (selectedFiles.Any(f => f.Name == file.Name))
                continue;

            if (file.Size > maxAllowedSize)
            {
                errorMessage = $"File '{file.Name}' exceeds the {maxUploadSizeMb} MB limit.";
                continue;
            }

            try
            {
                using var stream = file.OpenReadStream(maxAllowedSize);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                selectedFiles.Add(new SelectedFileModel
                {
                    Name = file.Name,
                    Content = ms.ToArray(),
                    ContentType = file.ContentType
                });
            }
            catch (Exception ex)
            {
                errorMessage = $"Error reading '{file.Name}': {ex.Message}";
            }
        }
    }

    private void RemoveFile(SelectedFileModel file)
    {
        selectedFiles.Remove(file);
    }

    private string? errorMessage;

    private async Task SubmitTask()
    {
        if (selectedTask == null || isSubmitting) return;
        isSubmitting = true;
        errorMessage = null; // Clear previous error
        
        try 
        {
            var req = new AgentTaskRequest
            {
                AgentName = "RuntimeAgent", // Default fallback
                Prompt = string.IsNullOrWhiteSpace(userPrompt) ? $"Please execute task: {selectedTask.Name}" : userPrompt,
                Mode = selectedMode,
                DebugMode = debugMode,
                TraceMode = traceMode,
                IsPrivate = isPrivate,
                SourcePluginId = selectedTask.PluginId,
                PluginTaskId = selectedTask.Id
            };

            HttpResponseMessage response;
            if (selectedFiles.Count > 0)
            {
                using var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(req)), "request");

                foreach (var file in selectedFiles)
                {
                    var byteContent = new ByteArrayContent(file.Content);
                    if (!string.IsNullOrWhiteSpace(file.ContentType) &&
                        System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(file.ContentType, out var mediaType))
                    {
                        byteContent.Headers.ContentType = mediaType;
                    }
                    multipart.Add(byteContent, "files", file.Name);
                }

                response = await Http.PostAsync("api/tasks", multipart);
            }
            else
            {
                response = await Http.PostAsJsonAsync("api/tasks", req);
            }

            if (response.IsSuccessStatusCode)
            {
                var created = await response.Content.ReadFromJsonAsync<AgentTaskRequest>();
                if (created != null)
                {
                    showModal = false;
                    Nav.NavigateTo($"/live/{created.TaskId}");
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                errorMessage = $"Error creating task: {error}";
                Console.WriteLine(errorMessage);
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Exception creating task: {ex.Message}";
            Console.WriteLine(errorMessage);
        }
        finally
        {
            isSubmitting = false;
        }
    }

    private record PaginatedResult(List<TaskSession> Items, int TotalCount, int Page, int PageSize, int TotalPages);
}
