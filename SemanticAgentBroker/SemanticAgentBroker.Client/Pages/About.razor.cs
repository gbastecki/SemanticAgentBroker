using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SemanticAgentBroker.Client.Pages;

public partial class About : ComponentBase, IAsyncDisposable
{
    public enum DocViewMode
    {
        Rendered,
        Raw
    }

    public record DocInfo(
        string Id,
        string Title,
        string FileName,
        string Icon,
        string Description,
        bool IsMarkdown
    );

    private readonly List<DocInfo> documents = new()
    {
        new DocInfo("readme", "README.md", "README.md", "description", "Project overview, key features, and quickstart guide.", true),
        new DocInfo("docs", "DOCUMENTATION.md", "DOCUMENTATION.md", "menu_book", "Comprehensive architecture, configuration, and API reference.", true),
        new DocInfo("license", "LICENSE", "LICENSE", "gavel", "MIT License terms and conditions.", false),
        new DocInfo("notice", "NOTICE", "NOTICE", "assignment", "Third-party software notices, attributions, and licenses.", false)
    };

    private string selectedDocId = "readme";
    private DocViewMode viewMode = DocViewMode.Rendered;
    private bool isLoading;
    private string? errorMessage;
    private bool isCopied;
    private bool _isDisposed;

    private readonly Dictionary<string, string> _rawContentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _renderedHtmlCache = new(StringComparer.OrdinalIgnoreCase);
    private DotNetObjectReference<About>? _dotNetRef;
    private string? _pendingAnchor;

    private DocInfo? activeDoc => documents.FirstOrDefault(d => d.Id.Equals(selectedDocId, StringComparison.OrdinalIgnoreCase));
    private string currentContent = string.Empty;
    private string renderedHtml = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
        await LoadDocumentAsync(selectedDocId, forceReload: false);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!isLoading)
        {
            try
            {
                if (activeDoc?.IsMarkdown == true && viewMode == DocViewMode.Rendered)
                {
                    if (_dotNetRef != null)
                    {
                        await Interop.InitDocViewerLinkHandlerAsync(".doc-viewer-body", _dotNetRef);
                    }
                    await Interop.AttachCopyButtonsAsync();
                }

                if (!string.IsNullOrEmpty(_pendingAnchor))
                {
                    var anchor = _pendingAnchor;
                    _pendingAnchor = null;
                    await Task.Delay(100);
                    await Interop.ScrollToDocAnchorAsync(".doc-viewer-body", anchor);
                }
            }
            catch { }
        }
    }

    [JSInvokable]
    public async Task OnInternalDocLinkClicked(string docId, string anchor)
    {
        _pendingAnchor = string.IsNullOrWhiteSpace(anchor) ? null : anchor;

        if (selectedDocId.Equals(docId, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(_pendingAnchor))
            {
                var target = _pendingAnchor;
                _pendingAnchor = null;
                await Interop.ScrollToDocAnchorAsync(".doc-viewer-body", target);
            }
        }
        else
        {
            await SelectDocAsync(docId);
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task SelectDocAsync(string docId)
    {
        selectedDocId = docId;
        isCopied = false;
        
        // Default to rendered for markdown, raw for plain text
        if (activeDoc?.IsMarkdown == true)
        {
            viewMode = DocViewMode.Rendered;
        }
        else
        {
            viewMode = DocViewMode.Raw;
        }

        await LoadDocumentAsync(docId, forceReload: false);
        await InvokeAsync(StateHasChanged);
    }

    private async Task RefreshDocAsync()
    {
        var doc = activeDoc;
        if (doc != null)
        {
            _rawContentCache.Remove(doc.FileName);
            _renderedHtmlCache.Remove(doc.FileName);
        }
        await LoadDocumentAsync(selectedDocId, forceReload: true);
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadDocumentAsync(string docId, bool forceReload)
    {
        var doc = documents.FirstOrDefault(d => d.Id.Equals(docId, StringComparison.OrdinalIgnoreCase));
        if (doc == null)
            return;

        errorMessage = null;

        // Instant switch if already cached (both raw and HTML if markdown)
        if (!forceReload && _rawContentCache.TryGetValue(doc.FileName, out var cachedRaw))
        {
            if (!doc.IsMarkdown || _renderedHtmlCache.TryGetValue(doc.FileName, out var cachedHtml))
            {
                currentContent = cachedRaw;
                renderedHtml = doc.IsMarkdown ? (_renderedHtmlCache.TryGetValue(doc.FileName, out var h) ? h : string.Empty) : string.Empty;
                isLoading = false;
                await InvokeAsync(StateHasChanged);
                return;
            }
        }

        // Cache miss: Show loading spinner immediately
        isLoading = true;
        currentContent = string.Empty;
        renderedHtml = string.Empty;
        await InvokeAsync(StateHasChanged);

        // Yield to let the browser render the spinner frame before doing work
        await Task.Yield();
        await Task.Delay(16);

        try
        {
            string raw;
            if (!forceReload && _rawContentCache.TryGetValue(doc.FileName, out var cached))
            {
                raw = cached;
            }
            else
            {
                using var client = new HttpClient { BaseAddress = new Uri(NavManager.BaseUri) };
                var url = $"docs/{doc.FileName}" + (forceReload ? $"?t={DateTime.UtcNow.Ticks}" : string.Empty);
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    errorMessage = $"Could not retrieve document '{doc.FileName}' (HTTP {(int)response.StatusCode}: {response.ReasonPhrase}).";
                    currentContent = string.Empty;
                    renderedHtml = string.Empty;
                    isLoading = false;
                    await InvokeAsync(StateHasChanged);
                    return;
                }

                raw = await response.Content.ReadAsStringAsync();
                _rawContentCache[doc.FileName] = raw;
            }

            currentContent = raw;

            if (doc.IsMarkdown)
            {
                // Parse markdown asynchronously with chunking and yielding
                var parsedHtml = await MarkdownService.ToHtmlAsync(raw);
                renderedHtml = parsedHtml;
                _renderedHtmlCache[doc.FileName] = parsedHtml;
            }
            else
            {
                renderedHtml = string.Empty;
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Error fetching document '{doc.FileName}': {ex.Message}";
            currentContent = string.Empty;
            renderedHtml = string.Empty;
        }
        finally
        {
            isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CopyToClipboardAsync()
    {
        if (string.IsNullOrEmpty(currentContent))
            return;

        var success = await Interop.CopyToClipboardAsync(currentContent);
        if (success)
        {
            isCopied = true;
            if (!_isDisposed)
            {
                await InvokeAsync(StateHasChanged);
            }

            await Task.Delay(2000);
            isCopied = false;
            if (!_isDisposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task DownloadActiveDocAsync()
    {
        if (activeDoc == null || string.IsNullOrEmpty(currentContent))
            return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(currentContent);
            using var stream = new MemoryStream(bytes);
            using var streamRef = new DotNetStreamReference(stream);
            await Interop.DownloadFileFromStreamAsync(activeDoc.FileName, streamRef);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Download failed: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _isDisposed = true;
        _dotNetRef?.Dispose();
        return ValueTask.CompletedTask;
    }
}
