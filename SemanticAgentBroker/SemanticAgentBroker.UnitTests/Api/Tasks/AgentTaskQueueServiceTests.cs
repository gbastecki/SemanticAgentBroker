using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;
using SemanticAgentBroker.Api.Features.ReAct.Services;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Core.Features.Agents.Implementations;
using SemanticAgentBroker.Core.Features.KernelProvider;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Core.Features.TaskState.Implementations;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Core.Features.TokenCounting.Implementations;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Tasks;

[TestClass]
public class AgentTaskQueueServiceTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private Mock<IAgentRegistry> _agentRegistryMock = null!;
    private Mock<IPluginRegistry> _pluginRegistryMock = null!;
    private Mock<IConnectionLoadBalancer> _loadBalancerMock = null!;
    private Mock<IReactExecutionEngine> _reactEngineMock = null!;
    private Mock<IHubContext<AgentLogHub>> _hubMock = null!;
    private Mock<IKernelProviderService> _kernelProviderMock = null!;
    private Mock<PlanningEngine> _planningEngineMock = null!;
    private IServiceProvider _serviceProvider = null!;
    private AgentSystemOptions _sysOptions = null!;
    private AiProviderOptions _aiOptions = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();
        _sysOptions = _dbFixture.CreateSystemOptions();
        _sysOptions.MaxConcurrentAgents = 2;

        _aiOptions = new AiProviderOptions();
        _aiOptions.Connections.Add(new ProviderConnection
        {
            Id = "test-conn",
            ProviderType = "Ollama",
            Endpoint = "http://localhost:11434",
            IsEnabled = true,
            Models = [new ModelConfig { Id = "test-model" }]
        });
        _aiOptions.ModelRouting[TaskModelRole.PrimaryTextModel] = new ModelRouteConfig
        {
            ModelId = "test-model",
            ProviderId = "test-conn"
        };

        _agentRegistryMock = new Mock<IAgentRegistry>();
        _pluginRegistryMock = new Mock<IPluginRegistry>();
        _pluginRegistryMock.Setup(p => p.GetAllPlugins()).Returns([]);
        _pluginRegistryMock.Setup(p => p.GetMethods(It.IsAny<IEnumerable<(string PluginId, string MethodId)>>()!)).Returns([]);
        _loadBalancerMock = new Mock<IConnectionLoadBalancer>();
        _reactEngineMock = new Mock<IReactExecutionEngine>();

        _hubMock = new Mock<IHubContext<AgentLogHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);

        _kernelProviderMock = new Mock<IKernelProviderService>();
        _kernelProviderMock.Setup(k => k.CreateKernelBuilder(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() => Kernel.CreateBuilder());
        _kernelProviderMock.Setup(k => k.CreateKernelBuilderBalancedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (Kernel.CreateBuilder(), "test-conn"));

        var dummyStore = new AgentTaskStore(_dbFixture.CreateAppDbContext(), _hubMock.Object, _sysOptions);
        var dummyOps = new TaskOperationsService();
        var dummyLlm = new LlmCallService(dummyStore, _aiOptions, _sysOptions, NullLogger<LlmCallService>.Instance);
        _planningEngineMock = new Mock<PlanningEngine>(
            dummyStore, dummyOps, _aiOptions, _sysOptions, dummyLlm, NullLogger<PlanningEngine>.Instance, new Mock<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>().Object);

        var services = new ServiceCollection();
        services.AddScoped(_ => _dbFixture.CreateAppDbContext());
        services.AddScoped(_ => _dbFixture.CreateAuthDbContext());
        services.AddScoped(_ => _dbFixture.CreateTokenDbContext());
        services.AddSingleton(_sysOptions);
        services.AddSingleton(_aiOptions);
        services.AddSingleton(_hubMock.Object);
        services.AddScoped<AgentTaskStore>();
        services.AddSingleton<TaskOperationsService>();
        services.AddScoped<IAgentTaskContext, AgentTaskContext>();
        services.AddScoped<ITaskStateManager, TaskStateManager>();
        services.AddSingleton<ITokenCounter, MlTokenCounter>();
        services.AddSingleton<IWorkspaceLockManager, WorkspaceLockManager>();
        services.AddScoped(_ => _reactEngineMock.Object);
        services.AddScoped(_ => _kernelProviderMock.Object);
        services.AddScoped(_ => _planningEngineMock.Object);
        services.AddScoped<IPluginContextFactory>(_ => new Mock<IPluginContextFactory>().Object);
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task EnqueueTaskAsync_And_Process_WhenSessionNotFound_CompletesWithoutError()
    {
        // Arrange
        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var nonExistentTaskId = Guid.NewGuid();
        var request = new AgentTaskRequest
        {
            TaskId = nonExistentTaskId,
            AgentName = "Orchestrator",
            Prompt = "Test task with no DB session"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        // Give background queue processor a moment to dequeue
        await Task.Delay(300);

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert - Task was processed gracefully, no session was created or altered
        using var db = _dbFixture.CreateAppDbContext();
        var session = await db.TaskSessions.FindAsync(nonExistentTaskId);
        Assert.IsNull(session);
    }

    [TestMethod]
    public async Task StartAsync_And_StopAsync_CancelsAndShutsDownCleanly()
    {
        // Arrange
        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        Assert.IsNotNull(queueService.ExecuteTask, "Background execution task should have been created by StartAsync");
        Assert.IsFalse(cts.IsCancellationRequested);

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(cts.IsCancellationRequested);
    }

    [TestMethod]
    public async Task RunTaskAsync_WhenCriticalExceptionOccurs_UpdatesStateToFailed()
    {
        // Arrange - Create a task in DB
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "FailingAgent",
                UserPrompt = "Failing task",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Configure agent registry to throw an exception
        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Throws(new InvalidOperationException("Agent definition corrupted"));

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "FailingAgent",
            Prompt = "Failing task prompt"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        // Wait deterministically for queue processing and fallback failure handling
        TaskSession? session = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            using (var verifyDb = _dbFixture.CreateAppDbContext())
            {
                session = await verifyDb.TaskSessions.FindAsync(taskId);
                if (session?.State == TaskState.Failed)
                    break;
            }
            await Task.Delay(50);
        }

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert - Task state should be updated to Failed with error message
        Assert.IsNotNull(session);
        Assert.AreEqual(TaskState.Failed, session.State);
    }

    [TestMethod]
    public async Task RunTaskAsync_WhenLlmTimesOut_UpdatesStateToFailedWithTimeoutMessage()
    {
        // Arrange - Create a task in DB
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TimeoutAgent",
                UserPrompt = "Timeout prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Configure agent registry to throw an OperationCanceledException with inner TimeoutException
        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Throws(new OperationCanceledException("Operation timed out", new TimeoutException("LLM gateway timeout")));

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TimeoutAgent",
            Prompt = "Timeout prompt"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        TaskSession? session = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            using (var verifyDb = _dbFixture.CreateAppDbContext())
            {
                session = await verifyDb.TaskSessions.FindAsync(taskId);
                if (session?.State == TaskState.Failed)
                    break;
            }
            await Task.Delay(50);
        }

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var verifiedSession = await verifyDb.TaskSessions.FindAsync(taskId);
            var events = await verifyDb.TaskEvents.Where(e => e.SessionId == taskId).ToListAsync();
            Assert.IsNotNull(verifiedSession);
            Assert.AreEqual(TaskState.Failed, verifiedSession.State);
            Assert.IsTrue(events.Any(e => e.Payload.Contains("timed out", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public async Task RunTaskAsync_WhenUserCancels_UpdatesStateToCancelled()
    {
        // Arrange - Create a task in DB
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "CancelAgent",
                UserPrompt = "Cancel prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var taskOps = _serviceProvider.GetRequiredService<TaskOperationsService>();

        // When agent registry resolves, trigger cancellation
        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Callback(() => taskOps.CancelTask(taskId))
            .Throws(new OperationCanceledException());

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "CancelAgent",
            Prompt = "Cancel prompt"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        TaskSession? session = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            using (var verifyDb = _dbFixture.CreateAppDbContext())
            {
                session = await verifyDb.TaskSessions.FindAsync(taskId);
                if (session?.State == TaskState.Cancelled)
                    break;
            }
            await Task.Delay(50);
        }

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert
        Assert.IsNotNull(session);
        Assert.AreEqual(TaskState.Cancelled, session.State);
    }

    [TestMethod]
    public async Task RunTaskAsync_WhenGlobalTimeoutExpires_UpdatesStateToFailed()
    {
        // Arrange - Create a task in DB
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TimeoutAgent",
                UserPrompt = "Timeout prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Configure a 1-second global timeout
        _sysOptions.GlobalTaskTimeoutSeconds = 1;

        // When agent registry resolves, delay to allow the 1-second global timeout to expire
        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(() =>
            {
                Thread.Sleep(1200);
                throw new OperationCanceledException();
            });

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TimeoutAgent",
            Prompt = "Timeout prompt"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        TaskSession? session = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            using (var verifyDb = _dbFixture.CreateAppDbContext())
            {
                session = await verifyDb.TaskSessions.FindAsync(taskId);
                if (session?.State == TaskState.Failed)
                    break;
            }
            await Task.Delay(50);
        }

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert - Must be Failed, NOT Cancelled
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var verifiedSession = await verifyDb.TaskSessions.FindAsync(taskId);
            var events = await verifyDb.TaskEvents.Where(e => e.SessionId == taskId).ToListAsync();
            Assert.IsNotNull(verifiedSession);
            Assert.AreEqual(TaskState.Failed, verifiedSession.State);
            Assert.IsTrue(events.Any(e => e.Payload.Contains("exceeded the maximum allowed execution time", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public async Task RunTaskAsync_WhenHttpClientTimesOut_SetsHttpTimeoutDiagnosticMessage()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TimeoutAgent",
                UserPrompt = "Http timeout prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Simulate HttpClient throwing TaskCanceledException with inner TimeoutException during agent resolution / execution
        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Throws(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException("The operation timed out")));

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TimeoutAgent",
            Prompt = "Http timeout prompt"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        TaskSession? session = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            using (var verifyDb = _dbFixture.CreateAppDbContext())
            {
                session = await verifyDb.TaskSessions.FindAsync(taskId);
                if (session?.State == TaskState.Failed)
                    break;
            }
            await Task.Delay(50);
        }

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var verifiedSession = await verifyDb.TaskSessions.FindAsync(taskId);
            var events = await verifyDb.TaskEvents.Where(e => e.SessionId == taskId).ToListAsync();
            Assert.IsNotNull(verifiedSession);
            Assert.AreEqual(TaskState.Failed, verifiedSession.State);
            Assert.IsTrue(events.Any(e => e.Payload.Contains("HTTP request timed out while waiting for a response from the LLM. Try again.", StringComparison.OrdinalIgnoreCase)),
                $"Expected HTTP timeout message in events, but found: {string.Join(" | ", events.Select(e => e.Payload))}");
        }
    }

    [TestMethod]
    public async Task BuildAgentKernel_ExceptionAfterConnectionAcquisition_ReleasesConnection()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Test prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Configure routing without sticky provider to trigger load balancer connection acquisition
        _aiOptions.ModelRouting[TaskModelRole.PrimaryTextModel] = new ModelRouteConfig
        {
            ModelId = "test-model",
            ProviderId = null
        };

        var kernelProviderMock = new Mock<IKernelProviderService>();
        var builder = Microsoft.SemanticKernel.Kernel.CreateBuilder();
        kernelProviderMock
            .Setup(kp => kp.CreateKernelBuilderBalancedAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync((builder, "conn-to-release"));

        // Setup plugin registry to throw an exception AFTER connection acquisition
        _pluginRegistryMock.Setup(p => p.GetAllPlugins())
            .Throws(new InvalidOperationException("Simulated plugin scan error during agent build"));

        var services = new ServiceCollection();
        services.AddScoped(_ => _dbFixture.CreateAppDbContext());
        services.AddScoped(_ => _dbFixture.CreateAuthDbContext());
        services.AddScoped(_ => _dbFixture.CreateTokenDbContext());
        services.AddSingleton(_sysOptions);
        services.AddSingleton(_aiOptions);
        services.AddSingleton(_hubMock.Object);
        services.AddScoped<AgentTaskStore>();
        services.AddSingleton<TaskOperationsService>();
        services.AddScoped<IAgentTaskContext, AgentTaskContext>();
        services.AddScoped<ITaskStateManager, TaskStateManager>();
        services.AddSingleton<ITokenCounter, MlTokenCounter>();
        services.AddSingleton<IWorkspaceLockManager, WorkspaceLockManager>();
        services.AddScoped(_ => _reactEngineMock.Object);
        services.AddScoped(_ => kernelProviderMock.Object);
        services.AddLogging();
        await using var customSp = services.BuildServiceProvider();

        var queueService = new AgentTaskQueueService(
            customSp,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TestAgent",
            Prompt = "Test prompt"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        // Wait until task fails
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            using var verifyDb = _dbFixture.CreateAppDbContext();
            var session = await verifyDb.TaskSessions.FindAsync(taskId);
            if (session?.State == TaskState.Failed)
                break;
            await Task.Delay(50);
        }

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert
        _loadBalancerMock.Verify(lb => lb.ReleaseConnection("conn-to-release"), Times.Once,
            "Load balancer connection must be released when exception occurs during agent building");
    }

    [TestMethod]
    public async Task EnqueueTaskAsync_DuplicateTaskId_SkipsSecondExecution()
    {
        // Arrange - Create a task in DB
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "SlowAgent",
                UserPrompt = "Slow task",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        int executionCount = 0;
        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Callback(() =>
            {
                Interlocked.Increment(ref executionCount);
                Thread.Sleep(500); // Simulate slow task
            })
            .Throws(new InvalidOperationException("Expected"));

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        using var cts = new CancellationTokenSource();
        await queueService.StartAsync(cts.Token);

        // Act - Enqueue same TaskId twice rapidly
        var request = new AgentTaskRequest { TaskId = taskId, AgentName = "SlowAgent", Prompt = "Slow task" };
        await queueService.EnqueueTaskAsync(request);
        await queueService.EnqueueTaskAsync(request);

        await Task.Delay(1500);
        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert - Only ONE execution should have occurred
        Assert.AreEqual(1, executionCount,
            "Duplicate task ID must be rejected, not executed concurrently.");
    }

    [TestMethod]
    public async Task RunTaskAsync_WhenTaskPausedInDbWhileQueued_DoesNotExecuteOrChangeStateToInProgress()
    {
        // Arrange - Create a task in DB that was paused while queued
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Test prompt",
                State = TaskState.Paused,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        bool agentResolved = false;
        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Callback(() => agentResolved = true)
            .Returns(new AgentDefinition { Name = "TestAgent", SystemInstruction = "Prompt" });

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TestAgent",
            Prompt = "Test prompt"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        // Allow queue loop time to process the item
        await Task.Delay(400);

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert - Task state must remain Paused in DB, and agent execution should NOT have started
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var verifiedSession = await verifyDb.TaskSessions.FindAsync(taskId);
            Assert.IsNotNull(verifiedSession);
            Assert.AreEqual(TaskState.Paused, verifiedSession.State);
            Assert.IsFalse(agentResolved, "Agent should not be resolved or executed when task is paused.");
        }
    }

    [TestMethod]
    public async Task RunTaskAsync_WhenPlanningReturnsNull_TransitionsToFailedState()
    {
        // Arrange - Create a task in DB
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "PlanAgent",
                UserPrompt = "Plan prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(new AgentDefinition { Name = "PlanAgent", SystemInstruction = "System Prompt", MethodBindings = [] });

        _planningEngineMock.Setup(p => p.GeneratePlanAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "PlanAgent",
            Prompt = "Plan prompt"
        };

        using var cts = new CancellationTokenSource();

        // Act
        await queueService.StartAsync(cts.Token);
        await queueService.EnqueueTaskAsync(request);

        TaskSession? session = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            using (var verifyDb = _dbFixture.CreateAppDbContext())
            {
                session = await verifyDb.TaskSessions.FindAsync(taskId);
                if (session?.State == TaskState.Failed || session?.State == TaskState.Cancelled)
                    break;
            }
            await Task.Delay(50);
        }

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert - Task state must be Failed, NOT Cancelled
        using (var verifyDb = _dbFixture.CreateAppDbContext())
        {
            var verifiedSession = await verifyDb.TaskSessions.FindAsync(taskId);
            var events = await verifyDb.TaskEvents.Where(e => e.SessionId == taskId).ToListAsync();
            Assert.IsNotNull(verifiedSession);
            var errorEvents = string.Join(" | ", events.Select(e => $"[{e.EventType}] {e.Payload}"));
            Assert.AreEqual(TaskState.Failed, verifiedSession.State, errorEvents);
            _planningEngineMock.Verify(p => p.GeneratePlanAsync(
                taskId,
                It.IsAny<Kernel>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<SemaphoreSlim>(),
                It.IsAny<SemaphoreOwnershipState>(),
                It.IsAny<CancellationToken>()), Times.Once, $"Invocations missed. Events: {errorEvents}");
            Assert.IsTrue(events.Any(e => e.Payload.Contains("Planning phase did not produce an approved plan", StringComparison.OrdinalIgnoreCase)), errorEvents);
        }
    }

    [TestMethod]
    public async Task AgentTaskQueueService_InFlightTrackingAndEarlyRegistration_RegistersImmediately()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Immediate registration prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var executionStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(new AgentDefinition { Name = "TestAgent", SystemInstruction = "System Prompt", MethodBindings = [] });

        _planningEngineMock.Setup(p => p.GeneratePlanAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                executionStartedTcs.TrySetResult(true);
                await allowCompletionTcs.Task;
                return "1. Step one";
            });

        _reactEngineMock.Setup(e => e.ExecuteAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<int?>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("Completed result");

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TestAgent",
            Prompt = "Immediate registration prompt"
        };

        var taskOps = _serviceProvider.GetRequiredService<TaskOperationsService>();

        // Act 1: Enqueue task
        await queueService.EnqueueTaskAsync(request);
        Assert.IsTrue(queueService.IsTaskQueued(taskId), "Task must be tracked as queued.");
        Assert.IsTrue(queueService.IsTaskInFlight(taskId), "Task must be tracked as in flight.");

        // Act 2: Start queue processing
        using var cts = new CancellationTokenSource();
        await queueService.StartAsync(cts.Token);

        await executionStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Assert while execution is active
        Assert.IsTrue(queueService.IsTaskExecuting(taskId), "Task must be tracked as executing.");
        Assert.IsTrue(taskOps.IsTaskActive(taskId), "Task must be active in TaskOperationsService.");

        // Unblock completion
        allowCompletionTcs.SetResult(true);

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        Assert.IsFalse(queueService.IsTaskInFlight(taskId), "Task must no longer be in flight after completion.");
        Assert.IsFalse(taskOps.IsTaskActive(taskId), "Task must be unregistered from TaskOperationsService after completion.");
    }

    [TestMethod]
    public async Task IsTaskInFlight_NeverReturnsFalse_DuringQueueToActiveTransition()
    {
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Transition test prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var holdExecutionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(new AgentDefinition { Name = "TestAgent", SystemInstruction = "System Prompt", MethodBindings = [] });

        _planningEngineMock.Setup(p => p.GeneratePlanAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                executionStartedTcs.TrySetResult(true);
                await holdExecutionTcs.Task;
                return "1. Step one";
            });

        _reactEngineMock.Setup(e => e.ExecuteAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<int?>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                executionStartedTcs.TrySetResult(true);
                await holdExecutionTcs.Task;
                return "Completed";
            });

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TestAgent",
            Prompt = "Transition test prompt"
        };

        await queueService.EnqueueTaskAsync(request);

        using var cts = new CancellationTokenSource();
        var monitorCts = new CancellationTokenSource();
        bool observedFalseInFlight = false;

        // Monitor polling IsTaskInFlight at high frequency during transition
        var monitorTask = Task.Run(() =>
        {
            while (!monitorCts.Token.IsCancellationRequested)
            {
                if (!queueService.IsTaskInFlight(taskId))
                {
                    observedFalseInFlight = true;
                    break;
                }
                Thread.SpinWait(10);
            }
        });

        await queueService.StartAsync(cts.Token);
        await executionStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        monitorCts.Cancel();
        await monitorTask;

        holdExecutionTcs.SetResult(true);
        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        Assert.IsFalse(observedFalseInFlight, "IsTaskInFlight must never return false while transitioning from queued to active.");
    }

    [TestMethod]
    public async Task EnqueueTaskAsync_WhenTaskSupersededInQueue_ExecutesLatestRequestAndDiscardsOld()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using var setupDb = _dbFixture.CreateAppDbContext();
        setupDb.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Continuation Prompt",
            State = TaskState.Draft
        });
        await setupDb.SaveChangesAsync();

        var executedPrompts = new List<string>();
        var completionTcs = new TaskCompletionSource<bool>();

        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(new AgentDefinition { Name = "TestAgent", SystemInstruction = "System Prompt", MethodBindings = [] });

        _planningEngineMock.Setup(p => p.GeneratePlanAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("1. Step one");

        _reactEngineMock.Setup(e => e.ExecuteAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<int?>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .Returns<Guid, Kernel, string, string, bool, bool, int?, SemaphoreSlim, SemaphoreOwnershipState, CancellationToken>(
                (id, k, sys, prompt, debug, cont, max, sem, semState, ct) =>
                {
                    executedPrompts.Add(prompt);
                    completionTcs.TrySetResult(true);
                    return Task.FromResult("Completed");
                });

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var req1 = new AgentTaskRequest { TaskId = taskId, AgentName = "TestAgent", Prompt = "Initial Prompt" };
        var req2 = new AgentTaskRequest { TaskId = taskId, AgentName = "TestAgent", Prompt = "Continuation Prompt", IsContinuation = true };

        // Enqueue req1, then supersede with req2 before worker dispatcher starts
        await queueService.EnqueueTaskAsync(req1);
        await queueService.EnqueueTaskAsync(req2);

        using var cts = new CancellationTokenSource();
        await queueService.StartAsync(cts.Token);

        await completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        await queueService.StopAsync(CancellationToken.None);

        // Assert - The stale request should be skipped; only the latest continuation request must execute
        Assert.AreEqual(1, executedPrompts.Count, "Only the latest request should have executed.");
        Assert.AreEqual("Continuation Prompt", executedPrompts[0], "The continuation request prompt was dropped due to in-flight queue race.");
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenHostShutdownRequested_DoesNotHangOrDeadlock()
    {
        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var taskId = Guid.NewGuid();
        var req = new AgentTaskRequest { TaskId = taskId, AgentName = "TestAgent", Prompt = "Shutdown test prompt" };
        await queueService.EnqueueTaskAsync(req);

        using var cts = new CancellationTokenSource();
        await queueService.StartAsync(cts.Token);

        cts.Cancel();
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await queueService.StopAsync(stopCts.Token);
    }

    [TestMethod]
    public async Task CancelQueuedTask_WhenRequestEnqueued_SkipsExecutionAndDoesNotRunTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        using var setupDb = _dbFixture.CreateAppDbContext();
        setupDb.TaskSessions.Add(new TaskSession
        {
            Id = taskId,
            AgentName = "TestAgent",
            UserPrompt = "Cancelled Queued Prompt",
            State = TaskState.Queued
        });
        await setupDb.SaveChangesAsync();

        bool executionInvoked = false;

        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(new AgentDefinition { Name = "TestAgent", SystemInstruction = "System Prompt", MethodBindings = [] });

        _planningEngineMock.Setup(p => p.GeneratePlanAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                executionInvoked = true;
                return "1. Step one";
            });

        _reactEngineMock.Setup(e => e.ExecuteAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<int?>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                executionInvoked = true;
                return "Completed";
            });

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var req = new AgentTaskRequest { TaskId = taskId, AgentName = "TestAgent", Prompt = "Cancelled Queued Prompt" };

        // Act: Enqueue, then cancel before worker starts processing
        await queueService.EnqueueTaskAsync(req);
        queueService.CancelQueuedTask(taskId);

        using var cts = new CancellationTokenSource();
        await queueService.StartAsync(cts.Token);

        // Allow time for the queue reader to process the item from the channel
        await Task.Delay(300);

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        // Assert - Cancelled queued task must NOT have been executed
        Assert.IsFalse(executionInvoked, "Cancelled queued task was executed by queue worker dispatcher!");
        Assert.IsFalse(queueService.IsTaskInFlight(taskId), "Task should not be in flight after being cancelled.");
    }

    [TestMethod]
    public async Task RunTaskAsync_WhenTaskPausedInQueueAndResumedBeforeDispatch_DoesNotHangInPausedState()
    {
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Resume test prompt",
                State = TaskState.Paused, // Stale state in DB snapshot prior to resume
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var executionStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(new AgentDefinition { Name = "TestAgent", SystemInstruction = "System Prompt", MethodBindings = [] });

        _planningEngineMock.Setup(p => p.GeneratePlanAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("1. Step one");

        _reactEngineMock.Setup(e => e.ExecuteAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<int?>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                executionStartedTcs.TrySetResult(true);
                await Task.CompletedTask;
                return "Execution Success";
            });

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        // Simulate user clicking Resume via TaskResume endpoint:
        // taskOps is resumed and DB state is set to Queued
        var taskOps = _serviceProvider.GetRequiredService<TaskOperationsService>();
        taskOps.ResumeTask(taskId);
        using (var db = _dbFixture.CreateAppDbContext())
        {
            var s = await db.TaskSessions.FindAsync(taskId);
            s!.State = TaskState.Queued;
            await db.SaveChangesAsync();
        }

        var req = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TestAgent",
            Prompt = "Resume test prompt"
        };

        await queueService.EnqueueTaskAsync(req);

        using var cts = new CancellationTokenSource();
        await queueService.StartAsync(cts.Token);

        // Verifies that RunTaskAsync does not evaluate stale session.State == TaskState.Paused,
        // call PauseTask(taskId), and hang indefinitely on WaitIfPausedAsync.
        var delayTask = Task.Delay(TimeSpan.FromSeconds(2));
        var completed = await Task.WhenAny(executionStartedTcs.Task, delayTask);

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);

        Assert.AreEqual(executionStartedTcs.Task, completed,
            "Task dispatch hung in paused state because it re-paused an already-resumed task based on stale DB session state!");
    }

    [TestMethod]
    public async Task Dispatch_TaskIsImmediatelyActiveAndTracked_EliminatingRaceWindow()
    {
        var taskId = Guid.NewGuid();
        using (var db = _dbFixture.CreateAppDbContext())
        {
            db.TaskSessions.Add(new TaskSession
            {
                Id = taskId,
                AgentName = "TestAgent",
                UserPrompt = "Immediate tracking prompt",
                State = TaskState.Queued,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var holdExecutionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agentRegistryMock.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(new AgentDefinition { Name = "TestAgent", SystemInstruction = "System Prompt", MethodBindings = [] });

        _planningEngineMock.Setup(p => p.GeneratePlanAsync(
            taskId,
            It.IsAny<Kernel>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<SemaphoreSlim>(),
            It.IsAny<SemaphoreOwnershipState>(),
            It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                executionStartedTcs.TrySetResult(true);
                await holdExecutionTcs.Task;
                return "1. Step one";
            });

        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            _sysOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        var taskOps = _serviceProvider.GetRequiredService<TaskOperationsService>();

        var req = new AgentTaskRequest
        {
            TaskId = taskId,
            AgentName = "TestAgent",
            Prompt = "Immediate tracking prompt"
        };

        await queueService.EnqueueTaskAsync(req);

        using var cts = new CancellationTokenSource();
        await queueService.StartAsync(cts.Token);

        // Wait until task starts executing
        await executionStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Task must be active in both queue and taskOps
        Assert.IsTrue(queueService.IsTaskExecuting(taskId), "Task should be tracked as executing in queue.");
        Assert.IsTrue(taskOps.IsTaskActive(taskId), "Task should be tracked as active in taskOps.");

        // WaitForTaskCompletionAsync on queue should return false while task is executing
        var finishedPrematurely = await queueService.WaitForTaskCompletionAsync(taskId, TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(finishedPrematurely, "queue.WaitForTaskCompletionAsync returned true while task was still running!");

        // Cancel task and unblock
        taskOps.CancelTask(taskId);
        holdExecutionTcs.TrySetResult(true);

        // Awaiting completion on queue should now return true
        var completed = await queueService.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(3));
        Assert.IsTrue(completed, "queue.WaitForTaskCompletionAsync should return true after task finishes.");

        cts.Cancel();
        await queueService.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public void Constructor_WhenMaxConcurrentAgentsZeroOrNegative_DefaultsToOneWithoutThrowing()
    {
        var invalidOptions = new AgentSystemOptions { MaxConcurrentAgents = 0 };
        var queueService = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            invalidOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        Assert.IsNotNull(queueService);

        var negativeOptions = new AgentSystemOptions { MaxConcurrentAgents = -5 };
        var queueService2 = new AgentTaskQueueService(
            _serviceProvider,
            NullLogger<AgentTaskQueueService>.Instance,
            _aiOptions,
            negativeOptions,
            _agentRegistryMock.Object,
            _pluginRegistryMock.Object,
            _loadBalancerMock.Object);

        Assert.IsNotNull(queueService2);
    }
}

