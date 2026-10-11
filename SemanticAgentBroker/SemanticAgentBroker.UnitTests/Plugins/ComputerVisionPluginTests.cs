using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Moq;
using SemanticAgentBroker.Plugins.BuiltIn.Optional;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.UnitTests.TestInfrastructure;
using System.Net;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class ComputerVisionPluginTests
{
    private TestWorkspaceFixture _workspaceFixture = null!;
    private Mock<IPluginContext> _contextMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _workspaceFixture = new TestWorkspaceFixture();
        _contextMock = new Mock<IPluginContext>();
        _contextMock.Setup(c => c.WorkspacePath).Returns(_workspaceFixture.WorkspacePath);
        _contextMock.Setup(c => c.Configuration).Returns(new Dictionary<string, string>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _workspaceFixture.Dispose();
    }

    [TestMethod]
    public void ComputerVisionPlugin_Metadata_IsConfiguredCorrectly()
    {
        var plugin = new ComputerVisionPlugin();

        Assert.AreEqual("ComputerVision", plugin.Id);
        Assert.AreEqual("Computer Vision", plugin.DisplayName);
        Assert.AreEqual(PluginCategory.BuiltInOptional, plugin.Category);
        Assert.AreEqual(1, plugin.GetMethods().Count);
        Assert.AreEqual("AnalyzeImage", plugin.GetMethods()[0].Id);
    }

    [TestMethod]
    public async Task AnalyzeImage_ImageFileNotFound_ReturnsErrorMessage()
    {
        // Arrange
        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var chatMock = new Mock<IChatCompletionService>();
        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "non_existent_image.png",
            ["prompt"] = "What is in this picture?"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Error: Image file not found at");
    }

    [TestMethod]
    public async Task AnalyzeImage_NoChatServiceInContext_ReturnsSimulatedMessage()
    {
        // Arrange
        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns((IChatCompletionService?)null);

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "test.png",
            ["prompt"] = "Describe this image"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.Contains(result, "[SIMULATED VISION ANALYSIS]");
        StringAssert.Contains(result, "test.png");
    }

    [TestMethod]
    public async Task AnalyzeImage_ValidImageFile_CallsVisionChatService()
    {
        // Arrange
        var imagePath = Path.Combine(_workspaceFixture.WorkspacePath, "sample.png");
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header

        var chatMock = new Mock<IChatCompletionService>();
        var responseContent = new ChatMessageContent(AuthorRole.Assistant, "A blue sphere on a wooden table.");
        chatMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseContent });

        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "/app/workdir/sample.png",
            ["prompt"] = "What is shown in the image?"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("A blue sphere on a wooden table.", result);
    }

    [TestMethod]
    public async Task AnalyzeImage_RemoteHttpsUrl_InvokesVisionChatService()
    {
        // Arrange
        var chatMock = new Mock<IChatCompletionService>();
        var responseContent = new ChatMessageContent(AuthorRole.Assistant, "A photo of the Eiffel tower.");
        chatMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseContent });

        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "https://example.com/eiffel.jpg",
            ["prompt"] = "Identify the landmark"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("A photo of the Eiffel tower.", result);
    }

    [TestMethod]
    public async Task AnalyzeImage_RemoteUppercaseUrl_InvokesVisionChatService()
    {
        // Arrange
        var chatMock = new Mock<IChatCompletionService>();
        var responseContent = new ChatMessageContent(AuthorRole.Assistant, "A photo of the Eiffel tower.");
        chatMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { responseContent });

        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "HTTPS://EXAMPLE.COM/EIFFEL.JPG",
            ["prompt"] = "Identify the landmark"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("A photo of the Eiffel tower.", result);
    }

    [TestMethod]
    public async Task AnalyzeImage_PathTraversalAttempt_ClampsToWorkspaceRoot()
    {
        // Arrange
        var chatMock = new Mock<IChatCompletionService>();
        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "../../../etc/passwd.png",
            ["prompt"] = "Inspect file"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert - Path traversal should be clamped to workspace root and fail finding file inside workspace
        StringAssert.StartsWith(result, "Error: Image file not found at");
        StringAssert.Contains(result, _workspaceFixture.WorkspacePath);
        Assert.IsFalse(result.Contains(".."));
    }

    [TestMethod]
    public async Task AnalyzeImage_WithConfiguredVisionRouteInAiOptions_CreatesClientViaHttpClientFactory()
    {
        // Arrange
        var imagePath = Path.Combine(_workspaceFixture.WorkspacePath, "diagram.png");
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var aiOptions = new SemanticAgentBroker.Shared.Configuration.AiProviderOptions
        {
            Connections =
            [
                new SemanticAgentBroker.Shared.Configuration.ProviderConnection
                {
                    Id = "vision-provider",
                    EndpointName = "Vision Provider",
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "sk-mock-key",
                    IsEnabled = true,
                    Models = [new SemanticAgentBroker.Shared.Configuration.ModelConfig { Id = "gpt-4o" }]
                }
            ],
            ModelRouting = new Dictionary<SemanticAgentBroker.Shared.Configuration.TaskModelRole, SemanticAgentBroker.Shared.Configuration.ModelRouteConfig>
            {
                [SemanticAgentBroker.Shared.Configuration.TaskModelRole.VisionModel] = new SemanticAgentBroker.Shared.Configuration.ModelRouteConfig
                {
                    ProviderId = "vision-provider",
                    ModelId = "gpt-4o"
                }
            }
        };

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("vision-provider")).Returns(new HttpClient());

        _contextMock.Setup(c => c.GetService<SemanticAgentBroker.Shared.Configuration.AiProviderOptions>()).Returns(aiOptions);
        _contextMock.Setup(c => c.GetService<IHttpClientFactory>()).Returns(httpFactoryMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "diagram.png",
            ["prompt"] = "Explain diagram"
        };

        // Act - Will attempt to call OpenAI chat completion using mock client (or fail with expected network/auth exception handled gracefully)
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert - HttpClientFactory was invoked for the configured connection
        httpFactoryMock.Verify(f => f.CreateClient("vision-provider"), Times.Once);
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task AnalyzeImage_WhenVisionModelReturnsNullContent_ReturnsErrorString()
    {
        // Arrange
        var imagePath = Path.Combine(_workspaceFixture.WorkspacePath, "test.png");
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var chatMock = new Mock<IChatCompletionService>();
        chatMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { new ChatMessageContent(AuthorRole.Assistant, content: (string?)null) });

        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "test.png",
            ["prompt"] = "Check"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("Error: Empty response from vision model.", result);
    }

    [TestMethod]
    public async Task AnalyzeImage_WhenRequiredArgumentsMissing_ThrowsArgumentException()
    {
        // Arrange
        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        // Act & Assert 1: Missing imagePathOrUrl
        await Assert.ThrowsAsync<ArgumentException>(() =>
            method.ExecuteAsync(new Dictionary<string, object?> { ["prompt"] = "Describe" }, _contextMock.Object));

        // Act & Assert 2: Missing prompt
        await Assert.ThrowsAsync<ArgumentException>(() =>
            method.ExecuteAsync(new Dictionary<string, object?> { ["imagePathOrUrl"] = "pic.png" }, _contextMock.Object));
    }

    [TestMethod]
    public async Task AnalyzeImage_WhenVisionChatThrows_ReturnsAnalysisFailedError()
    {
        // Arrange
        var imagePath = Path.Combine(_workspaceFixture.WorkspacePath, "test.png");
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var chatMock = new Mock<IChatCompletionService>();
        chatMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Vision model API connection refused"));

        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "test.png",
            ["prompt"] = "Inspect"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        StringAssert.StartsWith(result, "Vision analysis failed:");
        StringAssert.Contains(result, "Vision model API connection refused");
    }

    [TestMethod]
    public async Task AnalyzeImage_ParentDirectorySymlinkTraversal_RestrictsToWorkspaceRoot()
    {
        // Arrange - create outside dir with an image and a symlink/junction inside workspace pointing to it
        var outsideDir = Path.Combine(_workspaceFixture.WorkspacePath, "..", "outside_cv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "secret.png");
        await File.WriteAllBytesAsync(outsideFile, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var symlinkDir = Path.Combine(_workspaceFixture.WorkspacePath, "sym_dir");
        if (OperatingSystem.IsWindows())
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{symlinkDir}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(symlinkDir, outsideDir);
        }

        try
        {
            var chatMock = new Mock<IChatCompletionService>();
            var responseContent = new ChatMessageContent(AuthorRole.Assistant, "Should not reach here");
            chatMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([responseContent]);
            _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

            var plugin = new ComputerVisionPlugin();
            var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

            var args = new Dictionary<string, object?>
            {
                ["imagePathOrUrl"] = "sym_dir/secret.png",
                ["prompt"] = "Inspect secret"
            };

            // Act
            var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

            // Assert - must not read from outside directory, should report not found at workspace root
            StringAssert.StartsWith(result, "Error: Image file not found at");
            StringAssert.EndsWith(result, Path.Combine(_workspaceFixture.WorkspacePath, "secret.png"));
        }
        finally
        {
            try { Directory.Delete(symlinkDir); } catch { }
            try { Directory.Delete(outsideDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task AnalyzeImage_WithFileUri_ResolvesLocalPathAndAnalyzes()
    {
        // Arrange
        var imagePath = Path.Combine(_workspaceFixture.WorkspacePath, "test_uri.png");
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);

        var fileUri = new Uri(imagePath).AbsoluteUri;

        var chatMock = new Mock<IChatCompletionService>();
        var responseContent = new ChatMessageContent(AuthorRole.Assistant, "Image analysis result.");
        chatMock.Setup(c => c.GetChatMessageContentsAsync(
            It.IsAny<ChatHistory>(),
            It.IsAny<PromptExecutionSettings>(),
            It.IsAny<Kernel>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync([responseContent]);
        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = fileUri,
            ["prompt"] = "Analyze this file URI image"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("Image analysis result.", result);
    }

    private class InterceptingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        private readonly HttpResponseMessage _response;

        public InterceptingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(_response);
        }
    }

    [TestMethod]
    public async Task AnalyzeImage_WithCustomEndpointConnection_ConfiguresOpenAIEndpoint()
    {
        // Arrange
        var imagePath = Path.Combine(_workspaceFixture.WorkspacePath, "test_custom_endpoint.png");
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);

        var aiOptions = new SemanticAgentBroker.Shared.Configuration.AiProviderOptions
        {
            Connections = [
                new SemanticAgentBroker.Shared.Configuration.ProviderConnection
                {
                    Id = "CustomVisionProvider",
                    ProviderType = "OpenAi",
                    Endpoint = "http://custom-vision-server:8000/v1",
                    ApiKey = "custom-key"
                }
            ],
            ModelRouting = new()
            {
                [SemanticAgentBroker.Shared.Configuration.TaskModelRole.VisionModel] = new SemanticAgentBroker.Shared.Configuration.ModelRouteConfig
                {
                    ProviderId = "CustomVisionProvider",
                    ModelId = "custom-vision-model"
                }
            }
        };
        _contextMock.Setup(c => c.GetService<SemanticAgentBroker.Shared.Configuration.AiProviderOptions>()).Returns(aiOptions);

        var jsonResponse = @"{
            ""id"": ""chatcmpl-test"",
            ""object"": ""chat.completion"",
            ""created"": 1677652288,
            ""choices"": [{
                ""index"": 0,
                ""message"": {
                    ""role"": ""assistant"",
                    ""content"": ""Analysis from custom endpoint""
                },
                ""finish_reason"": ""stop""
            }],
            ""usage"": { ""prompt_tokens"": 5, ""completion_tokens"": 5, ""total_tokens"": 10 }
        }";
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
        };
        var handler = new InterceptingHandler(httpResponse);
        var client = new HttpClient(handler);

        var httpFactoryMock = new Mock<System.Net.Http.IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient("CustomVisionProvider")).Returns(client);
        _contextMock.Setup(c => c.GetService<System.Net.Http.IHttpClientFactory>()).Returns(httpFactoryMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = "test_custom_endpoint.png",
            ["prompt"] = "What do you see?"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.IsNotNull(handler.CapturedRequest, "HTTP request should have been sent to the HTTP client");
        Assert.IsTrue(handler.CapturedRequest.RequestUri!.ToString().StartsWith("http://custom-vision-server:8000/v1"),
            $"Expected request to target custom endpoint 'http://custom-vision-server:8000/v1' but was '{handler.CapturedRequest.RequestUri}'");
    }

    [TestMethod]
    [DataRow("sample.gif", "image/gif")]
    [DataRow("sample.webp", "image/webp")]
    [DataRow("sample.bmp", "image/bmp")]
    [DataRow("sample.svg", "image/svg+xml")]
    [DataRow("sample.tiff", "image/tiff")]
    [DataRow("sample.png", "image/png")]
    [DataRow("sample.jpg", "image/jpeg")]
    [DataRow("sample.jpeg", "image/jpeg")]
    public async Task AnalyzeImage_VariousImageExtensions_UsesCorrectMimeType(string fileName, string expectedMimeType)
    {
        // Arrange
        var imagePath = Path.Combine(_workspaceFixture.WorkspacePath, fileName);
        Directory.CreateDirectory(_workspaceFixture.WorkspacePath);
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0x01, 0x02, 0x03, 0x04 });

        var chatMock = new Mock<IChatCompletionService>();
        ChatHistory? capturedHistory = null;
        chatMock.Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings>(),
                It.IsAny<Kernel>(),
                It.IsAny<CancellationToken>()))
            .Callback<ChatHistory, PromptExecutionSettings, Kernel, CancellationToken>((h, _, _, _) => capturedHistory = h)
            .ReturnsAsync(new List<ChatMessageContent> { new(AuthorRole.Assistant, "Analyzed") });

        _contextMock.Setup(c => c.GetService<IChatCompletionService>()).Returns(chatMock.Object);

        var plugin = new ComputerVisionPlugin();
        var method = plugin.GetMethods().First(m => m.Id == "AnalyzeImage");

        var args = new Dictionary<string, object?>
        {
            ["imagePathOrUrl"] = fileName,
            ["prompt"] = "Inspect MIME"
        };

        // Act
        var result = await method.ExecuteAsync(args, _contextMock.Object, CancellationToken.None);

        // Assert
        Assert.AreEqual("Analyzed", result);
        Assert.IsNotNull(capturedHistory);
        var userMsg = capturedHistory.LastOrDefault(m => m.Role == AuthorRole.User);
        Assert.IsNotNull(userMsg);
        var imageItem = userMsg.Items.OfType<ImageContent>().FirstOrDefault();
        Assert.IsNotNull(imageItem, "Message should contain an ImageContent item");
        Assert.AreEqual(expectedMimeType, imageItem.MimeType);
    }
}
