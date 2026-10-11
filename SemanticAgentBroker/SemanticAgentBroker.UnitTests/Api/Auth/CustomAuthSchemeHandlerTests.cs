using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SemanticAgentBroker.Api.Features.Auth;
using System.Text.Encodings.Web;

namespace SemanticAgentBroker.UnitTests.Api.Auth;

[TestClass]
public class CustomAuthSchemeHandlerTests
{
    private class TestableCustomAuthSchemeHandler : CustomAuthSchemeHandler
    {
        public TestableCustomAuthSchemeHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        public Task<AuthenticateResult> TestHandleAuthenticateAsync() => HandleAuthenticateAsync();
        public Task TestHandleForbiddenAsync(AuthenticationProperties properties) => HandleForbiddenAsync(properties);
        public Task TestHandleChallengeAsync(AuthenticationProperties properties) => HandleChallengeAsync(properties);
    }

    [TestMethod]
    public async Task HandleAuthenticateAsync_ReturnsNoResult()
    {
        // Arrange
        var optionsMock = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMock.Setup(o => o.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());

        var handler = new TestableCustomAuthSchemeHandler(
            optionsMock.Object,
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        var scheme = new AuthenticationScheme("CustomAuth", "CustomAuth", typeof(CustomAuthSchemeHandler));
        var context = new DefaultHttpContext();
        await handler.InitializeAsync(scheme, context);

        // Act
        var result = await handler.TestHandleAuthenticateAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.None);
    }

    [TestMethod]
    public async Task HandleForbiddenAsync_SetsResponseStatus403()
    {
        // Arrange
        var optionsMock = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMock.Setup(o => o.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());

        var handler = new TestableCustomAuthSchemeHandler(
            optionsMock.Object,
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        var scheme = new AuthenticationScheme("CustomAuth", "CustomAuth", typeof(CustomAuthSchemeHandler));
        var context = new DefaultHttpContext();
        await handler.InitializeAsync(scheme, context);

        // Act
        await handler.TestHandleForbiddenAsync(new AuthenticationProperties());

        // Assert
        Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task HandleChallengeAsync_SetsResponseStatus401()
    {
        // Arrange
        var optionsMock = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMock.Setup(o => o.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());

        var handler = new TestableCustomAuthSchemeHandler(
            optionsMock.Object,
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        var scheme = new AuthenticationScheme("CustomAuth", "CustomAuth", typeof(CustomAuthSchemeHandler));
        var context = new DefaultHttpContext();
        await handler.InitializeAsync(scheme, context);

        // Act
        await handler.TestHandleChallengeAsync(new AuthenticationProperties());

        // Assert
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }
}
