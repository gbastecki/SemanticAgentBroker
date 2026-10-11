namespace SemanticAgentBroker.Core.Features.Agents;

/// <summary>
/// Manages synchronization locks for workspace file access to prevent concurrent read/write corruptions.
/// </summary>
public interface IWorkspaceLockManager : SemanticAgentBroker.Sdk.IWorkspaceLockManager
{
}

