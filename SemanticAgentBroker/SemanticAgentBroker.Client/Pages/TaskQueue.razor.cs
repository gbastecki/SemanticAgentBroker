using Microsoft.AspNetCore.Components;
using SemanticAgentBroker.Shared.Domain;
using System.Net.Http.Json;

namespace SemanticAgentBroker.Client.Pages;

public partial class TaskQueue : IAsyncDisposable
{
    private List<TaskSession>? tasks;
    private HashSet<Guid> selectedIds = [];
    private int currentPage = 1;
    private int pageSize = 20;
    private int totalCount = 0;
    
    private string queueSearchTerm = "";
    private IEnumerable<TaskSession> FilteredTasks 
    {
        get 
        {
            if (tasks == null) return Enumerable.Empty<TaskSession>();
            if (string.IsNullOrWhiteSpace(queueSearchTerm)) return tasks;
            var terms = queueSearchTerm.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(t => t.ToLowerInvariant()).ToList();
            return tasks.Where(t => terms.All(term => 
                t.Id.ToString().Contains(term, StringComparison.InvariantCultureIgnoreCase) ||
                (!string.IsNullOrEmpty(t.PluginTaskId) && t.PluginTaskId.Contains(term, StringComparison.InvariantCultureIgnoreCase)) ||
                FormatMode(t.Mode).Contains(term, StringComparison.InvariantCultureIgnoreCase) ||
                t.State.ToString().Contains(term, StringComparison.InvariantCultureIgnoreCase)
            ));
        }
    }
    private int totalPages = 1;
    private bool showDeleteModal = false;
    private bool isDeleting = false;
    private Action<TaskStateChangedEvent>? _stateChangedHandler; // Store handler for cleanup
    private bool _isDisposed;

    protected override async Task OnInitializedAsync()
    {
        _stateChangedHandler = _ => InvokeAsync(() => { if (!_isDisposed) return RefreshTasks(); return Task.CompletedTask; });
        SignalR.OnTaskStateChanged += _stateChangedHandler;
        await RefreshTasks();
    }

    private bool isRefreshing = false;

    private async Task RefreshTasks()
    {
        if (_isDisposed || isRefreshing) return;
        isRefreshing = true;
        if (!_isDisposed) StateHasChanged();

        try
        {
            var result = await Http.GetFromJsonAsync<PaginatedResult>($"api/tasks?page={currentPage}&pageSize={pageSize}");
            if (!_isDisposed && result != null)
            {
                tasks = result.Items;
                totalCount = result.TotalCount;
                totalPages = result.TotalPages;
                currentPage = result.Page;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            isRefreshing = false;
            if (!_isDisposed)
            {
                StateHasChanged();
            }
        }
    }

    private async Task GoToPage(int page)
    {
        currentPage = Math.Clamp(page, 1, Math.Max(1, totalPages));
        await RefreshTasks();
    }

    private IEnumerable<int> GetPageNumbers()
    {
        const int maxVisible = 7;
        int start = Math.Max(1, currentPage - maxVisible / 2);
        int end = Math.Min(totalPages, start + maxVisible - 1);
        start = Math.Max(1, end - maxVisible + 1);
        return Enumerable.Range(start, end - start + 1);
    }

    // ── Selection ─────────────────────────────────────────────────────

    private void ToggleSelect(Guid taskId)
    {
        if (!selectedIds.Remove(taskId))
            selectedIds.Add(taskId);
    }

    private void ToggleSelectAll(ChangeEventArgs e)
    {
        if (tasks == null) return;
        if ((bool)(e.Value ?? false))
        {
            foreach (var t in tasks) selectedIds.Add(t.Id);
        }
        else
        {
            foreach (var t in tasks) selectedIds.Remove(t.Id);
        }
    }

    private void ClearSelection() => selectedIds.Clear();

    private void ShowDeleteConfirmation() => showDeleteModal = true;

    private async Task BulkDeleteConfirmed()
    {
        isDeleting = true;
        try
        {
            var response = await Http.PostAsJsonAsync("api/tasks/bulk-delete", new { taskIds = selectedIds.ToList() });
            if (response.IsSuccessStatusCode)
            {
                selectedIds.Clear();
                showDeleteModal = false;
                await RefreshTasks();
            }
        }
        finally { isDeleting = false; }
    }

    // ── Mode Formatting ───────────────────────────────────────────────

    private static string FormatMode(ExecutionMode mode) => mode switch
    {
        ExecutionMode.Auto => "Auto",
        ExecutionMode.Planned => "Planned",
        _ => mode.ToString()
    };

    internal static bool CanCancel(TaskState state) =>
        state is not (TaskState.Completed or TaskState.Cancelled or TaskState.Failed);

    // ── Actions ───────────────────────────────────────────────────────

    private async Task PauseTask(Guid taskId)
    {
        await Http.PostAsync($"api/tasks/{taskId}/pause", null);
        await RefreshTasks();
    }

    private async Task ResumeTask(Guid taskId)
    {
        await Http.PostAsync($"api/tasks/{taskId}/resume", null);
        await RefreshTasks();
    }

    private async Task CancelTask(Guid taskId)
    {
        await Http.DeleteAsync($"api/tasks/{taskId}");
        await RefreshTasks();
    }

    private async Task RestartTask(Guid taskId)
    {
        var response = await Http.PostAsync($"api/tasks/{taskId}/restart", null);
        if (response.IsSuccessStatusCode)
        {
            var req = await response.Content.ReadFromJsonAsync<AgentTaskRequest>();
            if (req != null)
                Nav.NavigateTo($"/live/{req.TaskId}");
        }
        await RefreshTasks();
    }

    private record PaginatedResult(List<TaskSession> Items, int TotalCount, int Page, int PageSize, int TotalPages);

    public ValueTask DisposeAsync()
    {
        _isDisposed = true;
        if (_stateChangedHandler != null)
        {
            SignalR.OnTaskStateChanged -= _stateChangedHandler;
        }

        return ValueTask.CompletedTask;
    }
}
