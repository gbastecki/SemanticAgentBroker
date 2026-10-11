using Microsoft.Extensions.Configuration;
using SemanticAgentBroker.Shared.Configuration;

namespace SemanticAgentBroker.UnitTests.Shared.Configuration;

[TestClass]
public class ClientHostOptionsTests
{
    [TestMethod]
    public void ClientHostOptions_DefaultValues_AreProperlyInitialized()
    {
        // Arrange & Act
        var opts = new ClientHostOptions();

        // Assert
        Assert.AreEqual("0.0.0.0", opts.Host);
        Assert.IsNull(opts.HttpPort);
        Assert.AreEqual(7050, opts.HttpsPort);
        Assert.IsFalse(opts.RedirectHttpToHttps);
        Assert.IsNull(opts.Certificate);
        Assert.IsNotNull(opts.ClientSettings);
        Assert.IsNotNull(opts.Cors);
        Assert.IsNotNull(opts.ForwardedHeaders);
        Assert.IsNotNull(opts.SecurityHeaders);
        Assert.IsNotNull(opts.Compression);
        Assert.IsNotNull(opts.StaticFiles);
        Assert.IsNotNull(opts.HealthCheck);
    }

    [TestMethod]
    public void ChildOptions_DefaultValues_MatchExpectedSecurityDefaults()
    {
        // ClientSettings
        var settings = new ClientSettingsOptions();
        Assert.AreEqual("https://localhost:5100/", settings.ApiBaseUrl);
        Assert.AreEqual("Production", settings.EnvironmentName);
        Assert.IsNotNull(settings.CustomSettings);

        // CORS
        var cors = new ClientCorsOptions();
        Assert.IsTrue(cors.AllowAll);
        Assert.IsTrue(cors.AllowCredentials);
        Assert.AreEqual(0, cors.AllowedOrigins.Count);
        Assert.AreEqual(0, cors.AllowedMethods.Count);
        Assert.AreEqual(0, cors.AllowedHeaders.Count);

        // ForwardedHeaders
        var fwd = new ForwardedHeadersConfigurationOptions();
        Assert.IsTrue(fwd.Enabled);
        Assert.IsFalse(fwd.TrustAllProxies);
        Assert.AreEqual(0, fwd.KnownProxies.Count);
        Assert.AreEqual(0, fwd.KnownNetworks.Count);

        // SecurityHeaders
        var sec = new SecurityHeaderOptions();
        Assert.IsFalse(sec.EnableHsts);
        Assert.AreEqual(365, sec.HstsMaxAgeDays);
        Assert.IsTrue(sec.HstsIncludeSubdomains);
        Assert.IsFalse(sec.HstsPreload);
        Assert.AreEqual("nosniff", sec.XContentTypeOptions);
        Assert.AreEqual("SAMEORIGIN", sec.XFrameOptions);
        Assert.AreEqual("strict-origin-when-cross-origin", sec.ReferrerPolicy);
        Assert.IsNull(sec.ContentSecurityPolicy);
        Assert.IsNull(sec.PermissionsPolicy);

        // Compression
        var comp = new CompressionOptions();
        Assert.IsTrue(comp.EnableCompression);
        Assert.IsTrue(comp.EnableForHttps);

        // StaticFiles
        var staticFiles = new StaticFileCachingOptions();
        Assert.AreEqual(31536000, staticFiles.FrameworkCacheMaxAgeSeconds);
        Assert.AreEqual(0, staticFiles.HtmlCacheMaxAgeSeconds);

        // HealthCheck
        var health = new ClientHealthCheckOptions();
        Assert.IsTrue(health.Enabled);
        Assert.AreEqual("/health", health.Path);
    }

    [TestMethod]
    public void ClientHostOptions_BindFromConfiguration_BindsAllPropertiesCorrectly()
    {
        // Arrange
        var inMemory = new Dictionary<string, string?>
        {
            ["ClientHost:Host"] = "127.0.0.1",
            ["ClientHost:HttpPort"] = "5050",
            ["ClientHost:HttpsPort"] = "7070",
            ["ClientHost:RedirectHttpToHttps"] = "true",
            ["ClientHost:ClientSettings:ApiBaseUrl"] = "https://api.internal.corp:5100/",
            ["ClientHost:ClientSettings:EnvironmentName"] = "Staging",
            ["ClientHost:ClientSettings:CustomSettings:CustomKey"] = "CustomValue",
            ["ClientHost:Cors:AllowAll"] = "false",
            ["ClientHost:Cors:AllowedOrigins:0"] = "https://app.internal.corp",
            ["ClientHost:ForwardedHeaders:Enabled"] = "true",
            ["ClientHost:ForwardedHeaders:TrustAllProxies"] = "true",
            ["ClientHost:SecurityHeaders:EnableHsts"] = "true",
            ["ClientHost:SecurityHeaders:XFrameOptions"] = "DENY",
            ["ClientHost:SecurityHeaders:ContentSecurityPolicy"] = "default-src 'self'",
            ["ClientHost:Compression:EnableCompression"] = "false",
            ["ClientHost:StaticFiles:FrameworkCacheMaxAgeSeconds"] = "86400",
            ["ClientHost:HealthCheck:Path"] = "/ping"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        // Act
        var options = config.GetSection("ClientHost").Get<ClientHostOptions>();

        // Assert
        Assert.IsNotNull(options);
        Assert.AreEqual("127.0.0.1", options.Host);
        Assert.AreEqual(5050, options.HttpPort);
        Assert.AreEqual(7070, options.HttpsPort);
        Assert.IsTrue(options.RedirectHttpToHttps);
        Assert.AreEqual("https://api.internal.corp:5100/", options.ClientSettings.ApiBaseUrl);
        Assert.AreEqual("Staging", options.ClientSettings.EnvironmentName);
        Assert.AreEqual("CustomValue", options.ClientSettings.CustomSettings["CustomKey"]);
        Assert.IsFalse(options.Cors.AllowAll);
        Assert.AreEqual("https://app.internal.corp", options.Cors.AllowedOrigins[0]);
        Assert.IsTrue(options.ForwardedHeaders.TrustAllProxies);
        Assert.IsTrue(options.SecurityHeaders.EnableHsts);
        Assert.AreEqual("DENY", options.SecurityHeaders.XFrameOptions);
        Assert.AreEqual("default-src 'self'", options.SecurityHeaders.ContentSecurityPolicy);
        Assert.IsFalse(options.Compression.EnableCompression);
        Assert.AreEqual(86400, options.StaticFiles.FrameworkCacheMaxAgeSeconds);
        Assert.AreEqual("/ping", options.HealthCheck.Path);
    }
}
