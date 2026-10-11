using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.Plugins.MockData;

public class SampleImagesPlugin : PluginBase
{
    public override string Id => "SampleImages";
    public override string DisplayName => "Image Analysis Tasks";
    public override string Description => "Provides tasks for analyzing images uploaded to the workspace.";
    public override string Version => "1.0.0";
    public override string Author => "SemanticAgentBroker";
    public override string Icon => "image";
    public override IReadOnlyList<string> Tags => ["images", "vision", "analysis"];
    public override PluginCategory Category => PluginCategory.Custom;

    public override IReadOnlyList<string> RequiredPlugins => ["ComputerVision"];

    public override IReadOnlyList<PluginTaskDef> GetTasks() =>
    [
        new PluginTaskDef
        {
            Id = "AnalyzeWorkspaceImages",
            Name = "Analyze Uploaded Images",
            Description = "Finds images in the workspace and describes them using Computer Vision.",
            SystemInstruction = "You are an image analysis agent. You must use the ComputerVision plugin's AnalyzeImage method to read and describe images. First, use ListWorkspaceFiles to find the image files. DO NOT use Python or write custom code for image analysis.",
            ExternalMethods = 
            [
                new ExternalMethodRef { PluginId = "ComputerVision", MethodId = "AnalyzeImage" }
            ]
        }
    ];

    public override IReadOnlyList<IPluginMethod> GetMethods() => [];
}
