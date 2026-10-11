using Microsoft.Extensions.DependencyInjection;
using SemanticAgentBroker.Shared.Configuration;
using System.Net.Http.Headers;

namespace SemanticAgentBroker.Core.Infrastructure.Http;

/// <summary>
/// Extension methods for configuring <see cref="IHttpClientBuilder"/> for AI provider connections.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Configures an HTTP client for a specific AI provider connection, including timeouts, authentication, SSL handling, and trace logging handlers.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="connection">The provider connection configuration.</param>
    /// <param name="timeoutMinutes">Timeout duration in minutes.</param>
    /// <param name="traceLogsDirectory">Directory path for saving HTTP trace logs.</param>
    /// <returns>The configured <see cref="IHttpClientBuilder"/>.</returns>
    public static IHttpClientBuilder ConfigureAiProviderClient(
        this IHttpClientBuilder builder, 
        ProviderConnection connection, 
        int timeoutMinutes,
        string traceLogsDirectory)
    {
        builder.ConfigureHttpClient(client =>
        {
            if (!string.IsNullOrEmpty(connection.Endpoint))
            {
                client.BaseAddress = new Uri(connection.Endpoint);
            }
            
            client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);

            if (!string.IsNullOrEmpty(connection.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
            }
        });

        if (string.IsNullOrEmpty(connection.ApiKey) && !string.IsNullOrEmpty(connection.HttpBasicAuthUsername))
        {
            builder.AddHttpMessageHandler(() => new BasicAuthDelegatingHandler(
                connection.HttpBasicAuthUsername, 
                connection.HttpBasicAuthPassword ?? string.Empty));
        }

        if (connection.TrustUntrustedCertificates)
        {
            builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            });
        }

        builder.AddHttpMessageHandler(() => new OllamaCompatibilityDelegatingHandler(connection));

        builder.AddHttpMessageHandler(sp => 
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TraceLoggingDelegatingHandler>>();
            return new TraceLoggingDelegatingHandler(traceLogsDirectory, connection.Id, logger);
        });

        return builder;
    }
}
