namespace SemanticAgentBroker.Api.Features.Tasks.Models;

public record BulkDeleteRequest(List<Guid> TaskIds);
