namespace SemanticAgentBroker.Client.Features.Markdown;

public interface IMarkdownService
{
    string ToHtml(string? markdown);
    Task<string> ToHtmlAsync(string? markdown, CancellationToken cancellationToken = default);
}
