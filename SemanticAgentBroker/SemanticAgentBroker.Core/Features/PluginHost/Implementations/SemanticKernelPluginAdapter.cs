using Microsoft.SemanticKernel;
using SemanticAgentBroker.Sdk;
namespace SemanticAgentBroker.Core.Features.PluginHost.Implementations;

/// <summary>
/// Bridges the plugin SDK's <see cref="IPluginMethod"/> to Semantic Kernel's <see cref="KernelFunction"/>.
/// Converts plugin methods into SK-compatible functions with full metadata for the LLM.
/// </summary>
public static class SemanticKernelPluginAdapter
{
    /// <summary>
    /// Creates Semantic Kernel functions from a set of plugin methods and registers them
    /// on the given kernel. Each method is registered under a plugin group named after the plugin ID.
    /// </summary>
    /// <param name="kernel">Target kernel to register functions on.</param>
    /// <param name="methods">Plugin methods to convert, keyed by plugin ID.</param>
    /// <param name="contextFactory">Factory for creating execution contexts.</param>
    /// <param name="workspacePath">Workspace path for the current task.</param>
    /// <param name="sessionId">Current session/task ID.</param>
    /// <param name="pluginConfigs">Per-plugin DB configuration values.</param>
    /// <param name="safetyOverrides">Optional map of method safety overrides.</param>
    public static void RegisterPluginMethods(
        Kernel kernel,
        IReadOnlyList<(string PluginId, IPluginMethod Method)> methods,
        IPluginContextFactory contextFactory,
        string workspacePath,
        Guid sessionId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? pluginConfigs = null,
        IReadOnlyDictionary<string, bool?>? safetyOverrides = null)
    {
        // Group methods by plugin ID so they appear as separate SK plugins
        var grouped = methods.GroupBy(m => m.PluginId);

        foreach (var group in grouped)
        {
            var pluginId = group.Key;
            var functions = new List<KernelFunction>();

            foreach (var (_, method) in group)
            {
                var overrideKey = $"{pluginId}:{method.Id}";
                var safetyOverride = safetyOverrides?.TryGetValue(overrideKey, out var over) == true ? over : null;

                var fn = CreateKernelFunction(method, pluginId, contextFactory, workspacePath, sessionId,
                    pluginConfigs?.GetValueOrDefault(pluginId), safetyOverride);
                functions.Add(fn);
            }

            kernel.Plugins.AddFromFunctions(pluginId, functions);
        }
    }

    /// <summary>
    /// Creates a single <see cref="KernelFunction"/> from an <see cref="IPluginMethod"/>.
    /// </summary>
    private static KernelFunction CreateKernelFunction(
        IPluginMethod method,
        string pluginId,
        IPluginContextFactory contextFactory,
        string workspacePath,
        Guid sessionId,
        IReadOnlyDictionary<string, string>? dbConfig,
        bool? safetyOverride)
    {
        // Build parameter metadata for SK
        var parameters = method.Parameters.Select(p =>
            new KernelParameterMetadata(p.Name)
            {
                Description = p.Description,
                ParameterType = p.ParameterType,
                IsRequired = p.IsRequired,
                DefaultValue = p.DefaultValue
            }).ToList();

        var returnParam = new KernelReturnParameterMetadata
        {
            Description = method.ReturnDescription
        };

        var isDangerous = safetyOverride ?? method.IsDangerous;
        var additionalProperties = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>
            {
                ["IsDangerous"] = isDangerous
            });

        // Create the SK function that delegates to IPluginMethod
        var function = KernelFunctionFactory.CreateFromMethod(
            async (Kernel k, KernelArguments args, CancellationToken ct) =>
            {
                // Convert KernelArguments to a simple dictionary
                var argDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in args)
                {
                    argDict[kvp.Key] = kvp.Value;
                }

                // Create execution context for this invocation
                var context = contextFactory.CreateExecutionContext(
                    workspacePath, sessionId, pluginId, dbConfig, null, k.Services);

                return await method.ExecuteAsync(argDict, context, ct);
            },
            functionName: method.Id,
            description: method.Description,
            parameters: parameters,
            returnParameter: returnParam);

        try
        {
            var prop = typeof(KernelFunctionMetadata).GetProperty(nameof(KernelFunctionMetadata.AdditionalProperties));
            if (prop?.SetMethod != null)
            {
                prop.SetValue(function.Metadata, additionalProperties);
            }
            else
            {
                var field = typeof(KernelFunctionMetadata).GetField($"<{nameof(KernelFunctionMetadata.AdditionalProperties)}>k__BackingField",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                field?.SetValue(function.Metadata, additionalProperties);
            }
        }
        catch
        {
            // Ignore reflection issues
        }

        return function;
    }
}

