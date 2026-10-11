using Microsoft.SemanticKernel;
using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.PluginHost.Implementations;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Core.Builders;

/// <summary>
/// Builds a Semantic Kernel instance for an agent definition, registering 
/// only the plugin methods that the agent has been configured to use.
/// </summary>
public class AgentBuilder
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _agentName;
    private string _systemInstruction = string.Empty;
    private readonly List<(string PluginId, string MethodId)> _methodBindings = new();

    /// <summary>
    /// Initializes a new instance of <see cref="AgentBuilder"/>.
    /// </summary>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <param name="agentName">The agent name.</param>
    public AgentBuilder(IServiceProvider serviceProvider, string agentName)
    {
        _serviceProvider = serviceProvider;
        _agentName = agentName;
    }

    /// <summary>Sets the system instruction template for the agent.</summary>
    /// <param name="instruction">The system instruction text.</param>
    /// <returns>This builder instance.</returns>
    public AgentBuilder WithSystemInstruction(string instruction)
    {
        _systemInstruction = instruction;
        return this;
    }

    private IReadOnlyDictionary<string, bool?>? _safetyOverrides;

    /// <summary>Configures the plugin method bindings for this agent.</summary>
    /// <param name="bindings">The method bindings to include.</param>
    /// <returns>This builder instance.</returns>
    public AgentBuilder WithMethodBindings(IEnumerable<AgentMethodBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            _methodBindings.Add((binding.PluginId, binding.MethodId));
        }
        return this;
    }

    /// <summary>Configures raw (PluginId, MethodId) bindings for this agent.</summary>
    /// <param name="bindings">The raw tuples of plugin ID and method ID.</param>
    /// <returns>This builder instance.</returns>
    public AgentBuilder WithRawMethodBindings(IEnumerable<(string PluginId, string MethodId)> bindings)
    {
        _methodBindings.AddRange(bindings);
        return this;
    }

    /// <summary>Sets safety overrides for specific plugin methods.</summary>
    /// <param name="overrides">Dictionary mapping "PluginId:MethodId" to dangerous boolean flags.</param>
    /// <returns>This builder instance.</returns>
    public AgentBuilder WithSafetyOverrides(IReadOnlyDictionary<string, bool?> overrides)
    {
        _safetyOverrides = overrides;
        return this;
    }

    /// <summary>
    /// Registers the bound plugin methods on the kernel using the SK adapter.
    /// </summary>
    /// <param name="kernel">The Semantic Kernel instance.</param>
    /// <param name="pluginRegistry">The plugin registry to resolve methods from.</param>
    /// <param name="contextFactory">The plugin context factory.</param>
    /// <param name="workspacePath">Path to the task workspace.</param>
    /// <param name="sessionId">Task session ID.</param>
    /// <param name="pluginConfigs">Optional per-plugin configuration dictionaries.</param>
    public void RegisterPlugins(
        Kernel kernel,
        IPluginRegistry pluginRegistry,
        IPluginContextFactory contextFactory,
        string workspacePath,
        Guid sessionId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? pluginConfigs = null)
    {
        var methods = pluginRegistry.GetMethods(
            _methodBindings.Count > 0 ? _methodBindings : null);

        SemanticKernelPluginAdapter.RegisterPluginMethods(
            kernel, methods, contextFactory, workspacePath, sessionId, pluginConfigs, _safetyOverrides);
    }

    private string? _taskObjective;

    /// <summary>Sets an optional high-level objective for this task execution.</summary>
    /// <param name="objective">The task objective description.</param>
    /// <returns>This builder instance.</returns>
    public AgentBuilder WithTaskObjective(string? objective)
    {
        _taskObjective = objective;
        return this;
    }

    /// <summary>
    /// Builds the full system prompt by combining the user-defined instruction
    /// with auto-generated tool descriptions from the kernel's registered plugins.
    /// Uses <see cref="ReactFormatTokens"/> constants for format keywords.
    /// Includes token budget awareness and data handling best practices.
    /// </summary>
    /// <param name="kernel">The Semantic Kernel instance with registered plugins.</param>
    /// <param name="maxTokens">The maximum context tokens budget.</param>
    /// <returns>The combined system prompt string.</returns>
    public string BuildSystemPrompt(Kernel kernel, int maxTokens = Consts.DefaultMaxContextTokens)
    {
        var functions = kernel.Plugins.GetFunctionsMetadata();
        var toolsDesc = string.Join("\n", functions.Select(f =>
            $"- {f.PluginName}-{f.Name}: {f.Description}" +
            (f.Parameters.Any() ? "\n  Params: " + string.Join(", ", f.Parameters.Select(p => $"{p.Name} ({p.ParameterType?.Name ?? "string"})")) : "") +
            (!string.IsNullOrWhiteSpace(f.ReturnParameter?.Description) ? $"\n  Returns: {f.ReturnParameter.Description}" : "")));
            
        bool hasDataTools = functions.Any(f => f.PluginName == "PythonSandbox" || f.PluginName == "FileIO");
        bool isComplexTask = _taskObjective != null && _taskObjective.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 30;

        var prompt = _systemInstruction;
        
        if (!string.IsNullOrWhiteSpace(_taskObjective))
        {
            prompt += $"\n\n## Task Objective\n{_taskObjective}";
        }
        
        prompt += $$"""
        

        ## Available Tools
        You have {{functions.Count}} tools available to you. You MUST use these tools natively via function calling.
        DO NOT invent tool names.
        {{toolsDesc}}

        ## Response Format
        - ALWAYS begin your response with `{{ReactFormatTokens.Thought}}` explaining what you observe in the current state, what you plan to do next, and why. This is mandatory for every response.
        - After your Thought, proceed to invoke the appropriate tool natively via function calling.
        - You may call multiple tools concurrently in the same turn if they are independent.
        
        ## Generating the Final Answer or Implementation Plan
        - If you need to create/update an implementation plan, use the exact keyword `{{ReactFormatTokens.Plan}}` followed by the markdown block.
        - When the task is complete, use the exact keyword `{{ReactFormatTokens.FinalAnswer}}` followed by your final result.
        
        CRITICAL RULES:
        1. Put requested analysis/summaries DIRECTLY into the Plan block.
        2. Do NOT promise to create separate report files unless explicitly requested.
        3. Listing a file in your plan's "Deliverables" does NOT create it! You MUST use a tool (like FileIO) to actually create them.
        
        ## Tool Argument Rules
        - Ensure all JSON arguments are well-formed - escape special characters and quotes properly.
        - For Python scripts: the sandbox runs Python 3.12. Use single quotes for standalone string literals (e.g., print('hello'), pd.read_csv('/app/workdir/...')). In single-quoted f-strings f'...', use double quotes \"...\" for inner dictionary keys or strings (e.g., f'{row[\"val\"]}'), or assign variables before printing to avoid Python 3.12 syntax errors.

        Context budget: ~{{maxTokens}} tokens. Be efficient.
        """;

        if (hasDataTools)
        {
            prompt += """
            
            ## Data Rules
            WORKSPACE_PATH: /app/workdir/
            ALL files are read from and written to /app/workdir/. ALWAYS use this exact path. Never abbreviate or alter it.
            - Large data (>500 chars) is auto-saved to /app/workdir/. Use the file path from the observation.
            - In Python, read AND write files directly to /app/workdir/ (e.g. df.to_csv('/app/workdir/result.csv')). This is your shared workspace.
            - Do NOT save files to the python sandbox directory. Always use /app/workdir/.
            - Print only summaries/statistics to the console. Save long reports directly to /app/workdir/ files within your Python scripts. NEVER print full reports or massive data to standard output.
            - In FinalAnswer, reference saved files + key findings. Don't reproduce large tables inline.
            - CRITICAL: ALWAYS fetch data in BULK. Pass ALL required IDs as a single comma-separated string in ONE call. You must NEVER loop over IDs calling the same tool repeatedly. One call, all IDs.
            - EFFICIENCY: Complete the task in the fewest steps possible. Do not make redundant file reads/writes.
            
            ## Python Scripting Best Practices
            - Target runtime: Python 3.12. Standard data science libraries (pandas, numpy, scipy, matplotlib, seaborn, scikit-learn, etc.) are pre-installed in the isolated sandbox.
            - Inspect schemas first: On new or unfamiliar datasets, run a quick script to inspect columns and sample rows (print(df.columns); print(df.head(2))) before writing complex transformations.
            - Keep scripts simple: Prefer straightforward loops or standard df.groupby(...).agg(...) over fragile MultiIndex pivots or chained regex replacements.
            - Table formatting: Always use tabulate(df, headers='keys', tablefmt='pipe') or df.to_markdown(index=False) for Markdown tables. NEVER write manual line-by-line print() loops with Markdown pipes and string escapes.
            - Do not flood console output: NEVER print full DataFrames, entire datasets, or full generated Markdown reports to the console (e.g. DO NOT do print(df) or print(markdown_report)). Print only the first 5-10 rows (print(df.head(10))) or summary statistics (print(df.describe())). Always save the full output to a file in /app/workdir/.
            - No top-level try-except: Do NOT wrap scripts in top-level try-except blocks. In the sandbox, unhandled exceptions automatically output tracebacks with exact line numbers for diagnosis. Wrapping code in try-except frequently causes indentation/SyntaxError issues and masks failures.
            - Single script per turn: Emit at most ONE Action:/ActionInput: block for PythonSandbox-ExecutePythonScript per response. Finalize your logic in Thought before generating the tool call.
            - Verify variable names: Double-check DataFrame variable names (df vs merged_df) and column names before using them.
            - Plain text symbols: Do NOT use LaTeX math delimiters ($) in thoughts, plans, or code. Use plain text arrows (->) instead.
            """;
        }

        if (isComplexTask || hasDataTools)
        {
            prompt += """
            
            ## Task Continuity
            - Your progress is tracked automatically. [TASK PROGRESS] blocks show completed steps - never repeat them.
            - After context compaction, [TASK STATE RECOVERED] tells you exactly where you left off - just continue.
            - The Files section lists all workspace files. Use them directly - don't recreate them.            
            ## Token Efficiency
            - Keep Thoughts to 1-2 sentences. Don't restate the objective.
            - Don't summarize tool outputs in Thought - the system tracks them.
            - Batch ALL operations: one Python script for all analysis, one tool call for all IDs.
            - LARGE ARGUMENTS: If a tool needs a massive argument (e.g. 500 IDs), save it to a file. Pass the file path prefixed with '@' (e.g., {"sensorId": "@/app/workdir/ids.txt"}). The system will auto-read it.
            """;
        }

        // File discipline rules are only relevant when agent has file/data tools
        if (hasDataTools)
        {
            prompt += """
            
            ## File Discipline
            - NEVER create multiple files for the same data. Use FileIO-AppendToWorkspace for incremental collection.
            - Use deterministic filenames (e.g., readings.csv, report.md) not timestamped ones.
            - Save intermediate results to files, reference them in FinalAnswer.
            - Keep workspace clean: one file per purpose.
            """;
        }

        return prompt;
    }

    /// <summary>Gets the configured system instruction text.</summary>
    public string SystemInstruction => _systemInstruction;
}
