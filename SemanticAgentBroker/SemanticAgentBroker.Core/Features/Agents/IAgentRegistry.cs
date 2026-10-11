using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Core.Features.Agents;

/// <summary>
/// DB-backed registry for agent definitions. Agents are created/edited via the UI/API
/// and persisted in the database. Caches loaded definitions in memory for fast lookup.
/// </summary>
public interface IAgentRegistry
{
    /// <summary>Loads agent definitions into memory.</summary>
    /// <param name="agents">Collection of agent definitions to load.</param>
    void LoadAll(IEnumerable<AgentDefinition> agents);

    /// <summary>Registers or updates an agent definition in the registry cache.</summary>
    /// <param name="definition">The agent definition to register.</param>
    void Register(AgentDefinition definition);

    /// <summary>Removes an agent definition by name.</summary>
    /// <param name="name">Agent definition name.</param>
    /// <returns>True if removed; otherwise false.</returns>
    bool Remove(string name);

    /// <summary>Resolves an agent definition by name.</summary>
    /// <param name="name">Agent definition name.</param>
    /// <returns>The resolved agent definition.</returns>
    AgentDefinition Resolve(string name);

    /// <summary>Returns all currently registered agent definitions.</summary>
    /// <returns>A read-only collection of agent definitions.</returns>
    IReadOnlyCollection<AgentDefinition> GetAll();

    /// <summary>Checks whether an agent definition exists by name.</summary>
    /// <param name="name">Agent definition name.</param>
    /// <returns>True if exists; otherwise false.</returns>
    bool Contains(string name);
}
