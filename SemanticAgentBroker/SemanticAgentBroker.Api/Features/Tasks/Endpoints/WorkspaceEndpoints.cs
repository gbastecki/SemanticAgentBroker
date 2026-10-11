using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Core.Features.Agents.Implementations;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.Shared.Utilities;

namespace SemanticAgentBroker.Api.Features.Tasks.Endpoints;

public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        // Config endpoint for upload limits (used by client-side validation)
        app.MapGet(ApiRoutes.ConfigUploadLimit, (AgentSystemOptions sysOptions) =>
            Results.Ok(new { maxUploadSizeMb = sysOptions.MaxUploadSizeMb }));

        // List all files in workspace
        app.MapGet(ApiRoutes.TaskWorkspace, async (Guid taskId, AgentSystemOptions sysOptions, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskAccessAsync(taskId, context, db, authDb);
            if (authResult != null) return authResult;

            var workspacePath = AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            if (!Directory.Exists(workspacePath))
                return Results.Ok(Array.Empty<WorkspaceFileDto>());

            var files = new DirectoryInfo(workspacePath)
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .Select(f => new WorkspaceFileDto(
                    f.Name,
                    f.Length,
                    f.LastWriteTimeUtc,
                    IsTextFile(f.Name)))
                .OrderByDescending(f => f.LastModified)
                .ToArray();

            return Results.Ok(files);
        });

        // Read/download a specific file
        app.MapGet(ApiRoutes.TaskWorkspaceFile, async (Guid taskId, string fileName, AgentSystemOptions sysOptions, SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager lockManager, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskAccessAsync(taskId, context, db, authDb);
            if (authResult != null) return authResult;

            var workspacePath = AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            if (!TryResolveAndValidateWorkspacePath(workspacePath, fileName, out var safeName, out var filePath))
                return Results.BadRequest("Invalid file name.");

            using (await lockManager.AcquireLockAsync(taskId, safeName))
            {
                if (!File.Exists(filePath))
                    return Results.NotFound($"File '{safeName}' not found.");

                var maxDownloadBytes = (long)sysOptions.MaxDownloadSizeMb * 1024 * 1024;
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > maxDownloadBytes)
                    return Results.BadRequest($"File '{safeName}' exceeds the maximum allowed download size of {sysOptions.MaxDownloadSizeMb} MB.");

                // For text files, return content as plain text
                if (IsTextFile(safeName))
                {
                    var content = await File.ReadAllTextAsync(filePath);
                    return Results.Text(content, "text/plain; charset=utf-8");
                }

                // For binary files, read bytes while lock is held to avoid stream sharing race
                var bytes = await File.ReadAllBytesAsync(filePath);
                return Results.File(bytes, "application/octet-stream", safeName);
            }
        });

        // Upload file(s) to workspace (Requires UploadFiles permission)
        app.MapPost(ApiRoutes.TaskWorkspaceUpload, async (Guid taskId, HttpRequest request, AgentSystemOptions sysOptions, AgentTaskStore taskStore, SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager lockManager, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskAccessAsync(taskId, context, db, authDb, requiredOption: "UploadFiles");
            if (authResult != null) return authResult;

            if (!request.HasFormContentType)
                return Results.BadRequest("Expected multipart form data.");

            var workspacePath = AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            var form = await request.ReadFormAsync();
            var maxUploadSize = (long)sysOptions.MaxUploadSizeMb * 1024 * 1024;

            // Validate all files in batch before creating directory or writing to disk
            foreach (var file in form.Files)
            {
                if (file.Length > maxUploadSize)
                    return Results.BadRequest($"File '{file.FileName}' exceeds the {sysOptions.MaxUploadSizeMb} MB size limit.");

                if (!TryResolveAndValidateWorkspacePath(workspacePath, file.FileName, out _, out _))
                    return Results.BadRequest($"Invalid file name: '{file.FileName}'.");
            }

            Directory.CreateDirectory(workspacePath);
            var uploadedFiles = new List<WorkspaceFileDto>();

            foreach (var file in form.Files)
            {
                TryResolveAndValidateWorkspacePath(workspacePath, file.FileName, out var safeName, out var targetPath);

                using (await lockManager.AcquireLockAsync(taskId, safeName))
                {
                    using (var stream = File.Create(targetPath))
                    {
                        await file.CopyToAsync(stream);
                    }

                    var info = new FileInfo(targetPath);
                    uploadedFiles.Add(new WorkspaceFileDto(
                        info.Name, info.Length, info.LastWriteTimeUtc, IsTextFile(info.Name)));

                    await taskStore.BroadcastWorkspaceFileChangedAsync(taskId, safeName, "Created");
                }
            }

            return Results.Ok(uploadedFiles);
        }).DisableAntiforgery();

        // Delete a file from workspace (Requires EditFiles permission)
        app.MapDelete(ApiRoutes.TaskWorkspaceFile, async (Guid taskId, string fileName, AgentSystemOptions sysOptions, AgentTaskStore taskStore, SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager lockManager, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskAccessAsync(taskId, context, db, authDb, requiredOption: "EditFiles");
            if (authResult != null) return authResult;

            var workspacePath = AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            if (!TryResolveAndValidateWorkspacePath(workspacePath, fileName, out var safeName, out var filePath))
                return Results.BadRequest("Invalid file name.");

            using (await lockManager.AcquireLockAsync(taskId, safeName))
            {
                if (!File.Exists(filePath))
                    return Results.NotFound($"File '{safeName}' not found.");

                File.Delete(filePath);
                await taskStore.BroadcastWorkspaceFileChangedAsync(taskId, safeName, "Deleted");
                return Results.Ok();
            }
        });

        // Update (save) text file content in workspace (Requires EditFiles permission)
        app.MapPut(ApiRoutes.TaskWorkspaceFile, async (Guid taskId, string fileName, HttpRequest request, AgentSystemOptions sysOptions, AgentTaskStore taskStore, SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager lockManager, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskAccessAsync(taskId, context, db, authDb, requiredOption: "EditFiles");
            if (authResult != null) return authResult;

            var maxEditSizeBytes = (long)sysOptions.MaxFileEditSizeMb * 1024 * 1024;
            if (request.ContentLength.HasValue && request.ContentLength.Value > maxEditSizeBytes)
                return Results.BadRequest($"File content exceeds the {sysOptions.MaxFileEditSizeMb} MB size limit.");

            var workspacePath = AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            if (!TryResolveAndValidateWorkspacePath(workspacePath, fileName, out var safeName, out var filePath))
                return Results.BadRequest("Invalid file name.");

            if (!IsTextFile(safeName))
                return Results.BadRequest("Only text files can be edited.");

            using (await lockManager.AcquireLockAsync(taskId, safeName))
            {
                if (!File.Exists(filePath))
                    return Results.NotFound($"File '{safeName}' not found.");

                using var ms = new MemoryStream();
                var buffer = new byte[8192];
                int bytesRead;
                long totalBytes = 0;
                while ((bytesRead = await request.Body.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > maxEditSizeBytes)
                        return Results.BadRequest($"File content exceeds the {sysOptions.MaxFileEditSizeMb} MB size limit.");
                    await ms.WriteAsync(buffer.AsMemory(0, bytesRead));
                }

                ms.Position = 0;
                using var reader = new StreamReader(ms);
                var content = await reader.ReadToEndAsync();
                await File.WriteAllTextAsync(filePath, content);
                await taskStore.BroadcastWorkspaceFileChangedAsync(taskId, safeName, "Modified");

                var info = new FileInfo(filePath);
                return Results.Ok(new WorkspaceFileDto(info.Name, info.Length, info.LastWriteTimeUtc, true));
            }
        });
    }

    private static bool TryResolveAndValidateWorkspacePath(
        string workspacePath, string fileName, out string safeFileName, out string fullPath)
    {
        safeFileName = string.Empty;
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var normalizedInput = fileName.Replace('\\', '/');
        var nameOnly = Path.GetFileName(normalizedInput);
        if (string.IsNullOrWhiteSpace(nameOnly) || nameOnly.Contains("..") || nameOnly.Contains('/') || nameOnly.Contains('\\'))
            return false;

        var invalidChars = Path.GetInvalidFileNameChars();
        if (nameOnly.IndexOfAny(invalidChars) >= 0)
            return false;

        var normalizedWorkspace = Path.GetFullPath(workspacePath);
        if (!normalizedWorkspace.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            normalizedWorkspace += Path.DirectorySeparatorChar;
        }

        var resolvedFullPath = Path.GetFullPath(Path.Combine(workspacePath, nameOnly));
        if (!resolvedFullPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Validate symbolic link / junction target if file exists on disk
        if (File.Exists(resolvedFullPath) || Directory.Exists(resolvedFullPath))
        {
            var fileInfo = new FileInfo(resolvedFullPath);
            if (fileInfo.LinkTarget != null)
            {
                var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    var targetFullPath = Path.GetFullPath(target.FullName);
                    if (!targetFullPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
        }

        safeFileName = nameOnly;
        fullPath = resolvedFullPath;
        return true;
    }

    private static bool IsTextFile(string fileName) =>
        WorkspaceFileUtils.IsTextFile(fileName);

    private static async Task<IResult?> AuthorizeTaskAccessAsync(
        Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb, string? requiredOption = null)
    {
        var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
        if (session == null) return Results.Unauthorized();
        if (session.Role == "Admin") return null;

        var task = await db.TaskSessions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return Results.NotFound();

        // Non-owner cannot access private tasks
        if (task.IsPrivate && task.CreatedByUserId != session.UserId) return Results.Forbid();

        // Check granular permissions for tasks bound to a plugin task
        if (task.SourcePluginId != null && task.PluginTaskId != null)
        {
            var perms = await authDb.GetEffectivePermissionsAsync(session.UserId);
            if (!perms.HasPermission(task.SourcePluginId, task.PluginTaskId, requiredOption))
                return Results.Forbid();

            return null;
        }

        // For tasks without source plugin task, creator is allowed
        if (task.CreatedByUserId == session.UserId)
        {
            return null;
        }

        return Results.Forbid();
    }
}
