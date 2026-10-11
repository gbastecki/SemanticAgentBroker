using SemanticAgentBroker.Core.Features.Agents.Implementations;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.UnitTests.Core.Features.Agents;

[TestClass]
public class AgentRegistryTests
{
    [TestMethod]
    public void Register_And_Resolve_WorksCaseInsensitively()
    {
        // Arrange
        var registry = new AgentRegistry();
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "OrchestratorAgent",
            SystemInstruction = "You are the orchestrator."
        };

        // Act
        registry.Register(agent);

        // Assert
        Assert.IsTrue(registry.Contains("OrchestratorAgent"));
        Assert.IsTrue(registry.Contains("orchestratoragent"));
        var resolved = registry.Resolve("ORCHESTRATORAGENT");
        Assert.AreEqual(agent.Id, resolved.Id);
    }

    [TestMethod]
    public void Resolve_NonExistentAgent_ThrowsArgumentException()
    {
        // Arrange
        var registry = new AgentRegistry();

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => registry.Resolve("NonExistent"));
        StringAssert.Contains(ex.Message, "Unknown agent: NonExistent");
    }

    [TestMethod]
    public void Remove_RemovesRegisteredAgent()
    {
        // Arrange
        var registry = new AgentRegistry();
        var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "TempAgent" };
        registry.Register(agent);

        // Act
        bool removed = registry.Remove("TempAgent");

        // Assert
        Assert.IsTrue(removed);
        Assert.IsFalse(registry.Contains("TempAgent"));
    }

    [TestMethod]
    public void LoadAll_ReplacesEntireRegistry()
    {
        // Arrange
        var registry = new AgentRegistry();
        registry.Register(new AgentDefinition { Id = Guid.NewGuid(), Name = "OldAgent" });

        var newAgents = new List<AgentDefinition>
        {
            new() { Id = Guid.NewGuid(), Name = "AgentA" },
            new() { Id = Guid.NewGuid(), Name = "AgentB" }
        };

        // Act
        registry.LoadAll(newAgents);

        // Assert
        Assert.IsFalse(registry.Contains("OldAgent"));
        Assert.IsTrue(registry.Contains("AgentA"));
        Assert.IsTrue(registry.Contains("AgentB"));
        Assert.AreEqual(2, registry.GetAll().Count);
    }

    [TestMethod]
    public void Remove_NonExistentAgent_ReturnsFalse()
    {
        // Arrange
        var registry = new AgentRegistry();

        // Act
        bool removed = registry.Remove("NonExistentAgent");

        // Assert
        Assert.IsFalse(removed);
    }

    [TestMethod]
    public void Register_OverwritesExistingAgentWithSameName()
    {
        // Arrange
        var registry = new AgentRegistry();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var agent1 = new AgentDefinition { Id = id1, Name = "DupeAgent", SystemInstruction = "First" };
        var agent2 = new AgentDefinition { Id = id2, Name = "dupeagent", SystemInstruction = "Second" };

        // Act
        registry.Register(agent1);
        registry.Register(agent2);

        // Assert
        Assert.AreEqual(1, registry.GetAll().Count);
        var resolved = registry.Resolve("DUPEAGENT");
        Assert.AreEqual(id2, resolved.Id);
        Assert.AreEqual("Second", resolved.SystemInstruction);
    }
}
