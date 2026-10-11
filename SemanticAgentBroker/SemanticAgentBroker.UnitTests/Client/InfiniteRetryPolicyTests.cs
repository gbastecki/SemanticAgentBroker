using SemanticAgentBroker.Client.Features.SignalR.Implementations;
using System.Net;
using SignalRRetryContext = Microsoft.AspNetCore.SignalR.Client.RetryContext;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class InfiniteRetryPolicyTests
{
    [TestMethod]
    public void NextRetryDelay_BackoffSchedule_FollowsExpectedProgression()
    {
        var policy = new SignalRService.InfiniteRetryPolicy();

        // 0 retries -> 0s
        var context0 = new SignalRRetryContext { PreviousRetryCount = 0, ElapsedTime = TimeSpan.Zero };
        Assert.AreEqual(TimeSpan.Zero, policy.NextRetryDelay(context0));

        // 1 retry -> 2s
        var context1 = new SignalRRetryContext { PreviousRetryCount = 1, ElapsedTime = TimeSpan.FromSeconds(1) };
        Assert.AreEqual(TimeSpan.FromSeconds(2), policy.NextRetryDelay(context1));

        // 2 retries -> 5s
        var context2 = new SignalRRetryContext { PreviousRetryCount = 2, ElapsedTime = TimeSpan.FromSeconds(3) };
        Assert.AreEqual(TimeSpan.FromSeconds(5), policy.NextRetryDelay(context2));

        // 3 retries -> 10s
        var context3 = new SignalRRetryContext { PreviousRetryCount = 3, ElapsedTime = TimeSpan.FromSeconds(8) };
        Assert.AreEqual(TimeSpan.FromSeconds(10), policy.NextRetryDelay(context3));

        // 4+ retries -> 15s
        var context4 = new SignalRRetryContext { PreviousRetryCount = 4, ElapsedTime = TimeSpan.FromSeconds(18) };
        Assert.AreEqual(TimeSpan.FromSeconds(15), policy.NextRetryDelay(context4));

        var context10 = new SignalRRetryContext { PreviousRetryCount = 10, ElapsedTime = TimeSpan.FromMinutes(2) };
        Assert.AreEqual(TimeSpan.FromSeconds(15), policy.NextRetryDelay(context10));
    }

    [TestMethod]
    public void NextRetryDelay_On401UnauthorizedException_ReturnsNullToStopRetrying()
    {
        var policy = new SignalRService.InfiniteRetryPolicy();

        // Scenario 1: Exception message contains "401"
        var contextMsg401 = new SignalRRetryContext
        {
            PreviousRetryCount = 1,
            RetryReason = new Exception("Response status code does not indicate success: 401 (Unauthorized).")
        };
        Assert.IsNull(policy.NextRetryDelay(contextMsg401));

        // Scenario 2: HttpRequestException with HttpStatusCode.Unauthorized
        var contextHttp401 = new SignalRRetryContext
        {
            PreviousRetryCount = 1,
            RetryReason = new HttpRequestException("Unauthorized request", null, HttpStatusCode.Unauthorized)
        };
        Assert.IsNull(policy.NextRetryDelay(contextHttp401));
    }

    [TestMethod]
    public void NextRetryDelay_OnNon401Exception_ContinuesRetrying()
    {
        var policy = new SignalRService.InfiniteRetryPolicy();

        var contextOther = new SignalRRetryContext
        {
            PreviousRetryCount = 2,
            RetryReason = new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable)
        };

        var delay = policy.NextRetryDelay(contextOther);
        Assert.IsNotNull(delay);
        Assert.AreEqual(TimeSpan.FromSeconds(5), delay.Value);
    }

    [TestMethod]
    public void NextRetryDelay_WhenRetryReasonIsNull_FollowsDefaultBackoffProgression()
    {
        // Arrange
        var policy = new SignalRService.InfiniteRetryPolicy();
        var context = new SignalRRetryContext
        {
            PreviousRetryCount = 3,
            RetryReason = null // Explicitly null
        };

        // Act
        var delay = policy.NextRetryDelay(context);

        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(10), delay);
    }
}
