using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Shared.Configuration;

[TestClass]
public class AiProviderOptionsDefaultsTests
{
    [TestMethod]
    public void CodeDefaults_MatchExpectedAppSettingsValues()
    {
        var opts = new AiProviderOptions();
        Assert.AreEqual(60, opts.DefaultTimeoutMinutes);
        Assert.AreEqual(8192, opts.MaxTokensPerRequest);
        Assert.AreEqual(24000, opts.MaxAssistantOutputLength);
        Assert.AreEqual(24000, opts.MaxToolObservationLength);
        Assert.AreEqual(3000, opts.AutoSaveToolOutputThreshold);
        Assert.AreEqual(500, opts.ToolOutputPreviewLength);
    }

    [TestMethod]
    public void ClientSettings_CodeDefaults_MatchExpectedValues()
    {
        var opts = new ClientSettingsOptions();
        Assert.AreEqual("https://localhost:5100/", opts.ApiBaseUrl);
        Assert.AreEqual("Production", opts.EnvironmentName);
    }

    [TestMethod]
    public void RestApi_And_ClientHost_CodeDefaults_HttpsOnly()
    {
        var restOpts = new RestApiOptions();
        Assert.IsNull(restOpts.HttpPort);
        Assert.AreEqual(5100, restOpts.HttpsPort);
        Assert.AreEqual(5100, restOpts.Port);

        var hostOpts = new ClientHostOptions();
        Assert.IsNull(hostOpts.HttpPort);
        Assert.AreEqual(7050, hostOpts.HttpsPort);
    }

    [TestMethod]
    public void InferenceOptions_CodeDefaults_MatchExpectedValues()
    {
        var opts = new InferenceOptions();
        Assert.AreEqual(3, opts.MaxParallelToolCalls);
        Assert.AreEqual(-1, opts.MaxLlmRetries);
        Assert.AreEqual(5, opts.LlmRetryDelaySeconds);
        Assert.AreEqual(30, opts.MaxLlmRetryDelaySeconds);
        Assert.AreEqual(3, opts.MaxFormatRetries);
        Assert.AreEqual(4000, opts.MaxFinalAnswerInlineChars);
        Assert.AreEqual(4, opts.StopMarkers.Count);
        CollectionAssert.Contains(opts.StopMarkers, "<channel|>");
        CollectionAssert.Contains(opts.StopMarkers, "<tool_call|>");
    }

    [TestMethod]
    public void PythonSandboxOptions_And_AgentSystemOptions_CodeDefaults_MatchExpectedValues()
    {
        var sandboxOpts = new PythonSandboxOptions();
        Assert.AreEqual("/app/workdir", sandboxOpts.ContainerDataPath);
        Assert.AreEqual("ai-sandbox-python:latest", sandboxOpts.ImageName);
        Assert.AreEqual(60, sandboxOpts.TimeoutSeconds);
        Assert.AreEqual(512, sandboxOpts.MemoryLimitMb);
        Assert.AreEqual(50, sandboxOpts.MaxOutputMb);
        Assert.AreEqual(100000, sandboxOpts.CpuPeriod);

        var sysOpts = new AgentSystemOptions();
        Assert.AreEqual(3, sysOpts.MaxConcurrentAgents);
        Assert.AreEqual(7, sysOpts.SessionDays);
        Assert.AreEqual(10, sysOpts.MaxFailedAttempts);
        Assert.AreEqual(5, sysOpts.LockoutMinutes);
        Assert.AreEqual(8000, sysOpts.MaxFileReadChars);
        Assert.AreEqual(10, sysOpts.MaxUploadSizeMb);
        Assert.AreEqual(50, sysOpts.MaxFileEditSizeMb);
        Assert.AreEqual(50, sysOpts.MaxAutoLoadFileSizeMb);
        Assert.IsTrue(sysOpts.CleanupWorkspacesOnTaskDelete);
        Assert.IsTrue(sysOpts.IsTasksCleanupServiceEnabled);
        Assert.AreEqual(14, sysOpts.TasksCleanupDays);
    }
}
