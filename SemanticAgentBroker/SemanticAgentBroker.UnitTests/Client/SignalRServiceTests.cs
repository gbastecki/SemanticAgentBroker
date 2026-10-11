using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using Moq;
using SemanticAgentBroker.Client.Features.Auth;
using SemanticAgentBroker.Client.Features.SignalR.Implementations;

namespace SemanticAgentBroker.UnitTests.Client;

[TestClass]
public class SignalRServiceTests
{
    private Mock<IJSRuntime> _jsRuntimeMock = null!;
    private HttpClient _httpClient = null!;
    private CustomAuthStateProvider _authStateProvider = null!;
    private IConfiguration _configuration = null!;

    [TestInitialize]
    public void Setup()
    {
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _jsRuntimeMock
            .Setup(js => js.InvokeAsync<string>("localStorage.getItem", It.Is<object?[]>(args => (string?)args[0] == "browserId")))
            .ReturnsAsync("browser-client-test");

        _httpClient = new HttpClient { BaseAddress = new Uri("https://localhost:5001/") };
        _authStateProvider = new CustomAuthStateProvider(_httpClient, _jsRuntimeMock.Object);

        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ApiBaseUrl"] = "https://agentbroker.local:7000/"
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _httpClient.Dispose();
    }

    [TestMethod]
    public void SignalRService_InitialState_IsDisconnected()
    {
        // Arrange & Act
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        // Assert
        Assert.AreEqual(HubConnectionState.Disconnected, service.State);
    }

    [TestMethod]
    public void SignalRService_WithCustomBaseUrl_ConstructsHubUrlCorrectly()
    {
        // Arrange
        var customConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://my-api-host:8080/prefix/" })
            .Build();

        // Act
        var service = new SignalRService(customConfig, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        // Assert - verified via reflection on private field _hubUrl
        var field = typeof(SignalRService).GetField("_hubUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(field);
        var hubUrl = field.GetValue(service) as string;
        Assert.AreEqual("http://my-api-host:8080/prefix/loghub", hubUrl);
    }

    [TestMethod]
    public void SignalRService_WithoutBaseUrlConfig_FallsBackToDefaultHubUrl()
    {
        // Arrange
        var emptyConfig = new ConfigurationBuilder().Build();

        // Act
        var service = new SignalRService(emptyConfig, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        // Assert
        var field = typeof(SignalRService).GetField("_hubUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(field);
        var hubUrl = field.GetValue(service) as string;
        Assert.AreEqual("https://localhost:5100/loghub", hubUrl);
    }

    [TestMethod]
    public async Task SignalRService_DisposeAsync_DisposesCleanlyWhenNotConnected()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        // Act & Assert - Should dispose cleanly without throwing
        await service.DisposeAsync();
        Assert.AreEqual(HubConnectionState.Disconnected, service.State);
    }

    [TestMethod]
    public async Task SignalRService_DisposeAsync_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        // Act & Assert - Calling DisposeAsync multiple times should be idempotent and not throw ObjectDisposedException
        await service.DisposeAsync();
        await service.DisposeAsync();
        Assert.AreEqual(HubConnectionState.Disconnected, service.State);
    }

    [TestMethod]
    public async Task SendInterruptionAsync_WhenDisconnected_ReturnsFalse()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        // Act
        var result = await service.SendInterruptionAsync(Guid.NewGuid(), "Hello, Stop!");

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task PlanAndStepActions_WhenDisconnected_DoNotThrow()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);
        var taskId = Guid.NewGuid();

        // Act & Assert - All operations should safely check state and complete without throwing
        await service.SubscribeToTaskAsync(taskId);
        await service.UnsubscribeFromTaskAsync(taskId);
        await service.ApproveStepAsync(taskId);
        await service.RejectStepAsync(taskId, "Needs revision");
        await service.ApprovePlanAsync(taskId);
        await service.ModifyPlanAsync(taskId, "### Modified Plan");
        await service.RejectPlanAsync(taskId, "Invalid plan");
        await service.AskPlanQuestionAsync(taskId, "Clarify step 2?");
    }

    [TestMethod]
    public void SignalRService_EventHandlers_CanBeSubscribedAndTriggered()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);
        int eventsFired = 0;

        service.OnLogReceived += _ => eventsFired++;
        service.OnTaskStateChanged += _ => eventsFired++;
        service.OnAgentThought += _ => eventsFired++;
        service.OnPlanGenerated += _ => eventsFired++;
        service.OnToolExecution += _ => eventsFired++;
        service.OnAgentActivity += _ => eventsFired++;
        service.OnPlanReviewRequested += _ => eventsFired++;
        service.OnPlanRevision += _ => eventsFired++;
        service.OnContextCompaction += _ => eventsFired++;
        service.OnWorkspaceFileChanged += _ => eventsFired++;
        service.OnTokenCountUpdated += _ => eventsFired++;
        service.OnConnectionStateChanged += _ => eventsFired++;

        // Act - Trigger all events via reflection
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var eventFields = typeof(SignalRService).GetFields(flags)
            .Where(f => f.FieldType.Name.StartsWith("Action"))
            .ToList();

        Assert.IsTrue(eventFields.Count >= 12, "SignalRService should have at least 12 backing event fields.");

        foreach (var field in eventFields)
        {
            var del = field.GetValue(service) as MulticastDelegate;
            Assert.IsNotNull(del, $"Event backing field {field.Name} should have an attached subscriber.");
            
            // Create dummy parameter if required
            var paramType = del.Method.GetParameters().FirstOrDefault()?.ParameterType;
            object? param = paramType == typeof(HubConnectionState) ? HubConnectionState.Connected :
                            paramType != null && paramType.IsValueType ? Activator.CreateInstance(paramType) :
                            paramType == typeof(string) ? "test" : null;

            del.DynamicInvoke(param);
        }

        // Assert
        Assert.AreEqual(eventFields.Count, eventsFired);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenConnectionFails_ReleasesConnectLockAndDoesNotHang()
    {
        // Arrange - Hub URL pointing to unreachable port
        var customConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://127.0.0.1:1/" })
            .Build();

        var service = new SignalRService(customConfig, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        // Act - First ConnectAsync should initiate connection loop in background and return promptly
        var connectTask = service.ConnectAsync();
        var completedInTime = await Task.WhenAny(connectTask, Task.Delay(2000)) == connectTask;

        // Assert - ConnectAsync must return and release lock within 2 seconds, not loop indefinitely
        Assert.IsTrue(completedInTime, "ConnectAsync should not block indefinitely on connection retry loop.");

        // And second call should not deadlock on _connectLock
        var secondConnectTask = service.ConnectAsync();
        var secondCompletedInTime = await Task.WhenAny(secondConnectTask, Task.Delay(2000)) == secondConnectTask;
        Assert.IsTrue(secondCompletedInTime, "Second ConnectAsync call should not deadlock waiting for _connectLock.");

        await service.DisposeAsync();
    }

    [TestMethod]
    public async Task SubscribeToTaskAsync_TracksSubscription_EvenWhenDisconnected()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);
        var taskId = Guid.NewGuid();

        // Act
        await service.SubscribeToTaskAsync(taskId);

        // Assert
        var prop = typeof(SignalRService).GetProperty("ActiveTaskSubscriptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.IsNotNull(prop, "SignalRService should have ActiveTaskSubscriptions property.");
        var subscriptions = prop.GetValue(service) as System.Collections.Generic.IReadOnlyCollection<Guid>;
        Assert.IsNotNull(subscriptions);
        Assert.IsTrue(subscriptions.Contains(taskId), "ActiveTaskSubscriptions should contain the subscribed task.");
    }

    [TestMethod]
    public async Task UnsubscribeFromTaskAsync_RemovesTrackedSubscription()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);
        var taskId = Guid.NewGuid();
        await service.SubscribeToTaskAsync(taskId);

        // Act
        await service.UnsubscribeFromTaskAsync(taskId);

        // Assert
        var prop = typeof(SignalRService).GetProperty("ActiveTaskSubscriptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.IsNotNull(prop);
        var subscriptions = prop.GetValue(service) as System.Collections.Generic.IReadOnlyCollection<Guid>;
        Assert.IsNotNull(subscriptions);
        Assert.IsFalse(subscriptions.Contains(taskId), "ActiveTaskSubscriptions should no longer contain unsubscribed task.");
    }

    [TestMethod]
    public async Task DisposeAsync_DisposesConnectLock()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        var lockField = typeof(SignalRService).GetField("_connectLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(lockField);
        var sem = (SemaphoreSlim)lockField.GetValue(service)!;

        // Act
        await service.DisposeAsync();

        // Assert - SemaphoreSlim must be disposed
        Assert.Throws<ObjectDisposedException>(() => sem.Wait(0));
    }

    [TestMethod]
    public async Task DisposeAsync_DuringActiveConnectionLoop_DoesNotThrowNullReferenceException()
    {
        // Arrange
        var service = new SignalRService(_configuration, _jsRuntimeMock.Object, _httpClient, _authStateProvider);

        // Act - Start connect and immediately dispose
        var connectTask = service.ConnectAsync();
        await service.DisposeAsync();

        try
        {
            await connectTask;
        }
        catch (Exception ex) when (ex is not NullReferenceException)
        {
            // Any cancellation or connection failure is acceptable, but NOT NullReferenceException
        }

        // Assert - Should complete cleanly
        Assert.AreEqual(HubConnectionState.Disconnected, service.State);
    }
}
