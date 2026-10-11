using Markdig;

namespace SemanticAgentBroker.Client.Features.Markdown.Implementations;

/// <inheritdoc />
public partial class MarkdownService : IMarkdownService
{
    private readonly MarkdownPipeline _safePipeline;

    public MarkdownService()
    {
        _safePipeline = new MarkdownPipelineBuilder()
            .DisableHtml()
            .UseAdvancedExtensions()
            .UseAutoLinks()
            .UseTaskLists()
            .UseEmojiAndSmiley()
            .Build();
    }

    /// <inheritdoc />
    public string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        return Markdig.Markdown.ToHtml(markdown, _safePipeline);
    }

    /// <inheritdoc />
    public async Task<string> ToHtmlAsync(string? markdown, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        // For large documents, yield so the browser renders frames and processes user cancel/close clicks
        if (markdown.Length >= 16384)
        {
            await Task.Delay(1, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var html = Markdig.Markdown.ToHtml(markdown, _safePipeline);
        cancellationToken.ThrowIfCancellationRequested();

        return html;
    }
}
