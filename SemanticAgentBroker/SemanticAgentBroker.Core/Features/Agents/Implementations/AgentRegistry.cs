using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Core.Features.Agents.Implementations;

/// <inheritdoc />
public class AgentRegistry : IAgentRegistry
{
    private readonly Dictionary<string, AgentDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <inheritdoc />
    public void LoadAll(IEnumerable<AgentDefinition> agents)
    {
        lock (_lock)
        {
            _cache.Clear();
            foreach (var agent in agents)
            {
                _cache[agent.Name] = agent;
            }
        }
    }

    /// <inheritdoc />
    public void Register(AgentDefinition definition)
    {
        lock (_lock)
        {
            _cache[definition.Name] = definition;
        }
    }

    /// <inheritdoc />
    public bool Remove(string name)
    {
        lock (_lock)
        {
            return _cache.Remove(name);
        }
    }

    /// <inheritdoc />
    public AgentDefinition Resolve(string name)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(name, out var def)
                ? def
                : throw new ArgumentException(
                    $"Unknown agent: {name}. Available agents: {string.Join(", ", _cache.Keys)}");
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentDefinition> GetAll()
    {
        lock (_lock)
        {
            return _cache.Values.ToList();
        }
    }

    /// <inheritdoc />
    public bool Contains(string name)
    {
        lock (_lock)
        {
            return _cache.ContainsKey(name);
        }
    }
}
