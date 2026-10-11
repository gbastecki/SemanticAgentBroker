using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SemanticAgentBroker.Client.Features.Shared;

public class InteropService : IAsyncDisposable
{
    private Task<IJSObjectReference>? _moduleTask;
    private readonly object _moduleLock = new();
    private readonly IJSRuntime _jsRuntime;

    public InteropService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    private Task<IJSObjectReference> GetModuleAsync()
    {
        var task = _moduleTask;
        if (task is { IsFaulted: false, IsCanceled: false })
        {
            return task;
        }

        lock (_moduleLock)
        {
            if (_moduleTask is { IsFaulted: false, IsCanceled: false })
            {
                return _moduleTask;
            }

            _moduleTask = _jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "/js/interop.js").AsTask();
            return _moduleTask;
        }
    }

    public async ValueTask ScrollToBottomAsync(string selector)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("scrollToBottom", selector);
    }

    public async ValueTask RegisterLogScrollListenerAsync(string selector, object dotNetHelper)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("registerLogScrollListener", selector, dotNetHelper);
    }

    public async ValueTask PreserveScrollAsync(string selector, double previousScrollHeight)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("preserveScroll", selector, previousScrollHeight);
    }

    public async ValueTask<double> GetScrollHeightAsync(string selector)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<double>("getScrollHeight", selector);
    }

    public async ValueTask SetElementHtmlAsync(ElementReference element, string html, bool attachCopy = false)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("setElementHtml", element, html, attachCopy);
    }

    public async ValueTask SetElementHtmlAsync(string selector, string html, bool attachCopy = false)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("setElementHtml", selector, html, attachCopy);
    }

    public async ValueTask AttachCopyButtonsAsync(string? containerSelector = null)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("attachCopyButtons", containerSelector);
    }

    public async ValueTask InitDocViewerLinkHandlerAsync(string containerSelector, object dotNetHelper)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("initDocViewerLinkHandler", containerSelector, dotNetHelper);
    }

    public async ValueTask ScrollToDocAnchorAsync(string containerSelector, string anchorId)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("scrollToDocAnchor", containerSelector, anchorId);
    }

    public async ValueTask<bool> CopyToClipboardAsync(string text)
    {
        try
        {
            var module = await GetModuleAsync();
            return await module.InvokeAsync<bool>("copyToClipboard", text);
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask ScrollSelectOptionIntoViewAsync(Microsoft.AspNetCore.Components.ElementReference containerRef)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("scrollSelectOptionIntoView", containerRef);
    }

    public async ValueTask DownloadFileFromStreamAsync(string fileName, DotNetStreamReference contentStreamReference)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("downloadFileFromStream", fileName, contentStreamReference);
    }

    public async ValueTask SetThemeAsync(string theme)
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("window.themeManager.setTheme", theme);
        }
        catch
        {
            try
            {
                var module = await GetModuleAsync();
                await module.InvokeVoidAsync("setTheme", theme);
            }
            catch { }
        }
    }

    public async ValueTask<string> GetThemeAsync()
    {
        try
        {
            var res = await _jsRuntime.InvokeAsync<string>("window.themeManager.getTheme");
            if (!string.IsNullOrEmpty(res)) return res;
        }
        catch { }

        try
        {
            var module = await GetModuleAsync();
            return await module.InvokeAsync<string>("getTheme");
        }
        catch
        {
            return "light";
        }
    }

    public async ValueTask<bool> ConfirmAsync(string message)
    {
        return await _jsRuntime.InvokeAsync<bool>("confirm", message);
    }

    public async ValueTask AlertAsync(string message)
    {
        await _jsRuntime.InvokeVoidAsync("alert", message);
    }

    public async ValueTask PositionDropdownAsync(ElementReference input, ElementReference dropdown, DotNetObjectReference<object> dotNetRef)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("positionDropdown", input, dropdown, dotNetRef);
    }

    public async ValueTask CleanupDropdownAsync()
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("cleanupDropdown");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Task<IJSObjectReference>? task;
            lock (_moduleLock)
            {
                task = _moduleTask;
            }

            if (task is { IsCompletedSuccessfully: true })
            {
                var module = await task;
                try
                {
                    await module.InvokeVoidAsync("cleanupDropdown");
                }
                catch
                {
                    // Ignore JSDisconnectedException / ObjectDisposedException on teardown
                }
                await module.DisposeAsync();
            }
        }
        catch
        {
            // Ignore teardown errors
        }
    }
}


