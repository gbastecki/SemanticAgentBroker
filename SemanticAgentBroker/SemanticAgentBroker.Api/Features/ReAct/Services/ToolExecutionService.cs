using Microsoft.SemanticKernel;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

/// <summary>
/// Handles tool invocation, argument resolution, auto-save of large outputs,
/// and structured state recording.
/// </summary>
public class ToolExecutionService
{
    private readonly AgentTaskStore _taskStore;
    private readonly ITaskStateManager _taskStateManager;
    private readonly AiProviderOptions _aiOptions;
    private readonly AgentSystemOptions _sysOptions;
    private readonly ILogger<ToolExecutionService> _logger;
    private readonly SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager? _lockManager;

    public ToolExecutionService(
        AgentTaskStore taskStore,
        ITaskStateManager taskStateManager,
        AiProviderOptions aiOptions,
        AgentSystemOptions sysOptions,
        ILogger<ToolExecutionService> logger,
        SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager? lockManager = null)
    {
        _taskStore = taskStore;
        _taskStateManager = taskStateManager;
        _aiOptions = aiOptions;
        _sysOptions = sysOptions;
        _logger = logger;
        _lockManager = lockManager;
    }

    public async Task<string> ExecuteToolAsync(
        Guid sessionId, Kernel kernel, string actionName, string actionInputStr,
        int step, bool debugMode, bool structuredStateEnabled, CancellationToken ct,
        string agentName = "Orchestrator")
    {
        actionName = actionName.Trim().Trim('`', '*', '_', '"', '\'').Trim();

        try
        {
            await _taskStore.BroadcastToolExecutionAsync(sessionId, actionName, actionInputStr, null, isStarting: true, agentName);
            await _taskStore.BroadcastActivityAsync(sessionId, $"Executing {actionName}...", agentName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast tool starting activity for {ActionName} in session {SessionId}", actionName, sessionId);
        }

        var pluginFn = actionName.Split('-', 2);
        if (pluginFn.Length != 2)
        {
            var err = $"Invalid action format '{actionName}'. Use PluginName-FunctionName.";
            string broadcastErr = err;
            if (structuredStateEnabled) broadcastErr += "\n[/app/workdir/_task_state.md]";
            await _taskStore.BroadcastToolExecutionAsync(sessionId, actionName, actionInputStr, broadcastErr, isStarting: false, agentName);
            return err;
        }

        if (!kernel.Plugins.TryGetPlugin(pluginFn[0], out var plugin))
        {
            var availablePlugins = kernel.Plugins.Count > 0
                ? string.Join(", ", kernel.Plugins.Select(p => p.Name))
                : "(none)";
            var err = $"Tool execution failed: Plugin {pluginFn[0]} not found. Available plugins: {availablePlugins}";
            string broadcastErr = err;
            if (structuredStateEnabled) broadcastErr += "\n[/app/workdir/_task_state.md]";
            await _taskStore.BroadcastToolExecutionAsync(sessionId, actionName, actionInputStr, broadcastErr, isStarting: false, agentName);
            return err;
        }

        if (!plugin.TryGetFunction(pluginFn[1], out var function))
        {
            var availableFns = plugin.Count() > 0
                ? string.Join(", ", plugin.Select(f => f.Name))
                : "(none)";
            var err = $"Tool execution failed: Function {pluginFn[1]} not found in plugin '{pluginFn[0]}'. Available functions: {availableFns}";
            string broadcastErr = err;
            if (structuredStateEnabled) broadcastErr += "\n[/app/workdir/_task_state.md]";
            await _taskStore.BroadcastToolExecutionAsync(sessionId, actionName, actionInputStr, broadcastErr, isStarting: false, agentName);
            return err;
        }

        try
        {
            var args = new KernelArguments();

            if (debugMode)
                await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                    $"[DEBUG] Tool call: {actionName}\n  Raw ActionInput: \"{actionInputStr}\"");

            Dictionary<string, object?>? parsedArgs = null;
            string? parseWarning = null;

            if (!string.IsNullOrWhiteSpace(actionInputStr) && actionInputStr != "{}")
            {
                // Step 1: Try fast-path parse
                try
                {
                    parsedArgs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(actionInputStr);
                }
                catch (System.Text.Json.JsonException)
                {
                    // Step 2: Attempt JSON sanitization and retry
                    var (sanitized, didSanitize, warning) = JsonSanitizer.TrySanitize(actionInputStr);
                    
                    if (didSanitize)
                    {
                        if (debugMode)
                            await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                                $"[DEBUG] JSON sanitizer applied: {warning}");

                        try
                        {
                            parsedArgs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(sanitized);
                            
                            _logger.LogInformation(
                                "JSON arguments sanitized for {ActionName} in session {SessionId}: {Warning}",
                                actionName, sessionId, warning);
                            
                            await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.Warning,
                                $"[SANITIZED] Tool arguments were auto-repaired: {warning}");
                        }
                        catch (System.Text.Json.JsonException ex2)
                        {
                            // Sanitization didn't fix it
                            parseWarning = $"Original error and sanitization failed. {ex2.Message}";
                        }
                    }
                    else
                    {
                        parseWarning = "JSON parsing failed and no sanitization was applicable.";
                    }
                }
            }

            if (parsedArgs == null && !string.IsNullOrEmpty(parseWarning))
            {
                var errorResponse = $"Error: Invalid JSON format in ActionInput. {parseWarning}. Please ensure all strings are properly escaped (use \\n for newlines, prefer single quotes in scripts).";
                
                await _taskStore.LogEventAsync(sessionId, EventTypes.ToolError, LogLevelNames.Warning, errorResponse);
                string broadcastErr = errorResponse;
                if (structuredStateEnabled) broadcastErr += "\n[/app/workdir/_task_state.md]";
                await _taskStore.BroadcastToolExecutionAsync(sessionId, actionName, actionInputStr, broadcastErr, isStarting: false, agentName);
                
                return errorResponse;
            }

            if (parsedArgs != null)
            {
                var workspacePath = GetWorkspacePath(sessionId);
                foreach (var kvp in parsedArgs)
                {
                    var valStr = kvp.Value?.ToString();
                    if (valStr != null && (valStr.StartsWith("@/app/workdir/") || valStr.StartsWith(@"@\app\workdir\")))
                    {
                        var normalizedValStr = valStr.Replace('\\', '/');
                        var fileName = Path.GetFileName(normalizedValStr);

                        // Defense-in-depth: reject filenames with path traversal characters
                        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')
                            || fileName.Contains(Path.DirectorySeparatorChar)
                            || fileName.Contains(Path.AltDirectorySeparatorChar))
                        {
                            _logger.LogWarning("Rejected suspicious file parameter: {Value}", valStr);
                            args[kvp.Key] = kvp.Value;
                            continue;
                        }

                        var hostPath = Path.Combine(workspacePath, fileName);

                        // Verify resolved path is within workspace
                        var normalizedWorkspace = Path.GetFullPath(workspacePath);
                        if (!normalizedWorkspace.EndsWith(Path.DirectorySeparatorChar))
                            normalizedWorkspace += Path.DirectorySeparatorChar;

                        if (!Path.GetFullPath(hostPath).StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("File parameter resolved outside workspace: {Path}", hostPath);
                            args[kvp.Key] = kvp.Value;
                            continue;
                        }

                        if (File.Exists(hostPath))
                        {
                            var fileInfo = new FileInfo(hostPath);
                            if (fileInfo.LinkTarget != null)
                            {
                                var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                                if (target != null)
                                {
                                    var targetFullPath = Path.GetFullPath(target.FullName);
                                    if (!targetFullPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _logger.LogWarning("Symlink target resolved outside workspace: {Path}", targetFullPath);
                                        args[kvp.Key] = kvp.Value;
                                        continue;
                                    }
                                }
                            }

                            var maxBytes = (long)_sysOptions.MaxAutoLoadFileSizeMb * 1024 * 1024;
                            if (fileInfo.Length > maxBytes)
                            {
                                _logger.LogWarning("File parameter {Path} exceeds maximum auto-load size ({Size} bytes > {MaxBytes} bytes)",
                                    hostPath, fileInfo.Length, maxBytes);
                                await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.Warning,
                                    $"[WARNING] File {fileName} ({fileInfo.Length / (1024.0 * 1024.0):F1}MB) exceeds maximum auto-load size ({_sysOptions.MaxAutoLoadFileSizeMb}MB). Not loading into parameter.");
                                args[kvp.Key] = kvp.Value;
                                continue;
                            }

                            try
                            {
                                if (_lockManager != null)
                                {
                                    using (await _lockManager.AcquireLockAsync(sessionId, fileName, ct))
                                    {
                                        args[kvp.Key] = await File.ReadAllTextAsync(hostPath, ct);
                                    }
                                }
                                else
                                {
                                    args[kvp.Key] = await File.ReadAllTextAsync(hostPath, ct);
                                }

                                if (debugMode)
                                    await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                                        $"[DEBUG] Auto-loaded {args[kvp.Key]?.ToString()?.Length ?? 0} chars from {fileName} for parameter '{kvp.Key}'");
                                continue;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to auto-load parameter from file {HostPath}", hostPath);
                            }
                        }
                    }
                    args[kvp.Key] = kvp.Value;
                }
            }

            if (debugMode)
                await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                    $"[DEBUG] Invoking {pluginFn[0]}.{pluginFn[1]} with {args.Count} arg(s): [{string.Join(", ", args.Select(a => $"{a.Key}={Truncate(a.Value?.ToString(), 60)}"))}]");

            var toolResult = await function.InvokeAsync(kernel, args, cancellationToken: ct);
            var resultStr = toolResult.GetValue<object>()?.ToString() ?? "null";

            // ── Auto-save large outputs to workspace ────────────────────
            bool skipAutoSave = actionName is "FileIO-ReadFromWorkspace" or "FileIO-ViewWorkspaceFile";
            var autoSaveThreshold = _aiOptions.AutoSaveToolOutputThreshold;
            if (autoSaveThreshold > 0 && resultStr.Length > autoSaveThreshold && !skipAutoSave)
            {
                var originalLength = resultStr.Length;

                var workspacePath = GetWorkspacePath(sessionId);
                Directory.CreateDirectory(workspacePath);

                var extension = resultStr.Contains("|---") || resultStr.Contains("| ---") || resultStr.Contains("## ") || resultStr.StartsWith("# ") || resultStr.Contains("\n# ") || resultStr.Contains("```") ? ".md" : ".txt";
                var safeActionName = string.Join("_", actionName.Split(Path.GetInvalidFileNameChars()));
                var uniqueDiscriminator = Guid.NewGuid().ToString("N")[..6];
                var fileName = $"{safeActionName}_step{step}_{uniqueDiscriminator}_output{extension}";
                var filePath = Path.Combine(workspacePath, fileName);
                if (_lockManager != null)
                {
                    using (await _lockManager.AcquireLockAsync(sessionId, fileName, ct))
                    {
                        await File.WriteAllTextAsync(filePath, resultStr, ct);
                    }
                }
                else
                {
                    await File.WriteAllTextAsync(filePath, resultStr, ct);
                }

                int targetPreviewLen = _aiOptions.ToolOutputPreviewLength > 0 ? _aiOptions.ToolOutputPreviewLength : 500;
                int keepLen = Math.Min(targetPreviewLen, originalLength / 2);
                int safeBeginEnd = SafeTruncateIndex(resultStr, keepLen);
                int safeEndStart = SafeTruncateIndex(resultStr, resultStr.Length - keepLen);
                string beginning = resultStr[..safeBeginEnd];
                string end = resultStr[safeEndStart..];
                int truncatedCount = Math.Max(0, safeEndStart - safeBeginEnd);

                resultStr = $"SUCCESS: Tool output was very large ({originalLength:N0} chars) and has been auto-saved to /app/workdir/{fileName}\n" +
                           $"--- Instructions ---\n" +
                           $"- If this output is the final goal of your task, the task is now COMPLETE. Please return a FinalAnswer containing the path: /app/workdir/{fileName}\n" +
                           $"- If you need to process this data further, use Python or FileIO tools to read from /app/workdir/{fileName}. Do NOT print massive data to standard output again.\n\n" +
                           $"--- Output Preview ---\n{beginning}\n\n... [{truncatedCount} chars truncated] ...\n\n{end}";

                if (debugMode)
                    await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                        $"[DEBUG] Auto-saved {originalLength:N0} char tool output to {filePath}");
            }
            else if (resultStr.Length > _aiOptions.MaxToolObservationLength)
            {
                var originalLength = resultStr.Length;
                int keepLen = _aiOptions.MaxToolObservationLength / 2;
                
                // Safely truncate without splitting surrogate pairs
                int safeBeginEnd = SafeTruncateIndex(resultStr, keepLen);
                int safeEndStart = SafeTruncateIndex(resultStr, resultStr.Length - keepLen);
                string beginning = resultStr[..safeBeginEnd];
                string end = resultStr[safeEndStart..];

                resultStr = beginning + 
                    $"\n\n... [{originalLength - (keepLen * 2)} chars removed due to length] ...\n\n" + 
                    end + 
                    $"\n[WARNING: Tool output was {originalLength} chars. You must NEVER print massive raw datasets to standard output. Refine your code.]";
            }

            await _taskStore.LogEventAsync(sessionId, EventTypes.ToolExecuted, LogLevelNames.Tool, $"{actionName} returned {resultStr.Length} chars");
            string broadcastStr = resultStr;
            if (structuredStateEnabled) broadcastStr += "\n[/app/workdir/_task_state.md]";
            await _taskStore.BroadcastToolExecutionAsync(sessionId, actionName, actionInputStr, broadcastStr, isStarting: false, agentName);

            if (debugMode)
                await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                    $"[DEBUG] Tool result ({resultStr.Length} chars): {Truncate(resultStr, 300)}");

            return resultStr;
        }
        catch (ArgumentException argEx)
        {
            var error = $"Error: {argEx.Message} Please retry this tool call with all required parameters.";
            _logger.LogWarning("Tool {Action} called with missing/invalid parameters for session {SessionId}: {Message}",
                actionName, sessionId, argEx.Message);
            await _taskStore.LogEventAsync(sessionId, EventTypes.ToolError, LogLevelNames.Warning, error);
            string broadcastErr = error;
            if (structuredStateEnabled) broadcastErr += "\n[/app/workdir/_task_state.md]";
            await _taskStore.BroadcastToolExecutionAsync(sessionId, actionName, actionInputStr, broadcastErr, isStarting: false, agentName);
            return error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = $"Tool execution failed: {ex.Message}";
            _logger.LogError(ex, "Tool {Action} failed for session {SessionId}", actionName, sessionId);
            await _taskStore.LogEventAsync(sessionId, EventTypes.ToolError, LogLevelNames.System, error);
            string broadcastErr = error;
            if (structuredStateEnabled) broadcastErr += "\n[/app/workdir/_task_state.md]";
            await _taskStore.BroadcastToolExecutionAsync(sessionId, actionName, actionInputStr, broadcastErr, isStarting: false, agentName);

            if (debugMode)
                await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                    $"[DEBUG] Tool exception: {ex}");

            return error;
        }
    }

    /// <summary>
    /// Checks whether the target action references a registered function marked with IsDangerous: true in its metadata.
    /// Unregistered or unresolvable actions return false so they can be handled directly by ToolExecutionService.
    /// </summary>
    public static bool IsDangerousAction(Kernel kernel, string actionName)
    {
        actionName = actionName.Trim().Trim('`', '*', '_', '"', '\'').Trim();
        var pluginFn = actionName.Split('-', 2);
        if (pluginFn.Length != 2) return false;

        try
        {
            if (kernel.Plugins.TryGetPlugin(pluginFn[0], out var plugin) &&
                plugin.TryGetFunction(pluginFn[1], out var function))
            {
                var metadata = function.Metadata;
                if (metadata.AdditionalProperties.TryGetValue("IsDangerous", out var isDangerous))
                {
                    return isDangerous is true or "true";
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public string GetWorkspacePath(Guid sessionId) =>
        SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(sessionId, _sysOptions);

    /// <summary>
    /// Adjusts a truncation index to avoid splitting a UTF-16 surrogate pair.
    /// </summary>
    private static int SafeTruncateIndex(string s, int index)
    {
        if (index <= 0) return 0;
        if (index >= s.Length) return s.Length;
        if (char.IsHighSurrogate(s[index - 1])) return index - 1;
        if (char.IsLowSurrogate(s[index])) return Math.Max(0, index - 1);
        return index;
    }

    public static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "(null)";
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
