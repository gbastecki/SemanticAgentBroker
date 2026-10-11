namespace SemanticAgentBroker.Api.Features.Agents.Models;

public record CreateAgentRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? AccentClass { get; init; }
    public string? SystemInstruction { get; init; }
    public List<MethodBindingDto>? Methods { get; init; }
}

public record UpdateAgentRequest
{
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? AccentClass { get; init; }
    public string? SystemInstruction { get; init; }
    public bool? IsEnabled { get; init; }
    public List<MethodBindingDto>? Methods { get; init; }
}

public record MethodBindingDto
{
    public string PluginId { get; init; } = string.Empty;
    public string MethodId { get; init; } = string.Empty;
}
