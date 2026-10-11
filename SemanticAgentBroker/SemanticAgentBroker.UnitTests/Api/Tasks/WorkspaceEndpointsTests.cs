using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SemanticAgentBroker.UnitTests.Api.Tasks;

[TestClass]
public class WorkspaceEndpointsTests
{
    private CustomWebApplicationFactory _factory = null!;
    private HttpClient _adminClient = null!;
    private HttpClient _userClient = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _adminClient = await _factory.CreateAuthenticatedClientAsync("admin", "Admin", "admin-browser");
        _userClient = await _factory.CreateAuthenticatedClientAsync("testuser", "User", "user-browser");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _adminClient.Dispose();
        _userClient.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task GetWorkspaceFiles_ReturnsListOfFiles()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "output.json"), "{}");
        }

        // Act
        var url = ApiRoutes.TaskWorkspace.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var files = doc.RootElement.EnumerateArray().ToList();
        Assert.IsTrue(files.Any(f => f.GetProperty("name").GetString() == "output.json"));
    }

    [TestMethod]
    public async Task DownloadWorkspaceFile_ReturnsFileContent()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "report.csv"), "col1,col2\n1,2");
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "report.csv");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.AreEqual("col1,col2\n1,2", content);
    }

    [TestMethod]
    public async Task DownloadWorkspaceFile_WhenFileSizeExceedsMaxDownloadSizeMb_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            sysOptions.MaxDownloadSizeMb = 1; // 1 MB limit for test
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);

            // Create 1.5 MB file
            var largeData = new byte[1500 * 1024];
            Array.Fill(largeData, (byte)0x41);
            await File.WriteAllBytesAsync(Path.Combine(workspacePath, "large.bin"), largeData);
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "large.bin");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "exceeds the maximum allowed download size of 1 MB");
    }

    [TestMethod]
    public async Task UploadWorkspaceFile_ValidMultipartFile_SavesToDiskAndReturnsDto()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();
        }

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("uploaded file content"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        content.Add(fileContent, "file", "uploaded_doc.txt");

        // Act
        var url = ApiRoutes.TaskWorkspaceUpload.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _adminClient.PostAsync(url, content);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var verifyScope = _factory.Services.CreateScope();
        var sysOptions = verifyScope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
        var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
        var filePath = Path.Combine(workspacePath, "uploaded_doc.txt");
        Assert.IsTrue(File.Exists(filePath));
        Assert.AreEqual("uploaded file content", await File.ReadAllTextAsync(filePath));
    }

    [TestMethod]
    public async Task UploadWorkspaceFile_PathTraversalFilename_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();
        }

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("invalid filename payload"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        content.Add(fileContent, "file", "invalid?*name.txt");

        // Act
        var url = ApiRoutes.TaskWorkspaceUpload.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _adminClient.PostAsync(url, content);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UploadWorkspaceFile_WithSubdirectoryOrBackslashFilename_SavesSafeFileNameOnly()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();
        }

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("subpath payload"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        content.Add(fileContent, "file", @"subdir\nested\subfile.txt");

        // Act
        var url = ApiRoutes.TaskWorkspaceUpload.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _adminClient.PostAsync(url, content);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var verifyScope = _factory.Services.CreateScope();
        var sysOptions = verifyScope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
        var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
        var expectedFilePath = Path.Combine(workspacePath, "subfile.txt");
        Assert.IsTrue(File.Exists(expectedFilePath));
        Assert.IsFalse(expectedFilePath.Contains("subdir"));
    }

    [TestMethod]
    public async Task DownloadWorkspaceFile_WithBackslashPathTraversal_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();
        }

        // Act - fileName containing path traversal characters
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "test..file.txt");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteWorkspaceFile_ExistingFile_DeletesFromDisk()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        string filePath;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            filePath = Path.Combine(workspacePath, "to_delete.txt");
            await File.WriteAllTextAsync(filePath, "delete me");
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "to_delete.txt");
        var response = await _adminClient.DeleteAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(File.Exists(filePath));
    }

    [TestMethod]
    public async Task UpdateWorkspaceFile_ValidTextFile_UpdatesContent()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        string filePath;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            filePath = Path.Combine(workspacePath, "editable.txt");
            await File.WriteAllTextAsync(filePath, "original content");
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "editable.txt");
        var updateContent = new StringContent("updated new content", Encoding.UTF8, "text/plain");
        var response = await _adminClient.PutAsync(url, updateContent);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("updated new content", await File.ReadAllTextAsync(filePath));
    }

    [TestMethod]
    public async Task UpdateWorkspaceFile_BinaryFile_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllBytesAsync(Path.Combine(workspacePath, "binary.bin"), [0x00, 0x01]);
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "binary.bin");
        var updateContent = new StringContent("some text", Encoding.UTF8, "text/plain");
        var response = await _adminClient.PutAsync(url, updateContent);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task AuthorizeTaskAccess_NonOwnerPrivateTask_ReturnsForbidden()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "GeneralAssistant",
                UserPrompt = "Private Task",
                IsPrivate = true,
                CreatedByUserId = Guid.NewGuid() // Not testuser
            });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskWorkspace.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _userClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task GetUploadLimit_ReturnsConfiguredLimit()
    {
        // Act
        var response = await _adminClient.GetAsync(ApiRoutes.ConfigUploadLimit);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(json.TryGetProperty("maxUploadSizeMb", out var limitProp));
        Assert.IsTrue(limitProp.GetInt32() > 0);
    }

    [TestMethod]
    public async Task DownloadWorkspaceFile_BinaryFile_ReturnsOctetStream()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var binaryBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllBytesAsync(Path.Combine(workspacePath, "image.bin"), binaryBytes);
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "image.bin");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        CollectionAssert.AreEqual(binaryBytes, bytes);
    }

    [TestMethod]
    public async Task UpdateWorkspaceFile_ExceedingMaxEditSize_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            sysOptions.MaxFileEditSizeMb = 1; // 1 MB limit for test
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "large.txt"), "init");
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "large.txt");

        // 2 MB payload (exceeding 1 MB limit)
        var largeContent = new string('A', 2 * 1024 * 1024);
        var updateContent = new StringContent(largeContent, Encoding.UTF8, "text/plain");
        var response = await _adminClient.PutAsync(url, updateContent);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(error, "size limit");
    }

    [TestMethod]
    public async Task EditFile_WithMultiByteUtf8Content_ExceedingByteLimit_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            sysOptions.MaxFileEditSizeMb = 1; // 1 MB = 1,048,576 bytes limit
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "multibyte.txt"), "init");
        }

        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "multibyte.txt");

        // 600,000 chars of '你' (3 bytes each in UTF-8 = 1,800,000 bytes > 1,048,576 bytes)
        // Chars (600,000) < 1,048,576, but Bytes (1,800,000) > 1,048,576
        var text = new string('你', 600_000);
        var utf8Bytes = Encoding.UTF8.GetBytes(text);

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        var stream = new MemoryStream(utf8Bytes);
        request.Content = new StreamContent(stream);
        request.Headers.TransferEncodingChunked = true;

        // Act
        var response = await _adminClient.SendAsync(request);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(error, "size limit");
    }

    [TestMethod]
    public async Task DownloadWorkspaceFile_NonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "non_existent_file.txt");
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteWorkspaceFile_NonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "non_existent_file.txt");
        var response = await _adminClient.DeleteAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateWorkspaceFile_NonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString())
            .Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "non_existent_file.txt");
        var response = await _adminClient.PutAsync(url, new StringContent("content", Encoding.UTF8, "text/plain"));

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task UploadWorkspaceFile_NotMultipartFormData_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();
        }

        // Act - Send plain string instead of multipart form data
        var url = ApiRoutes.TaskWorkspaceUpload.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _adminClient.PostAsync(url, new StringContent("not form data", Encoding.UTF8, "text/plain"));

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UploadWorkspaceFile_FileExceedingMaxSize_ReturnsBadRequest()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            sysOptions.MaxUploadSizeMb = 1; // 1 MB limit for test
        }

        // 2 MB payload
        using var content = new MultipartFormDataContent();
        var fileBytes = new byte[2 * 1024 * 1024];
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        content.Add(fileContent, "file", "too_large.bin");

        // Act
        var url = ApiRoutes.TaskWorkspaceUpload.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _adminClient.PostAsync(url, content);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(error, "size limit");
    }

    [TestMethod]
    public async Task GetWorkspaceFiles_NonExistentTaskId_ReturnsNotFound()
    {
        // Arrange
        var nonExistentTaskId = Guid.NewGuid();

        // Act - Call as regular user to trigger db.TaskSessions check
        var url = ApiRoutes.TaskWorkspace.Replace("{taskId:guid}", nonExistentTaskId.ToString()).Replace("{taskId}", nonExistentTaskId.ToString());
        var response = await _userClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task GetWorkspaceFiles_WithDockerfileAndMakefile_MarksAsTextFile()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0");
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "Makefile"), "build:\n\tdotnet build");
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "LICENSE"), "MIT License");
        }

        // Act
        var url = ApiRoutes.TaskWorkspace.Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString());
        var response = await _adminClient.GetAsync(url);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var files = await response.Content.ReadFromJsonAsync<WorkspaceFileDto[]>();
        Assert.IsNotNull(files);

        var dockerfile = files.FirstOrDefault(f => f.Name == "Dockerfile");
        Assert.IsNotNull(dockerfile);
        Assert.IsTrue(dockerfile.IsTextFile, "Dockerfile should be identified as a text file");

        var makefile = files.FirstOrDefault(f => f.Name == "Makefile");
        Assert.IsNotNull(makefile);
        Assert.IsTrue(makefile.IsTextFile, "Makefile should be identified as a text file");

        var license = files.FirstOrDefault(f => f.Name == "LICENSE");
        Assert.IsNotNull(license);
        Assert.IsTrue(license.IsTextFile, "LICENSE should be identified as a text file");
    }

    [TestMethod]
    public async Task UpdateWorkspaceFile_WithDockerfile_AllowsTextEditing()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        string workspacePath;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskSessions.Add(new TaskSession { Id = taskId, AgentName = "GeneralAssistant", UserPrompt = "P" });
            await db.SaveChangesAsync();

            var sysOptions = scope.ServiceProvider.GetRequiredService<AgentSystemOptions>();
            workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "Dockerfile"), "FROM old");
        }

        // Act
        var url = ApiRoutes.TaskWorkspaceFile
            .Replace("{taskId:guid}", taskId.ToString()).Replace("{taskId}", taskId.ToString())
            .Replace("{fileName}", "Dockerfile");
        var content = new StringContent("FROM mcr.microsoft.com/dotnet/aspnet:10.0", Encoding.UTF8, "text/plain");
        var response = await _adminClient.PutAsync(url, content);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updatedText = await File.ReadAllTextAsync(Path.Combine(workspacePath, "Dockerfile"));
        Assert.AreEqual("FROM mcr.microsoft.com/dotnet/aspnet:10.0", updatedText);
    }
}
