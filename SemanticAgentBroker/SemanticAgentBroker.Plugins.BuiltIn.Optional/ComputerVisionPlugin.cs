using SemanticAgentBroker.Sdk;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;

namespace SemanticAgentBroker.Plugins.BuiltIn.Optional;

public class ComputerVisionPlugin : PluginBase
{
    public override string Id => "ComputerVision";
    public override string DisplayName => "Computer Vision";
    public override string Description => "Analyze images using the configured Vision model.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "visibility";
    public override IReadOnlyList<string> Tags => ["vision", "image", "analysis"];
    public override PluginCategory Category => PluginCategory.BuiltInOptional;

    public override IReadOnlyList<IPluginMethod> GetMethods() =>
    [
        new AnalyzeImageMethod()
    ];
}

public class AnalyzeImageMethod : PluginMethodBase
{
    public override string Id => "AnalyzeImage";
    public override string DisplayName => "Analyze Image";
    public override string Description => "Analyzes an image from a path or URL and returns a description or answers a question about it.";
    public override string ReturnDescription => "Textual analysis of the image.";

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new() { Name = "imagePathOrUrl", Description = "The path or URL of the image to analyze.", ParameterType = typeof(string), IsRequired = true },
        new() { Name = "prompt", Description = "The question or instruction about the image (e.g. 'Describe this image').", ParameterType = typeof(string), IsRequired = true }
    ];

    public override async Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct)
    {
        var imagePathOrUrl = GetRequiredString(arguments, "imagePathOrUrl");
        var prompt = GetRequiredString(arguments, "prompt");

        try
        {
            var aiOptions = context.GetService<SemanticAgentBroker.Shared.Configuration.AiProviderOptions>();
            var httpFactory = context.GetService<System.Net.Http.IHttpClientFactory>();

            IChatCompletionService? chatService = null;

            if (aiOptions != null && httpFactory != null &&
                aiOptions.ModelRouting.TryGetValue(SemanticAgentBroker.Shared.Configuration.TaskModelRole.VisionModel, out var visionRoute))
            {
                var connection = aiOptions.Connections.FirstOrDefault(c =>
                    !string.IsNullOrEmpty(visionRoute.ProviderId)
                        ? c.Id.Equals(visionRoute.ProviderId, StringComparison.OrdinalIgnoreCase)
                        : c.Models.Any(m => m.Id.Equals(visionRoute.ModelId, StringComparison.OrdinalIgnoreCase)));

                if (connection != null)
                {
                    var httpClient = httpFactory.CreateClient(connection.Id);
                    var kernelBuilder = Kernel.CreateBuilder();

                    Uri? endpoint = null;
                    if (!string.IsNullOrWhiteSpace(connection.Endpoint) &&
                        Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var parsedEndpoint))
                    {
                        endpoint = parsedEndpoint;
                    }

                    if (endpoint != null)
                    {
                        kernelBuilder.AddOpenAIChatCompletion(
                            modelId: visionRoute.ModelId,
                            endpoint: endpoint,
                            apiKey: string.IsNullOrEmpty(connection.ApiKey) ? "dummy-key" : connection.ApiKey,
                            httpClient: httpClient);
                    }
                    else
                    {
                        kernelBuilder.AddOpenAIChatCompletion(
                            modelId: visionRoute.ModelId,
                            apiKey: string.IsNullOrEmpty(connection.ApiKey) ? "dummy-key" : connection.ApiKey,
                            httpClient: httpClient);
                    }
                    chatService = kernelBuilder.Build().GetRequiredService<IChatCompletionService>();
                }
            }

            chatService ??= context.GetService<IChatCompletionService>();

            if (chatService != null)
            {
                var chatHistory = new ChatHistory();
                var message = new ChatMessageContentItemCollection();
                message.Add(new TextContent(prompt));
                
                string localOrRemotePath = imagePathOrUrl;
                if (Uri.TryCreate(imagePathOrUrl, UriKind.Absolute, out var parsedUri) && parsedUri.IsFile)
                {
                    localOrRemotePath = parsedUri.LocalPath;
                }

                if (localOrRemotePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || localOrRemotePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    message.Add(new ImageContent(new Uri(localOrRemotePath)));
                }
                else
                {
                    var fullPath = ResolveLocalPath(localOrRemotePath, context.WorkspacePath);
                    if (File.Exists(fullPath))
                    {
                        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
                        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                        var mimeType = ext switch
                        {
                            ".png" => "image/png",
                            ".gif" => "image/gif",
                            ".webp" => "image/webp",
                            ".bmp" => "image/bmp",
                            ".svg" => "image/svg+xml",
                            ".tiff" or ".tif" => "image/tiff",
                            _ => "image/jpeg"
                        };
                        message.Add(new ImageContent(bytes, mimeType));
                    }
                    else
                    {
                        return $"Error: Image file not found at {fullPath}";
                    }
                }

                chatHistory.AddUserMessage(message);
                var response = await chatService.GetChatMessageContentAsync(chatHistory, null, null, ct);
                return response?.Content ?? "Error: Empty response from vision model.";
            }
            else
            {
                return $"[SIMULATED VISION ANALYSIS] Analyzed image '{imagePathOrUrl}' with prompt '{prompt}'. IChatCompletionService not available in context.";
            }
        }
        catch (Exception ex)
        {
            return $"Vision analysis failed: {ex.Message}";
        }
    }

    private static string ResolveLocalPath(string path, string workspacePath)
    {
        // Replace backslashes with forward slashes for uniform parsing
        var normalized = path.Replace('\\', '/');

        // Strip any simulated absolute prefixes: "/app/workdir/", "app/workdir/", "/app/", "app/"
        if (normalized.StartsWith("/app/workdir/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["/app/workdir/".Length..];
        }
        else if (normalized.StartsWith("app/workdir/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["app/workdir/".Length..];
        }
        else if (normalized.StartsWith("/app/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["/app/".Length..];
        }
        else if (normalized.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["app/".Length..];
        }

        // Clean up leading slashes after stripping
        path = path.TrimStart('/', '\\');

        // Resolve absolute path and verify directory bounds to prevent path traversal
        var combined = Path.GetFullPath(Path.Combine(workspacePath, path));
        
        // Ensure trailing slash on target workspace directory for correct prefix match
        var targetDir = Path.GetFullPath(workspacePath);
        if (!targetDir.EndsWith(Path.DirectorySeparatorChar))
        {
            targetDir += Path.DirectorySeparatorChar;
        }

        if (!combined.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
        {
            // Traversal attempt detected - restrict to workspace root
            return Path.Combine(workspacePath, Path.GetFileName(path));
        }

        // Validate symbolic link / junction targets (file and all parent directories)
        if (File.Exists(combined) || Directory.Exists(combined))
        {
            var fileInfo = new FileInfo(combined);
            if (fileInfo.LinkTarget != null)
            {
                var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    var targetFullPath = Path.GetFullPath(target.FullName);
                    if (!targetFullPath.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
                    {
                        // Symlink traversal attempt detected - restrict to workspace root
                        return Path.Combine(workspacePath, Path.GetFileName(path));
                    }
                }
            }

            // Check each parent directory up to the workspace root for symlinks / junctions
            var parent = fileInfo.Directory;
            while (parent != null && !string.Equals(parent.FullName.TrimEnd(Path.DirectorySeparatorChar), targetDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                if (parent.LinkTarget != null)
                {
                    var dirTarget = parent.ResolveLinkTarget(returnFinalTarget: true);
                    if (dirTarget != null)
                    {
                        var dirTargetFullPath = Path.GetFullPath(dirTarget.FullName);
                        if (!dirTargetFullPath.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
                        {
                            // Directory symlink/junction traversal attempt detected - restrict to workspace root
                            return Path.Combine(workspacePath, Path.GetFileName(path));
                        }
                    }
                }
                parent = parent.Parent;
            }
        }

        return combined;
    }
}
