using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.TestInfrastructure;

/// <summary>
/// Provides isolated workspace folder management for testing file-based agent tools,
/// state manager, and file I/O plugins without polluting system directories.
/// </summary>
public class TestWorkspaceFixture : IDisposable
{
    public string BaseWorkspaceDir { get; }
    public Guid SessionId { get; }
    public string WorkspacePath { get; }
    public AgentSystemOptions SystemOptions { get; }

    public TestWorkspaceFixture()
    {
        SessionId = Guid.NewGuid();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        BaseWorkspaceDir = Path.Combine(userProfile, "SemanticAgentBrokerDataTests", "workspaces", SessionId.ToString("N"));
        WorkspacePath = Path.Combine(BaseWorkspaceDir, SessionId.ToString());
        Directory.CreateDirectory(WorkspacePath);

        SystemOptions = new AgentSystemOptions
        {
            TaskWorkspacesPath = BaseWorkspaceDir
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(BaseWorkspaceDir))
            {
                Directory.Delete(BaseWorkspaceDir, recursive: true);
            }
        }
        catch
        {
            // Ignore OS file lock release delays on cleanup
        }
    }
}
