using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SemanticAgentBroker.Client;
using SemanticAgentBroker.Client.Features.Markdown;
using SemanticAgentBroker.Client.Features.Markdown.Implementations;
using SemanticAgentBroker.Client.Features.SignalR;
using SemanticAgentBroker.Client.Features.SignalR.Implementations;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Use a single base URL for both HTTP and SignalR
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5100/";
builder.Services.AddScoped<SemanticAgentBroker.Client.Features.Auth.AuthHeaderHandler>();
builder.Services.AddScoped<SemanticAgentBroker.Client.Features.Auth.UnauthorizedResponseHandler>();
builder.Services.AddScoped(sp => 
{
    var authHandler = sp.GetRequiredService<SemanticAgentBroker.Client.Features.Auth.AuthHeaderHandler>();
    var unauthHandler = sp.GetRequiredService<SemanticAgentBroker.Client.Features.Auth.UnauthorizedResponseHandler>();
    authHandler.InnerHandler = unauthHandler;
    unauthHandler.InnerHandler = new HttpClientHandler();
    return new HttpClient(authHandler) 
    { 
        BaseAddress = new Uri(apiBaseUrl),
        Timeout = TimeSpan.FromSeconds(60)
    };
});

builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, SemanticAgentBroker.Client.Features.Auth.CustomAuthStateProvider>();
builder.Services.AddScoped<SemanticAgentBroker.Client.Features.Auth.CustomAuthStateProvider>(sp => (SemanticAgentBroker.Client.Features.Auth.CustomAuthStateProvider)sp.GetRequiredService<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>());
builder.Services.AddScoped<SemanticAgentBroker.Client.Features.Auth.ClientAuthService>();

builder.Services.AddScoped<ISignalRService, SignalRService>();
builder.Services.AddSingleton<IMarkdownService, MarkdownService>();
builder.Services.AddScoped<SemanticAgentBroker.Client.Features.Shared.InteropService>();

await builder.Build().RunAsync();
