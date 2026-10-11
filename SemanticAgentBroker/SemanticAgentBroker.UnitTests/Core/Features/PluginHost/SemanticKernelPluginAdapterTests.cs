using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.PluginHost.Implementations;
using SemanticAgentBroker.Sdk;

namespace SemanticAgentBroker.UnitTests.Core.Features.PluginHost;

public class MockPluginMethod : PluginMethodBase
{
    public override string Id => "CalculateTax";
    public override string DisplayName => "Calculate Tax";
    public override string Description => "Calculates tax on amount";
    public override bool IsDangerous => false;

    public override IReadOnlyList<PluginParameter> Parameters =>
    [
        new PluginParameter
        {
            Name = "amount",
            Description = "Amount to tax",
            ParameterType = typeof(double),
            IsRequired = true
        },
        new PluginParameter
        {
            Name = "rate",
            Description = "Tax rate percentage",
            ParameterType = typeof(double),
            IsRequired = false,
            DefaultValue = 0.20
        }
    ];

    public override Task<string> ExecuteAsync(IDictionary<string, object?> arguments, IPluginContext context, CancellationToken ct = default)
    {
        var amount = Convert.ToDouble(arguments["amount"]);
        var rate = arguments.TryGetValue("rate", out var rVal) && rVal != null ? Convert.ToDouble(rVal) : 0.20;
        var tax = amount * rate;
        return Task.FromResult($"Tax: {tax.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
    }
}

[TestClass]
public class SemanticKernelPluginAdapterTests
{
    [TestMethod]
    public async Task RegisterPluginMethods_ConvertsMethodToKernelFunctionWithCorrectMetadata()
    {
        // Arrange
        var kernel = new Kernel();
        var contextFactoryMock = new Mock<IPluginContextFactory>();
        var sp = new ServiceCollection().BuildServiceProvider();
        var executionContext = new PluginExecutionContext(
            workspacePath: "/tmp/workspace",
            sessionId: Guid.NewGuid(),
            serviceProvider: sp,
            logger: NullLogger.Instance);

        contextFactoryMock.Setup(f => f.CreateExecutionContext(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<IServiceProvider>()))
            .Returns(executionContext);

        var method = new MockPluginMethod();
        var methods = new List<(string PluginId, IPluginMethod Method)>
        {
            ("TaxPlugin", method)
        };

        var sessionId = Guid.NewGuid();
        var workspacePath = "/tmp/workspace";

        // Act
        SemanticKernelPluginAdapter.RegisterPluginMethods(
            kernel,
            methods,
            contextFactoryMock.Object,
            workspacePath,
            sessionId);

        // Assert
        Assert.IsTrue(kernel.Plugins.Contains("TaxPlugin"));
        var plugin = kernel.Plugins["TaxPlugin"];
        var function = plugin["CalculateTax"];
        Assert.IsNotNull(function);
        Assert.AreEqual("CalculateTax", function.Name);
        Assert.AreEqual("Calculates tax on amount", function.Description);

        // Verify parameters
        var parameters = function.Metadata.Parameters;
        Assert.AreEqual(2, parameters.Count);

        var amountParam = parameters.First(p => p.Name == "amount");
        Assert.AreEqual("Amount to tax", amountParam.Description);
        Assert.AreEqual(typeof(double), amountParam.ParameterType);
        Assert.IsTrue(amountParam.IsRequired);

        var rateParam = parameters.First(p => p.Name == "rate");
        Assert.AreEqual("Tax rate percentage", rateParam.Description);
        Assert.AreEqual(0.20, rateParam.DefaultValue);
        Assert.IsFalse(rateParam.IsRequired);

        // Act: Invoke KernelFunction
        var result = await function.InvokeAsync(kernel, new KernelArguments { ["amount"] = 100.0, ["rate"] = 0.15 });

        // Assert
        Assert.AreEqual("Tax: 15.00", result.GetValue<string>());
    }

    [TestMethod]
    public void RegisterPluginMethods_AppliesSafetyOverrides()
    {
        // Arrange
        var kernel = new Kernel();
        var contextFactoryMock = new Mock<IPluginContextFactory>();
        var method = new MockPluginMethod(); // default IsDangerous = false
        var methods = new List<(string PluginId, IPluginMethod Method)>
        {
            ("TaxPlugin", method)
        };

        var safetyOverrides = new Dictionary<string, bool?>
        {
            ["TaxPlugin:CalculateTax"] = true
        };

        // Act
        SemanticKernelPluginAdapter.RegisterPluginMethods(
            kernel,
            methods,
            contextFactoryMock.Object,
            "/tmp/ws",
            Guid.NewGuid(),
            safetyOverrides: safetyOverrides);

        // Assert
        var function = kernel.Plugins["TaxPlugin"]["CalculateTax"];
        Assert.IsNotNull(function);
        Assert.IsTrue(function.Metadata.AdditionalProperties.TryGetValue("IsDangerous", out var isDangerous));
        Assert.AreEqual(true, isDangerous);
    }

    [TestMethod]
    public async Task RegisterPluginMethods_WhenInvokedWithKernelServices_PassesKernelServicesToExecutionContext()
    {
        // Arrange
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<ITestScopedService, KernelScopedService>();
        var kernel = builder.Build();

        IServiceProvider? capturedKernelServices = null;
        var contextFactoryMock = new Mock<IPluginContextFactory>();
        contextFactoryMock.Setup(f => f.CreateExecutionContext(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<IServiceProvider>()))
            .Callback<string, Guid, string, IReadOnlyDictionary<string, string>?, IReadOnlyDictionary<string, string>?, IServiceProvider?>(
                (ws, id, pId, db, app, ks) => capturedKernelServices = ks)
            .Returns(new PluginExecutionContext("/ws", Guid.NewGuid(), new ServiceCollection().BuildServiceProvider(), NullLogger.Instance));

        var method = new MockPluginMethod();
        var methods = new List<(string PluginId, IPluginMethod Method)> { ("TaxPlugin", method) };

        SemanticKernelPluginAdapter.RegisterPluginMethods(
            kernel,
            methods,
            contextFactoryMock.Object,
            "/ws",
            Guid.NewGuid());

        // Act - Invoke function
        var function = kernel.Plugins["TaxPlugin"]["CalculateTax"];
        await function.InvokeAsync(kernel, new KernelArguments { ["amount"] = 50.0 });

        // Assert - kernel.Services was passed down to CreateExecutionContext
        Assert.IsNotNull(capturedKernelServices);
        var resolvedService = capturedKernelServices.GetService(typeof(ITestScopedService)) as ITestScopedService;
        Assert.IsNotNull(resolvedService);
        Assert.AreEqual("KernelService", resolvedService.GetName());
    }
}
