using Microsoft.AspNetCore.Components;
using SemanticAgentBroker.Client.Features.Shared;

namespace SemanticAgentBroker.Client.Layout;

public partial class NavMenu : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private string connectionClass = "disconnected";
    private string connectionText = "Disconnected";

    [Inject] private Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private InteropService InteropService { get; set; } = default!;

    private string currentTheme = "light";
    private bool _isDisposed;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            currentTheme = await InteropService.GetThemeAsync();
        }
        catch { }

        SignalR.OnConnectionStateChanged += UpdateState;
        UpdateState(SignalR.State);

        // Only attempt SignalR connection if the user is authenticated
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        if (authState.User.Identity?.IsAuthenticated != true)
            return;

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested && SignalR.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected)
            {
                try
                {
                    await SignalR.ConnectAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }

                try
                {
                    await Task.Delay(2000, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        if (SignalR != null)
        {
            SignalR.OnConnectionStateChanged -= UpdateState;
        }
    }

    private void UpdateState(Microsoft.AspNetCore.SignalR.Client.HubConnectionState state)
    {
        if (_isDisposed) return;
        (connectionClass, connectionText) = state switch
        {
            Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected => ("connected", "SignalR - Connected"),
            Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Reconnecting => ("reconnecting", "SignalR - Reconnecting..."),
            _ => ("disconnected", "SignalR - Disconnected")
        };
        InvokeAsync(() => { if (!_isDisposed) StateHasChanged(); });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                var theme = await InteropService.GetThemeAsync();
                if (!_isDisposed && theme != currentTheme)
                {
                    currentTheme = theme;
                    StateHasChanged();
                }
            }
            catch { }
        }
    }

    private async Task ToggleTheme()
    {
        var activeTheme = await InteropService.GetThemeAsync();
        currentTheme = activeTheme == "dark" ? "light" : "dark";
        await InteropService.SetThemeAsync(currentTheme);
        if (!_isDisposed) StateHasChanged();
    }
}
