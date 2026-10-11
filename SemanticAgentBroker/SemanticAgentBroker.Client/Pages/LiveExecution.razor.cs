using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SemanticAgentBroker.Shared.Domain;
using System.Net.Http.Json;

namespace SemanticAgentBroker.Client.Pages;

/// <summary>
/// Execution flow monitoring page for tracking an agent task.
/// </summary>
public partial class LiveExecution : IAsyncDisposable
{
    [Parameter] public Guid TaskId { get; set; }

    private TaskSession? taskDetails;
    private List<AgentTaskLog> logs = new();
    private List<ChatMessage> chatMessages = new();
    private readonly object _chatLock = new();
    private readonly object _logLock = new();
    private string activeTab = "split";
    // userInput is now managed by the isolated ChatInput component
    private string approvalInput = "";
    private string rejectFeedback = "";
    private string activityLabel = "";
    private bool showRejectInput = false;
    private bool followAgent = true;
    private string? pendingPlan;
    private bool planRequiresApproval;
    private string? pendingApprovalDetail;

    // Plan review state
    private string? planReviewMarkdown;
    private string? planReviewQuestions;
    private int planReviewVersion;
    private bool isPlanReviewActive;
    private bool showPlanEditor;
    private string planEditorContent = "";
    private string planRejectFeedback = "";
    private bool showPlanRejectInput;
    private string planQuestionText = "";
    private bool showPlanQuestionInput;

    private DotNetObjectReference<LiveExecution>? dotNetRef;
    private System.Threading.Timer? debounceTimer;
    private int _isStateDirty;
    private bool isLoadingOlderLogs;
    private DateTime? oldestLogTimestamp;

    private int currentTokenCountOrchestrator;
    private int tokenLimitOrchestrator;
    private int currentTokenCountSubagent;
    private int tokenLimitSubagent;

    // Workspace state
    private List<WorkspaceFileDto> workspaceFiles = new();
    private bool isLoadingFiles;
    private bool showFileViewer;
    private string viewingFileName = "";
    private long maxUploadSizeMb = 10;
    
    private string currentUserName = "User";

    internal record ChatMessage
    {
        public string Type { get; set; }
        public string Content { get; set; }
        public DateTime Time { get; set; }
        public List<string>? FilesAffected { get; set; }
        public string? CachedHtml { get; set; }
        public string? AgentName { get; set; }
        public string? UserName { get; set; }
        public string? FullHtml { get; set; }
        public string? FullContent { get; set; }
        public string? Observation { get; set; }
        public string? ToolArgs { get; set; }
        public bool IsExpanded { get; set; }

        public ChatMessage(string Type, string Content, DateTime Time, List<string>? FilesAffected = null, string? CachedHtml = null, string? AgentName = null, string? UserName = null, string? FullHtml = null, string? FullContent = null, string? Observation = null, string? ToolArgs = null)
        {
            this.Type = Type; this.Content = Content; this.Time = Time; this.FilesAffected = FilesAffected;
            this.CachedHtml = CachedHtml; this.AgentName = AgentName; this.UserName = UserName;
            this.FullHtml = FullHtml; this.FullContent = FullContent; this.Observation = Observation;
            this.ToolArgs = ToolArgs; this.IsExpanded = false;
        }
    }
    private record ChatSnapshotDto(string Role, string Content, DateTime Timestamp, string? AgentName, string? UserName);
    private record PlanDto(string PlanMarkdown, string? OpenQuestions, int Version, PlanStatus Status, DateTime? CreatedAt);

    private Guid _loadedTaskId;

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        if (authState.User.Identity?.IsAuthenticated == true)
        {
            currentUserName = authState.User.Identity.Name ?? "User";
        }

        dotNetRef = DotNetObjectReference.Create(this);
        debounceTimer = new System.Threading.Timer(OnDebounceRender, null, Timeout.Infinite, Timeout.Infinite);

        SignalR.OnLogReceived += OnLog;
        SignalR.OnAgentThought += OnThought;
        SignalR.OnPlanGenerated += OnPlan;
        SignalR.OnToolExecution += OnTool;
        SignalR.OnAgentActivity += OnActivity;
        SignalR.OnTaskStateChanged += OnStateChanged;
        SignalR.OnPlanReviewRequested += OnPlanReviewRequested;
        SignalR.OnPlanRevision += OnPlanRevision;
        SignalR.OnContextCompaction += OnContextCompaction;
        SignalR.OnWorkspaceFileChanged += OnWorkspaceFileChanged;
        SignalR.OnConnectionStateChanged += OnConnectionStateChanged;
        SignalR.OnTokenCountUpdated += OnTokenCountUpdated;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (TaskId != _loadedTaskId)
        {
            var oldTaskId = _loadedTaskId;
            _loadedTaskId = TaskId;

            if (oldTaskId != Guid.Empty)
            {
                try { await SignalR.UnsubscribeFromTaskAsync(oldTaskId); } catch { }
                ResetTaskState();
            }

            await LoadTaskDataAsync();
        }
    }

    internal void ResetTaskState()
    {
        taskDetails = null;
        lock (_logLock)
        {
            logs.Clear();
            oldestLogTimestamp = null;
        }
        lock (_chatLock)
        {
            chatMessages.Clear();
        }
        workspaceFiles.Clear();
        planReviewMarkdown = null;
        planReviewQuestions = null;
        planReviewVersion = 1;
        isPlanReviewActive = false;
        pendingApprovalDetail = null;
        activeTab = "split";
    }

    private async Task LoadTaskDataAsync()
    {
        try
        {
            await LoadUploadLimit();
            taskDetails = await Http.GetFromJsonAsync<TaskSession>($"api/tasks/{TaskId}");
            if (taskDetails != null && !taskDetails.AllowedOptions.Contains("ConsoleView"))
            {
                activeTab = "chat";
            }
            var existingLogs = await Http.GetFromJsonAsync<List<AgentTaskLog>>($"api/tasks/{TaskId}/logs?limit=200");
            if (existingLogs != null) 
            {
                lock (_logLock)
                {
                    logs = MergeLogs(existingLogs, logs);
                    if (logs.Any()) oldestLogTimestamp = logs.First().Timestamp;
                }
            }

            var loadedChatMessages = new List<ChatMessage>();
            var chatSnapshots = await Http.GetFromJsonAsync<List<ChatSnapshotDto>>($"api/tasks/{TaskId}/chat");
            if (chatSnapshots != null && chatSnapshots.Count > 0)
            {
                string? lastToolName = null;
                foreach (var snap in chatSnapshots)
                {
                    var msgType = snap.Role switch
                    {
                        "system" when snap.Content.StartsWith("[CONTINUATION CONTEXT]") => "compaction",
                        "system" => (string?)null,
                        "user" when snap.Content.StartsWith("Observation:") => "tool",
                        "user" when snap.Content.StartsWith("[USER INTERRUPTION]") => "user",
                        "user" when snap.Content.StartsWith("[CONTEXT SUMMARY") => "compaction",
                        "user" => "user",
                        "assistant" => "thought",
                        _ => null
                    };
                    if (msgType != null)
                    {
                        var content = snap.Content;
                        List<string>? filesAffected = null;
                        
                        if (msgType == "tool" && content.StartsWith("Observation: "))
                        {
                            content = content["Observation: ".Length..];
                            var tName = lastToolName ?? "Tool";
                            if (content.StartsWith("["))
                            {
                                var closeIdx = content.IndexOf(']');
                                if (closeIdx > 0)
                                {
                                    tName = content[1..closeIdx];
                                    content = content[(closeIdx + 1)..].TrimStart();
                                }
                            }
                            filesAffected = ExtractFileReferences(content);

                            var callingMsg = loadedChatMessages.LastOrDefault(m => m.Type == "tool" && m.Content.StartsWith($"Calling {tName}("));
                            var toolArgs = callingMsg?.ToolArgs;

                            var mdContent = FormatToolCompletedMarkdown(tName, toolArgs, content, 1000);
                            var fullMdContent = FormatToolCompletedMarkdown(tName, toolArgs, content, -1);
                            var flatContent = content.Replace("\n", "").Replace("\r", "");
                            var flatArgs = toolArgs?.Replace("\n", "").Replace("\r", "");

                            string shortContent;
                            string fullContentRaw;
                            if (!string.IsNullOrWhiteSpace(flatArgs) && flatArgs != "{}" && flatArgs != "None")
                            {
                                shortContent = $"{tName}({Truncate(flatArgs, 60)}) → {Truncate(flatContent, 150)}";
                                fullContentRaw = $"{tName}({flatArgs}) → {flatContent}";
                            }
                            else
                            {
                                shortContent = $"{tName} → {Truncate(flatContent, 200)}";
                                fullContentRaw = $"{tName} → {flatContent}";
                            }
                            
                            bool isTruncated = mdContent != fullMdContent || shortContent != fullContentRaw;
                            var toolMsg = new ChatMessage("tool", shortContent, snap.Timestamp, filesAffected, CachedHtml: Markdown.ToHtml(mdContent), AgentName: snap.AgentName, FullHtml: isTruncated ? Markdown.ToHtml(fullMdContent) : null, FullContent: isTruncated ? fullContentRaw : null, ToolArgs: toolArgs);

                            if (callingMsg != null)
                            {
                                var idx = loadedChatMessages.IndexOf(callingMsg);
                                loadedChatMessages[idx] = toolMsg;
                            }
                            else
                            {
                                loadedChatMessages.Add(toolMsg);
                            }
                            continue;
                        }
                        else if (msgType == "user")
                        {
                            if (content.StartsWith("[USER INTERRUPTION]: "))
                                content = content["[USER INTERRUPTION]: ".Length..];
                            else if (content.StartsWith("[USER INTERRUPTION]"))
                                content = content["[USER INTERRUPTION]".Length..];

                            // Handle legacy continuation prompts stored as user messages
                            if (content.Contains("## Previous Task State") && content.Contains("## New Instruction"))
                            {
                                var newInstructionIdx = content.IndexOf("## New Instruction");
                                if (newInstructionIdx >= 0)
                                {
                                    loadedChatMessages.Add(new("compaction", "Previous task context loaded (continuation)", snap.Timestamp, AgentName: snap.AgentName));
                                    content = content[(newInstructionIdx + "## New Instruction".Length)..].Trim();
                                    var boilerplateIdx = content.IndexOf("Continue working in the same workspace.");
                                    if (boilerplateIdx >= 0)
                                        content = content[..boilerplateIdx].Trim();
                                }
                            }

                            var remIndex = content.IndexOf("\n\n(Remember: You must continue executing");
                            if (remIndex >= 0)
                                content = content[..remIndex].TrimEnd();
                        }
                        else if (msgType == "thought")
                        {
                            var thought = ExtractSection(content, "Thought:", new[] { "Action:", "Plan:", "FinalAnswer:" });
                            if (!string.IsNullOrWhiteSpace(thought))
                                loadedChatMessages.Add(new("thought", thought, snap.Timestamp, AgentName: snap.AgentName));

                            var plan = ExtractSection(content, "Plan:", new[] { "Action:", "FinalAnswer:" });
                            if (!string.IsNullOrWhiteSpace(plan))
                            {
                                loadedChatMessages.Add(new("plan", plan, snap.Timestamp, CachedHtml: Markdown.ToHtml(plan), AgentName: snap.AgentName));
                            }

                            var actionPairs = ExtractAllActionPairs(content);
                            if (actionPairs.Count > 0)
                            {
                                foreach (var (actionName, args) in actionPairs)
                                {
                                    lastToolName = actionName;
                                    var mdContent = FormatToolStartingMarkdown(actionName, args, 1000);
                                    var fullMdContent = FormatToolStartingMarkdown(actionName, args, -1);
                                    // Make args single-line for the preview bubble
                                    var flatArgs = args.Replace("\n", "").Replace("\r", "");
                                    var shortContent = $"Calling {actionName}({Truncate(flatArgs, 100)})";
                                    var fullContentRaw = $"Calling {actionName}({flatArgs})";
                                    
                                    bool isTruncated = mdContent != fullMdContent || shortContent != fullContentRaw;
                                    loadedChatMessages.Add(new("tool", shortContent, snap.Timestamp, CachedHtml: Markdown.ToHtml(mdContent), AgentName: snap.AgentName, FullHtml: isTruncated ? Markdown.ToHtml(fullMdContent) : null, FullContent: isTruncated ? fullContentRaw : null, ToolArgs: args));
                                }
                            }

                            var finalAnswer = ExtractSection(content, "FinalAnswer:", Array.Empty<string>());
                            if (!string.IsNullOrWhiteSpace(finalAnswer))
                            {
                                var agentFiles = ExtractFileReferences(finalAnswer);
                                loadedChatMessages.Add(new("agent", finalAnswer, snap.Timestamp, agentFiles, Markdown.ToHtml(finalAnswer), AgentName: snap.AgentName));
                            }

                            bool foundStructure = !string.IsNullOrWhiteSpace(thought) || !string.IsNullOrWhiteSpace(plan) || actionPairs.Count > 0 || !string.IsNullOrWhiteSpace(finalAnswer);
                            if (foundStructure)
                                content = ""; // Prevent doubling if it was a structured block
                        }

                        if (!string.IsNullOrWhiteSpace(content) || msgType != "thought")
                        {
                            var cachedHtml = (msgType is "agent" or "user" or "plan" or "runtimePlan") ? Markdown.ToHtml(content) : null;
                            loadedChatMessages.Add(new(msgType, content, snap.Timestamp, filesAffected, CachedHtml: cachedHtml, AgentName: snap.AgentName, UserName: snap.UserName));
                        }
                    }
                }
            }

            if (taskDetails != null)
            {
                bool hasInitialPrompt = chatSnapshots?.Any(s => s.Role == "user" && s.Content == taskDetails.UserPrompt) ?? false;
                if (!hasInitialPrompt)
                {
                    loadedChatMessages.Add(new("user", taskDetails.UserPrompt, taskDetails.CreatedAt, UserName: taskDetails.CreatedByUserName));
                }
            }

            // Always attempt to load all plans for chat reconstruction
            try
            {
                var plans = await Http.GetFromJsonAsync<List<PlanDto>>($"api/tasks/{TaskId}/plans");
                if (plans != null && plans.Count > 0)
                {
                    // Remove any old plans from history parsed from assistant text to avoid duplicates
                    loadedChatMessages.RemoveAll(m => m.Type is "plan" or "planReview" or "runtimePlan");
                    
                    foreach (var p in plans)
                    {
                        if (p.Status == PlanStatus.RuntimeUpdate)
                        {
                            var planText = $"Runtime Plan v{p.Version}\n\n{p.PlanMarkdown}";
                            loadedChatMessages.Add(new("runtimePlan", planText, p.CreatedAt ?? DateTime.UtcNow, CachedHtml: Markdown.ToHtml(planText), AgentName: "Orchestrator"));
                        }
                        else
                        {
                            var planText = $"Implementation Plan v{p.Version}\n\n{p.PlanMarkdown}";
                            var isCurrentReview = (p == plans.Last() && taskDetails?.State is TaskState.PlanReview);
                            loadedChatMessages.Add(new(isCurrentReview ? "planReview" : "plan", planText, p.CreatedAt ?? DateTime.UtcNow, CachedHtml: Markdown.ToHtml(planText), AgentName: "Orchestrator"));
                        }
                    }

                    // For the review panel state, always point to the latest plan version
                    var latest = plans.OrderByDescending(p => p.Version).First();
                    if (taskDetails?.State is TaskState.PlanReview)
                    {
                        planReviewMarkdown = latest.PlanMarkdown;
                        planReviewQuestions = latest.OpenQuestions;
                        planReviewVersion = latest.Version;
                        isPlanReviewActive = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading plans: {ex.Message}");
            }

            // Ensure user prompts and plans have proper relative timestamps so plans never precede the initial prompt
            if (taskDetails != null)
            {
                var firstUser = loadedChatMessages.FirstOrDefault(m => m.Type == "user");
                if (firstUser != null)
                {
                    foreach (var planMsg in loadedChatMessages.Where(m => m.Type is "plan" or "planReview"))
                    {
                        if (planMsg.Time <= firstUser.Time)
                        {
                            planMsg.Time = firstUser.Time.AddMilliseconds(1);
                        }
                    }
                }
            }

            loadedChatMessages = loadedChatMessages.OrderBy(m => m.Time).ToList();

            if (taskDetails?.FinalResult != null)
            {
                bool alreadyPresent = loadedChatMessages.Any(m => m.Type == "agent" && !string.IsNullOrWhiteSpace(m.Content) &&
                    (m.Content == taskDetails.FinalResult ||
                     taskDetails.FinalResult.StartsWith(m.Content[..Math.Min(80, m.Content.Length)]) ||
                     m.Content.StartsWith(taskDetails.FinalResult[..Math.Min(80, taskDetails.FinalResult.Length)])));
                if (!alreadyPresent)
                {
                    var finalFiles = ExtractFileReferences(taskDetails.FinalResult);
                    loadedChatMessages.Add(new("agent", taskDetails.FinalResult, taskDetails.CompletedAt ?? DateTime.UtcNow, finalFiles, Markdown.ToHtml(taskDetails.FinalResult), AgentName: "Orchestrator"));
                }
            }

            lock (_chatLock)
            {
                chatMessages = MergeChatMessages(loadedChatMessages, chatMessages);
            }

            if (taskDetails?.State is TaskState.AwaitingApproval)
            {
                var awaitingLog = logs.LastOrDefault(l => l.Message.Contains("AwaitingApproval"));
                pendingApprovalDetail = awaitingLog?.Message ?? "Agent is waiting for your approval to proceed.";
            }

            await LoadWorkspaceFiles();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }

        try { await SignalR.ConnectAsync(); } catch (Exception ex) { Console.Error.WriteLine(ex); }
        await SignalR.SubscribeToTaskAsync(TaskId);
    }

    internal static List<AgentTaskLog> MergeLogs(List<AgentTaskLog> snapshotLogs, List<AgentTaskLog> liveLogs)
    {
        var existingIds = snapshotLogs.Select(l => l.Id).Where(id => id != Guid.Empty).ToHashSet();
        var newLiveLogs = liveLogs.Where(l => l.Id != Guid.Empty
            ? !existingIds.Contains(l.Id)
            : !snapshotLogs.Any(el => el.Timestamp == l.Timestamp && el.Message == l.Message)).ToList();
        var result = new List<AgentTaskLog>(snapshotLogs);
        result.AddRange(newLiveLogs);
        return result;
    }

    internal static List<ChatMessage> MergeChatMessages(List<ChatMessage> snapshotMessages, List<ChatMessage> liveMessages)
    {
        var result = new List<ChatMessage>(snapshotMessages);
        foreach (var liveMsg in liveMessages)
        {
            bool duplicate = result.Any(m =>
                m.Type == liveMsg.Type &&
                m.Content == liveMsg.Content &&
                Math.Abs((m.Time - liveMsg.Time).TotalSeconds) < 2);
            if (!duplicate)
            {
                result.Add(liveMsg);
            }
        }
        return result.OrderBy(m => m.Time).ToList();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await ScrollToBottom(force: true);
            if (dotNetRef != null)
            {
                try { await InteropService.RegisterLogScrollListenerAsync(".terminal", dotNetRef); } catch (Exception ex) { Console.Error.WriteLine(ex); }
            }
        }
    }

    // ── SignalR Handlers ───────────────────────────────────────────────

    private void OnTokenCountUpdated(TokenCountUpdatedEvent evt)
    {
        if (evt.TaskId != TaskId) return;
        if (evt.Scope == "Orchestrator")
        {
            currentTokenCountOrchestrator = evt.TokenCount;
            tokenLimitOrchestrator = evt.TokenLimit;
        }
        else
        {
            currentTokenCountSubagent = evt.TokenCount;
            tokenLimitSubagent = evt.TokenLimit;
        }
        QueueRender();
    }

    private void OnLog(AgentTaskLog log)
    {
        try
        {
            if (log.TaskId != TaskId) return;
            lock (_logLock)
            {
                logs.Add(log);
            }
            QueueRender();
        }
        catch (Exception) { /* Prevent async void crash */ }
    }

    private void OnThought(AgentThoughtEvent evt)
    {
        try
        {
            if (evt.TaskId != TaskId) return;
            var msgType = "thought";
            lock (_chatLock)
            {
                chatMessages.Add(new(msgType, evt.Thought, DateTime.Now, AgentName: evt.AgentName));
            }
            QueueRender();
        }
        catch (Exception) { /* Prevent async void crash */ }
    }

    private void OnPlan(PlanGeneratedEvent evt)
    {
        if (evt.TaskId != TaskId) return;
        pendingPlan = evt.PlanMarkdown;
        planRequiresApproval = evt.RequiresApproval;
        lock (_chatLock)
        {
            if (evt.RequiresApproval || taskDetails?.State is TaskState.Planning or TaskState.PlanReview or TaskState.Queued or TaskState.Draft)
            {
                chatMessages.RemoveAll(m => m.Type == "planReview");
                chatMessages.Add(new("plan", evt.PlanMarkdown, DateTime.Now, CachedHtml: Markdown.ToHtml(evt.PlanMarkdown), AgentName: "Orchestrator"));
            }
            else
            {
                // Runtime updates are added as separate runtimePlan bubbles
                var planText = $"Runtime Plan\n\n{evt.PlanMarkdown}";
                chatMessages.Add(new("runtimePlan", planText, DateTime.Now, CachedHtml: Markdown.ToHtml(planText), AgentName: "Orchestrator"));
            }
        }
        QueueRender();
    }

    private void OnTool(ToolExecutionEvent evt)
    {
        try
        {
            if (evt.TaskId != TaskId) return;
            List<string>? files = null;
            string content;
            string? cachedHtml = null;
            string? fullHtml = null;
            string? fullContent = null;
            string? toolArgs = evt.Args;

            if (evt.IsStarting)
            {
                var mdContent = FormatToolStartingMarkdown(evt.ToolName, evt.Args, 1000);
                var fullMdContent = FormatToolStartingMarkdown(evt.ToolName, evt.Args, -1);
                cachedHtml = Markdown.ToHtml(mdContent);
                var shortArgs = evt.Args?.Replace("\n", "").Replace("\r", "");
                content = $"Calling {evt.ToolName}({Truncate(shortArgs, 100)})";
                
                bool isTruncated = mdContent != fullMdContent || content != $"Calling {evt.ToolName}({shortArgs})";
                fullHtml = isTruncated ? Markdown.ToHtml(fullMdContent) : null;
                fullContent = isTruncated ? $"Calling {evt.ToolName}({shortArgs})" : null;
            }
            else
            {
                ChatMessage? callingMsg = null;
                lock (_chatLock)
                {
                    callingMsg = chatMessages.LastOrDefault(m => m.Type == "tool" && m.Content.StartsWith($"Calling {evt.ToolName}("));
                }

                if (string.IsNullOrWhiteSpace(toolArgs) && callingMsg?.ToolArgs != null)
                {
                    toolArgs = callingMsg.ToolArgs;
                }

                var mdContent = FormatToolCompletedMarkdown(evt.ToolName, toolArgs, evt.Result, 1000);
                var fullMdContent = FormatToolCompletedMarkdown(evt.ToolName, toolArgs, evt.Result, -1);
                cachedHtml = Markdown.ToHtml(mdContent);
                var shortResult = evt.Result?.Replace("\n", "").Replace("\r", "");
                var shortArgs = toolArgs?.Replace("\n", "").Replace("\r", "");

                if (!string.IsNullOrWhiteSpace(shortArgs) && shortArgs != "{}" && shortArgs != "None")
                {
                    content = $"{evt.ToolName}({Truncate(shortArgs, 60)}) → {Truncate(shortResult, 150)}";
                    fullContent = $"{evt.ToolName}({shortArgs}) → {shortResult}";
                }
                else
                {
                    content = $"{evt.ToolName} → {Truncate(shortResult, 200)}";
                    fullContent = $"{evt.ToolName} → {shortResult}";
                }
                
                bool isTruncated = mdContent != fullMdContent || content != fullContent;
                fullHtml = isTruncated ? Markdown.ToHtml(fullMdContent) : null;
                if (!isTruncated) fullContent = null;
                files = ExtractFileReferences(evt.Result);
                
                if (files != null && files.Count > 0)
                {
                    _ = InvokeAsync(async () =>
                    {
                        await LoadWorkspaceFiles();
                        if (!_isDisposed) StateHasChanged();
                    });
                }

                lock (_chatLock)
                {
                    if (callingMsg != null)
                    {
                        var index = chatMessages.IndexOf(callingMsg);
                        if (index >= 0)
                        {
                            chatMessages[index] = new("tool", content, DateTime.Now, files, CachedHtml: cachedHtml, AgentName: evt.AgentName, FullHtml: fullHtml, FullContent: fullContent, ToolArgs: toolArgs);
                            QueueRender();
                            return;
                        }
                    }
                }
            }

            lock (_chatLock)
            {
                chatMessages.Add(new("tool", content, DateTime.Now, files, CachedHtml: cachedHtml, AgentName: evt.AgentName, FullHtml: fullHtml, FullContent: fullContent, ToolArgs: toolArgs));
            }
            QueueRender();
        }
        catch (Exception) { /* Prevent async void crash */ }
    }

    private static readonly System.Text.RegularExpressions.Regex FileRefRegex =
        new(@"(?:/app/workdir/(?<name>[\w\-\.\s\(\)]+\.\w+))|\b(?<name>[\w\-\.]+\.(?:md|txt|json|csv|cs|html|css|js|py|yml|yaml|xml|log|sh|ps1|xlsx|pdf|png|jpg))", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static List<string>? ExtractFileReferences(string? result)
    {
        if (string.IsNullOrEmpty(result)) return null;
        var matches = FileRefRegex.Matches(result);
        if (matches.Count == 0) return null;
        var files = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var name = m.Groups["name"].Value;
            if (!string.IsNullOrEmpty(name) && !files.Contains(name)) files.Add(name);
        }
        return files.Count > 0 ? files : null;
    }

    private void OnActivity(AgentActivityEvent evt)
    {
        try
        {
            if (evt.TaskId != TaskId) return;
            activityLabel = evt.ActivityLabel;
            QueueRender();
        }
        catch (Exception) { /* Prevent async void crash */ }
    }

    private async void OnStateChanged(TaskStateChangedEvent evt)
    {
        try
        {
            if (evt.TaskId != TaskId) return;
            if (taskDetails != null) taskDetails.State = evt.NewState;

            if (evt.NewState == TaskState.AwaitingApproval)
            {
                pendingApprovalDetail = evt.Detail;
                showRejectInput = false;
            }
            else
            {
                pendingApprovalDetail = null;
            }

            if (evt.NewState != TaskState.PlanReview)
            {
                isPlanReviewActive = false;
                showPlanEditor = false;
                showPlanRejectInput = false;
                showPlanQuestionInput = false;
            }

            if (!string.IsNullOrEmpty(evt.Detail) && evt.NewState is TaskState.Completed or TaskState.Failed)
            {
                var agentFiles = ExtractFileReferences(evt.Detail);
                lock (_chatLock)
                {
                    bool alreadyPresent = chatMessages.Any(m => m.Type == "agent" && !string.IsNullOrWhiteSpace(m.Content) &&
                        (m.Content == evt.Detail || evt.Detail.Contains(m.Content) || m.Content.Contains(evt.Detail)));
                    if (!alreadyPresent)
                    {
                        chatMessages.Add(new("agent", evt.Detail, DateTime.Now, agentFiles, Markdown.ToHtml(evt.Detail)));
                    }
                }
                try
                {
                    taskDetails = await Http.GetFromJsonAsync<TaskSession>($"api/tasks/{TaskId}");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }

                if (agentFiles != null && agentFiles.Count > 0)
                {
                    _ = InvokeAsync(async () =>
                    {
                        await LoadWorkspaceFiles();
                        if (!_isDisposed) StateHasChanged();
                    });
                }
            }
            QueueRender();
        }
        catch (Exception) { /* Prevent async void crash */ }
    }

    private void OnPlanRevision(PlanRevisionEvent evt)
    {
        try
        {
            if (evt.TaskId != TaskId) return;
            planReviewMarkdown = evt.PlanMarkdown;
            planReviewVersion = evt.Version;
            isPlanReviewActive = true;
            var planText = $"Implementation Plan v{evt.Version}\n\n{evt.PlanMarkdown}";
            lock (_chatLock)
            {
                chatMessages.RemoveAll(m => m.Type == "planReview");
                chatMessages.Add(new("planReview", planText, DateTime.Now, CachedHtml: Markdown.ToHtml(planText), AgentName: "Orchestrator"));
            }
            QueueRender();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private void OnPlanReviewRequested(PlanReviewRequestedEvent evt)
    {
        try
        {
            if (evt.TaskId != TaskId) return;
            planReviewMarkdown = evt.PlanMarkdown;
            planReviewQuestions = evt.OpenQuestions;
            planReviewVersion = evt.Version;
            isPlanReviewActive = true;
            if (taskDetails != null) taskDetails.State = TaskState.PlanReview;
            showPlanEditor = false;
            showPlanRejectInput = false;
            showPlanQuestionInput = false;
            planEditorContent = evt.PlanMarkdown;
            var reviewText = $"Implementation Plan v{evt.Version}\n\n{evt.PlanMarkdown}";
            lock (_chatLock)
            {
                chatMessages.RemoveAll(m => m.Type == "planReview");
                chatMessages.Add(new("planReview", reviewText, DateTime.Now, CachedHtml: Markdown.ToHtml(reviewText), AgentName: "Orchestrator"));
            }
            QueueRender();
        }
        catch (Exception) { /* Prevent async void crash */ }
    }

    private void OnContextCompaction(ContextCompactionEvent evt)
    {
        try
        {
            if (evt.TaskId != TaskId) return;
            lock (_chatLock)
            {
                chatMessages.Add(new("compaction",
                    $"Context compacted: {evt.OriginalMessageCount} → {evt.CompactedMessageCount} messages",
                    DateTime.Now));
            }
            QueueRender();
        }
        catch (Exception) { /* Prevent async void crash */ }
    }

    // ── User Actions ───────────────────────────────────────────────────

    /// <summary>Called by the isolated ChatInput component when user submits a message.</summary>
    private async Task HandleChatSubmit(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var inputLower = input.Trim().ToLowerInvariant();
        if (taskDetails?.State == TaskState.AwaitingApproval)
        {
            if (inputLower is "ok" or "yes" or "proceed" or "approve" or "y" or "go ahead")
            {
                await ApproveStep();
                return;
            }
            if (inputLower is "no" or "stop" or "cancel" or "reject" or "n")
            {
                rejectFeedback = "User rejected step via chat.";
                await RejectStep();
                return;
            }
            if (inputLower.StartsWith("reject:") || inputLower.StartsWith("reject ") || inputLower.StartsWith("no,") || inputLower.StartsWith("no:"))
            {
                var sepIdx = input.IndexOfAny(new[] { ':', ' ' });
                var feedbackPart = sepIdx >= 0 ? input.Substring(sepIdx + 1).Trim() : input;
                rejectFeedback = string.IsNullOrWhiteSpace(feedbackPart) ? "User rejected step via chat." : feedbackPart;
                await RejectStep();
                return;
            }
            lock (_chatLock)
            {
                chatMessages.Add(new("user", input, DateTime.Now, UserName: currentUserName));
                chatMessages.Add(new("tool", "Action requires explicit approval: please type 'yes'/'approve' to proceed, or 'no'/'reject <reason>' to reject this step.", DateTime.Now));
            }
            return;
        }

        if (isPlanReviewActive || taskDetails?.State == TaskState.PlanReview)
        {
            if (inputLower is "ok" or "yes" or "approve" or "y" or "lgtm" or "looks good")
            {
                await ApprovePlanAction();
                return;
            }
            await SignalR.AskPlanQuestionAsync(TaskId, input);
            lock (_chatLock)
            {
                chatMessages.Add(new("user", input, DateTime.Now, UserName: currentUserName));
            }
            return;
        }

        if (taskDetails?.State is TaskState.AwaitingInput)
        {
            if (taskDetails != null) taskDetails.State = TaskState.InProgress;
            lock (_chatLock)
            {
                chatMessages.Add(new("user", input, DateTime.Now, UserName: currentUserName));
            }
            var deliveredInput = await SignalR.SendInterruptionAsync(TaskId, input);
            if (!deliveredInput)
            {
                lock (_chatLock)
                {
                    chatMessages.Add(new("tool", "Note: Input could not be delivered (task is not currently active).", DateTime.Now));
                }
            }
            return;
        }

        lock (_chatLock)
        {
            chatMessages.Add(new("user", input, DateTime.Now, UserName: currentUserName));
        }
        var delivered = await SignalR.SendInterruptionAsync(TaskId, input);
        if (!delivered)
        {
            lock (_chatLock)
            {
                chatMessages.Add(new("tool", "Note: Interruption could not be delivered (task is not currently active).", DateTime.Now));
            }
        }
    }

    private async Task HandleApprovalKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            await SendApprovalMessage();
        }
    }

    private async Task SendApprovalMessage()
    {
        if (string.IsNullOrWhiteSpace(approvalInput)) return;
        await HandleChatSubmit(approvalInput);
        approvalInput = "";
    }

    /// <summary>Called by the isolated ChatInput component for follow-up on completed/failed tasks.</summary>
    private async Task HandleFollowUp(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var response = await Http.PostAsJsonAsync($"api/tasks/{TaskId}/proceed", input);
        if (response.IsSuccessStatusCode)
        {
            if (taskDetails != null) { taskDetails.State = TaskState.Draft; taskDetails.FinalResult = null; }
            lock (_chatLock)
            {
                chatMessages.Add(new("user", input, DateTime.Now, UserName: currentUserName));
            }
            activityLabel = "Resuming task...";
            StateHasChanged();
        }
    }

    private async Task ApproveStep()
    {
        await SignalR.ApproveStepAsync(TaskId);
        planRequiresApproval = false;
        pendingApprovalDetail = null;
        showRejectInput = false;
        lock (_chatLock)
        {
            chatMessages.Add(new("user", "Step approved", DateTime.Now, UserName: currentUserName));
        }
    }

    private void ToggleRejectInput() => showRejectInput = !showRejectInput;

    private async Task HandleRejectKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await RejectStep();
    }

    private async Task RejectStep()
    {
        var feedback = string.IsNullOrWhiteSpace(rejectFeedback) ? "User rejected the step." : rejectFeedback;
        await SignalR.RejectStepAsync(TaskId, feedback);
        planRequiresApproval = false;
        pendingApprovalDetail = null;
        showRejectInput = false;
        lock (_chatLock)
        {
            chatMessages.Add(new("user", $"Rejected: {feedback}", DateTime.Now, UserName: currentUserName));
        }
        rejectFeedback = "";
    }

    // ── Plan Review Actions ────────────────────────────────────────────

    private async Task ApprovePlanAction()
    {
        await SignalR.ApprovePlanAsync(TaskId);
        isPlanReviewActive = false;
        lock (_chatLock)
        {
            var pendingReview = chatMessages.LastOrDefault(m => m.Type == "planReview");
            if (pendingReview != null)
            {
                var idx = chatMessages.IndexOf(pendingReview);
                chatMessages[idx] = pendingReview with { Type = "plan" };
            }
            chatMessages.Add(new("user", "Plan approved", DateTime.Now, UserName: currentUserName));
        }
    }

    private void TogglePlanEditor()
    {
        showPlanEditor = !showPlanEditor;
        if (showPlanEditor)
        {
            planEditorContent = planReviewMarkdown ?? "";
            showPlanRejectInput = false;
            showPlanQuestionInput = false;
        }
    }

    private async Task SubmitModifiedPlan()
    {
        await SignalR.ModifyPlanAsync(TaskId, planEditorContent);
        showPlanEditor = false;
        lock (_chatLock)
        {
            chatMessages.Add(new("user", "Plan modified and resubmitted", DateTime.Now, UserName: currentUserName));
        }
    }

    private void TogglePlanRejectInput()
    {
        showPlanRejectInput = !showPlanRejectInput;
        if (showPlanRejectInput) { showPlanEditor = false; showPlanQuestionInput = false; }
    }

    private async Task SubmitPlanRejection()
    {
        var feedback = string.IsNullOrWhiteSpace(planRejectFeedback) ? "Plan rejected." : planRejectFeedback;
        await SignalR.RejectPlanAsync(TaskId, feedback);
        showPlanRejectInput = false;
        lock (_chatLock)
        {
            chatMessages.Add(new("user", $"Plan rejected: {feedback}", DateTime.Now, UserName: currentUserName));
        }
        planRejectFeedback = "";
    }

    private void TogglePlanQuestionInput()
    {
        showPlanQuestionInput = !showPlanQuestionInput;
        if (showPlanQuestionInput) { showPlanEditor = false; showPlanRejectInput = false; }
    }

    private async Task SubmitPlanQuestion()
    {
        if (string.IsNullOrWhiteSpace(planQuestionText)) return;
        await SignalR.AskPlanQuestionAsync(TaskId, planQuestionText);
        lock (_chatLock)
        {
            chatMessages.Add(new("user", planQuestionText, DateTime.Now, UserName: currentUserName));
        }
        planQuestionText = "";
        showPlanQuestionInput = false;
    }

    private async Task PauseTask() => await Http.PostAsync($"api/tasks/{TaskId}/pause", null);
    private async Task ResumeTask() => await Http.PostAsync($"api/tasks/{TaskId}/resume", null);
    private async Task DeleteTask() => await Http.DeleteAsync($"api/tasks/{TaskId}");

    private async Task RestartTask()
    {
        var response = await Http.PostAsync($"api/tasks/{TaskId}/restart", null);
        if (response.IsSuccessStatusCode)
        {
            lock (_logLock) { logs.Clear(); }
            lock (_chatLock) { chatMessages.Clear(); }
            activityLabel = "";
            pendingPlan = null;
            planRequiresApproval = false;
            isPlanReviewActive = false;
            planReviewMarkdown = null;

            await SignalR.SubscribeToTaskAsync(TaskId);
            taskDetails = await Http.GetFromJsonAsync<TaskSession>($"api/tasks/{TaskId}");
            if (taskDetails != null)
            {
                taskDetails.FinalResult = null; // Ensure stale result is never shown
                lock (_chatLock)
                {
                    chatMessages.Add(new("user", taskDetails.UserPrompt, taskDetails.CreatedAt, UserName: taskDetails.CreatedByUserName));
                }
            }
            StateHasChanged();
        }
    }

    // ProceedTask logic moved into HandleFollowUp (called by ChatInput component)

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task SetActiveTab(string tab)
    {
        activeTab = tab;
        StateHasChanged();
        await ScrollToBottom(force: true);
    }

    private async Task ScrollToBottom(bool force = false)
    {
        if (!force && !followAgent) return;

        // Small delay to ensure Blazor finished rendering
        await Task.Delay(100);
        if (activeTab is "chat" or "split")
            await InteropService.ScrollToBottomAsync(".chat-messages");
        if (activeTab is "console" or "split")
            await InteropService.ScrollToBottomAsync(".terminal");
            
        try { await InteropService.AttachCopyButtonsAsync(".chat-messages"); } catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private async Task ToggleExpansion(ChatMessage msg)
    {
        msg.IsExpanded = !msg.IsExpanded;
        StateHasChanged();
        await Task.Delay(50);
        try { await InteropService.AttachCopyButtonsAsync(".chat-messages"); } catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private string GetInputPlaceholder() => taskDetails?.State switch
    {
        TaskState.Completed or TaskState.Failed => "Follow up with another instruction...",
        TaskState.AwaitingApproval => "Type 'Ok' to approve, or provide rejection feedback...",
        TaskState.Queued => "Waiting for execution slot...",
        TaskState.PlanReview => "Type 'approve' to accept the plan, or ask a question...",
        _ => "Send instruction to agent..."
    };

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private void QueueRender()
    {
        if (Interlocked.CompareExchange(ref _isStateDirty, 1, 0) != 0) return;
        debounceTimer?.Change(100, Timeout.Infinite);
    }

    private int _lastRenderedChatCount;
    private int _lastRenderedLogCount;
    private bool _isDisposed;

    private async void OnDebounceRender(object? state)
    {
        if (_isDisposed) return;

        try
        {
            Interlocked.Exchange(ref _isStateDirty, 0);
            await InvokeAsync(StateHasChanged);

            int currentChatCount;
            lock (_chatLock)
            {
                currentChatCount = chatMessages.Count;
            }

            int currentLogCount;
            lock (_logLock)
            {
                currentLogCount = logs.Count;
            }

            var chatChanged = currentChatCount != _lastRenderedChatCount;
            var logsChanged = currentLogCount != _lastRenderedLogCount;
            _lastRenderedChatCount = currentChatCount;
            _lastRenderedLogCount = currentLogCount;

            if (chatChanged || logsChanged)
            {
                await ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            // Expected lifecycle exception when component is unmounted or JS interop disconnects
            Console.Error.WriteLine($"[LiveExecution] Safe catch in debounced render: {ex.Message}");
        }
    }

    [JSInvokable]
    public async Task OnScrollToTop()
    {
        if (isLoadingOlderLogs || !oldestLogTimestamp.HasValue || (activeTab != "console" && activeTab != "split"))
            return;

        isLoadingOlderLogs = true;
        StateHasChanged(); 

        try
        {
            var oldScrollHeight = await InteropService.GetScrollHeightAsync(".terminal");
            
            var batch = await Http.GetFromJsonAsync<List<AgentTaskLog>>(
                $"api/tasks/{TaskId}/logs?limit=200&before={oldestLogTimestamp.Value:O}");

            if (batch != null && batch.Any())
            {
                oldestLogTimestamp = batch.First().Timestamp;
                lock (_logLock)
                {
                    logs.InsertRange(0, batch);
                }
                await InvokeAsync(() => { if (!_isDisposed) StateHasChanged(); });
                
                try { await InteropService.PreserveScrollAsync(".terminal", oldScrollHeight); } catch (Exception ex) { Console.Error.WriteLine(ex); }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            isLoadingOlderLogs = false;
            await InvokeAsync(() => { if (!_isDisposed) StateHasChanged(); });
        }
    }

    // ── Workspace File Methods ─────────────────────────────────────────

    private async Task ActivateFilesTab()
    {
        activeTab = "files";
        await LoadWorkspaceFiles();
    }

    private async Task LoadWorkspaceFiles()
    {
        isLoadingFiles = true;
        try
        {
            var files = await Http.GetFromJsonAsync<List<WorkspaceFileDto>>($"api/tasks/{TaskId}/workspace");
            workspaceFiles = files ?? new();
        }
        catch { workspaceFiles = new(); }
        finally { isLoadingFiles = false; }
    }

    private Task ViewWorkspaceFile(string fileName)
    {
        viewingFileName = fileName;
        showFileViewer = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task DownloadWorkspaceFile(string fileName)
    {
        try
        {
            using var response = await Http.GetAsync($"api/tasks/{TaskId}/workspace/{Uri.EscapeDataString(fileName)}", HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
            {
                var contentStream = await response.Content.ReadAsStreamAsync();
                using var streamRef = new DotNetStreamReference(contentStream);
                await InteropService.DownloadFileFromStreamAsync(fileName, streamRef);
            }
            else
            {
                Console.WriteLine($"Failed to download file. Status code: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading file: {ex.Message}");
        }
    }

    private async Task DeleteWorkspaceFile(string fileName)
    {
        try
        {
            bool confirmed = await InteropService.ConfirmAsync($"Are you sure you want to delete '{fileName}'?");
            if (!confirmed) return;

            await Http.DeleteAsync($"api/tasks/{TaskId}/workspace/{Uri.EscapeDataString(fileName)}");
            await LoadWorkspaceFiles();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private async Task LoadUploadLimit()
    {
        try
        {
            var config = await Http.GetFromJsonAsync<Dictionary<string, long>>("api/config/upload-limit");
            if (config != null && config.TryGetValue("maxUploadSizeMb", out var limit))
                maxUploadSizeMb = limit;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private async Task HandleFileUpload(InputFileChangeEventArgs e)
    {
        IReadOnlyList<IBrowserFile> files;
        try
        {
            files = e.GetMultipleFiles(10);
        }
        catch (InvalidOperationException)
        {
            await InteropService.AlertAsync("A maximum of 10 files can be uploaded at once.");
            return;
        }

        using var content = new MultipartFormDataContent();
        long maxAllowedSize = maxUploadSizeMb * 1024 * 1024;
        
        foreach (var file in files)
        {
            if (file.Size > maxAllowedSize)
            {
                await InteropService.AlertAsync($"File '{file.Name}' exceeds the {maxUploadSizeMb}MB limit.");
                continue;
            }

            try
            {
                var stream = file.OpenReadStream(maxAllowedSize);
                var streamContent = new StreamContent(stream);
                content.Add(streamContent, "files", file.Name);
            }
            catch (Exception ex)
            {
                await InteropService.AlertAsync($"Error preparing '{file.Name}': {ex.Message}");
            }
        }

        if (!content.Any()) return;

        try
        {
            await Http.PostAsync($"api/tasks/{TaskId}/workspace/upload", content);
            await LoadWorkspaceFiles();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private void CloseFileViewer()
    {
        showFileViewer = false;
        viewingFileName = "";
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    private static string FormatTokenCount(int count)
    {
        if (count >= 1_000_000)
            return (count / 1_000_000.0).ToString(count % 1_000_000 == 0 ? "F0" : "F1", System.Globalization.CultureInfo.InvariantCulture) + "M";
        if (count >= 1_000)
            return (count / 1_000.0).ToString(count % 1_000 == 0 ? "F0" : "F1", System.Globalization.CultureInfo.InvariantCulture) + "k";
        return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnWorkspaceFileChanged(WorkspaceFileChangedEvent evt)
    {
        if (evt.TaskId != TaskId) return;

        if (evt.FileName == "_task_state.md" || evt.FileName == "_walkthrough.md")
        {
            lock (_chatLock)
            {
                var lastToolMsg = chatMessages.LastOrDefault(m => m.Type == "tool" && !m.Content.StartsWith("Calling "));
                if (lastToolMsg != null)
                {
                    var files = lastToolMsg.FilesAffected != null 
                        ? new List<string>(lastToolMsg.FilesAffected) 
                        : new List<string>();
                    if (!files.Contains(evt.FileName))
                    {
                        files.Add(evt.FileName);
                        var index = chatMessages.IndexOf(lastToolMsg);
                        chatMessages[index] = lastToolMsg with { FilesAffected = files };
                    }
                }
                else
                {
                    chatMessages.Add(new("tool", $"System updated {evt.FileName}", DateTime.Now, new List<string> { evt.FileName }));
                }
            }
            QueueRender();
        }

        _ = InvokeAsync(async () =>
        {
            await LoadWorkspaceFiles();
            if (!_isDisposed) StateHasChanged();
        });
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        debounceTimer?.Dispose();
        dotNetRef?.Dispose();
        SignalR.OnLogReceived -= OnLog;
        SignalR.OnAgentThought -= OnThought;
        SignalR.OnPlanGenerated -= OnPlan;
        SignalR.OnToolExecution -= OnTool;
        SignalR.OnAgentActivity -= OnActivity;
        SignalR.OnTaskStateChanged -= OnStateChanged;
        SignalR.OnPlanReviewRequested -= OnPlanReviewRequested;
        SignalR.OnPlanRevision -= OnPlanRevision;
        SignalR.OnContextCompaction -= OnContextCompaction;
        SignalR.OnWorkspaceFileChanged -= OnWorkspaceFileChanged;
        SignalR.OnConnectionStateChanged -= OnConnectionStateChanged;
        SignalR.OnTokenCountUpdated -= OnTokenCountUpdated;
        if (_loadedTaskId != Guid.Empty)
        {
            await SignalR.UnsubscribeFromTaskAsync(_loadedTaskId);
        }
        else
        {
            await SignalR.UnsubscribeFromTaskAsync(TaskId);
        }
    }

    private async void OnConnectionStateChanged(Microsoft.AspNetCore.SignalR.Client.HubConnectionState state)
    {
        try
        {
            if (state == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
            {
                var targetId = _loadedTaskId != Guid.Empty ? _loadedTaskId : TaskId;
                if (targetId != Guid.Empty)
                {
                    await SignalR.SubscribeToTaskAsync(targetId);
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    private static int FindStrictTokenIndex(string content, string token, int startFrom = 0)
    {
        int idx = startFrom;
        while (true)
        {
            idx = content.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            bool isStartOfLine = idx == 0 || content[idx - 1] == '\n' || content[idx - 1] == '\r';
            bool isValidPrefix = false;
            if (!isStartOfLine)
            {
                int lineStart = idx - 1;
                while (lineStart > 0 && content[lineStart] != '\n' && content[lineStart] != '\r')
                    lineStart--;
                int prefixStart = (lineStart > 0 || content[lineStart] == '\n' || content[lineStart] == '\r') ? lineStart + 1 : lineStart;
                string prefix = content[prefixStart..idx].Trim();
                isValidPrefix = prefix.All(c => c is '#' or '*' or '-' or '_' or ' ' or '\t');
            }

            if (isStartOfLine || isValidPrefix)
            {
                return idx;
            }

            idx += token.Length;
        }
    }

    private static string? ExtractSection(string content, string token, string[] stopTokens)
    {
        int idx = FindStrictTokenIndex(content, token);
        if (idx < 0) return null;

        int start = idx + token.Length;
        int end = content.Length;

        foreach (var stop in stopTokens)
        {
            int stopIdx = FindStrictTokenIndex(content, stop, start);
            if (stopIdx >= start && stopIdx < end)
                end = stopIdx;
        }

        var result = content[start..end].Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static List<(string ActionName, string ActionInput)> ExtractAllActionPairs(string content)
    {
        var pairs = new List<(string ActionName, string ActionInput)>();
        int searchFrom = 0;

        int finalAnswerIdx = FindStrictTokenIndex(content, "FinalAnswer:");
        int effectiveEnd = finalAnswerIdx >= 0 ? finalAnswerIdx : content.Length;

        while (searchFrom < effectiveEnd)
        {
            int actionIdx = FindStrictTokenIndex(content, "Action:", searchFrom);
            if (actionIdx < 0 || actionIdx >= effectiveEnd) break;

            int actionContentStart = actionIdx + "Action:".Length;

            int nextActionIdx = FindStrictTokenIndex(content, "Action:", actionContentStart);
            int nextFinalIdx = FindStrictTokenIndex(content, "FinalAnswer:", actionContentStart);
            int nextActionOrFinal = effectiveEnd;
            if (nextActionIdx >= 0 && nextActionIdx < nextActionOrFinal) nextActionOrFinal = nextActionIdx;
            if (nextFinalIdx >= 0 && nextFinalIdx < nextActionOrFinal) nextActionOrFinal = nextFinalIdx;

            int inputIdx = FindStrictTokenIndex(content, "ActionInput:", actionContentStart);

            string actionName;
            string actionInput;
            int nextSearchStart;

            if (inputIdx >= 0 && inputIdx < nextActionOrFinal)
            {
                // Action has a corresponding ActionInput: block
                actionName = content[actionContentStart..inputIdx].Trim();
                int inputContentStart = inputIdx + "ActionInput:".Length;
                actionInput = content[inputContentStart..nextActionOrFinal].Trim();
                nextSearchStart = nextActionOrFinal;
            }
            else
            {
                // Action is parameterless (no ActionInput: block before next Action/FinalAnswer)
                actionName = content[actionContentStart..nextActionOrFinal].Trim();
                actionInput = "{}";
                nextSearchStart = nextActionOrFinal;
            }

            actionName = actionName.Trim('`', '*', '_', ' ', '\r', '\n');
            if (!string.IsNullOrWhiteSpace(actionName) && !actionName.Equals("None", StringComparison.OrdinalIgnoreCase) && !actionName.StartsWith("Input:", StringComparison.OrdinalIgnoreCase))
            {
                pairs.Add((actionName, string.IsNullOrWhiteSpace(actionInput) ? "{}" : actionInput));
            }

            searchFrom = nextSearchStart;
        }

        return pairs;
    }

    private static string TryPrettyPrintJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return rawJson;
        var trimmed = rawJson.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return trimmed;
            }
        }
        return trimmed;
    }

    private static string FormatToolStartingMarkdown(string toolName, string? args, int truncateLength = 1000)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**Calling `{toolName}`**");
        if (!string.IsNullOrWhiteSpace(args) && args != "{}" && args != "None")
        {
            var pretty = TryPrettyPrintJson(args);
            var display = (truncateLength > 0 && pretty.Length > truncateLength) ? Truncate(pretty, truncateLength) : pretty;
            sb.AppendLine();
            sb.AppendLine("**Parameters:**");
            sb.AppendLine("```json");
            sb.AppendLine(display);
            sb.AppendLine("```");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatToolCompletedMarkdown(string toolName, string? args, string? result, int truncateLength = 1000)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**`{toolName}`**");
        if (!string.IsNullOrWhiteSpace(args) && args != "{}" && args != "None")
        {
            var pretty = TryPrettyPrintJson(args);
            var display = (truncateLength > 0 && pretty.Length > truncateLength) ? Truncate(pretty, truncateLength) : pretty;
            sb.AppendLine();
            sb.AppendLine("**Parameters:**");
            sb.AppendLine("```json");
            sb.AppendLine(display);
            sb.AppendLine("```");
        }
        if (!string.IsNullOrWhiteSpace(result))
        {
            var display = (truncateLength > 0 && result.Length > truncateLength) ? Truncate(result, truncateLength) : result;
            sb.AppendLine();
            sb.AppendLine("**Result:**");
            sb.AppendLine("```");
            sb.AppendLine(display);
            sb.AppendLine("```");
        }
        return sb.ToString().TrimEnd();
    }
}
