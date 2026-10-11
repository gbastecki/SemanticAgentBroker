namespace SemanticAgentBroker.Shared.Configuration;

public class AiProviderOptions
{
    public int DefaultTimeoutMinutes { get; set; } = 60;
    public int MaxTokensPerRequest { get; set; } = 8192;
    public int MaxAssistantOutputLength { get; set; } = 24000;
    public int MaxToolObservationLength { get; set; } = 24000;

    /// <summary>
    /// When a tool result exceeds this char count, auto-save the full output to the workspace
    /// and replace the observation with a truncated preview + file path hint.
    /// This prevents large datasets from blowing up the LLM context window.
    /// Set to 0 to disable auto-save.
    /// </summary>
    public int AutoSaveToolOutputThreshold { get; set; } = 3000;

    /// <summary>
    /// Number of preview characters shown inline after a tool output is auto-saved to workspace.
    /// </summary>
    public int ToolOutputPreviewLength { get; set; } = 500;
    public Dictionary<TaskModelRole, ModelRouteConfig> ModelRouting { get; set; } = new();
    public List<ProviderConnection> Connections { get; set; } = new();
    public ContextCompactionOptions ContextCompaction { get; set; } = new();
    public InferenceOptions Inference { get; set; } = new();
    public PythonSandboxOptions PythonSandbox { get; set; } = new();
    public HealthCheckOptions HealthCheck { get; set; } = new();
}

public enum TaskModelRole
{
    PrimaryTextModel,
    SummarizationModel,
    VisionModel
}

public class ModelRouteConfig
{
    /// <summary>
    /// Optional sticky override. If set, the load balancer is bypassed and this specific
    /// provider connection is always used. If empty/null, the load balancer picks the best connection.
    /// </summary>
    public string? ProviderId { get; set; }
    public string ModelId { get; set; } = string.Empty;
}

public class ProviderConnection
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display name for this endpoint (used in logs and UI).</summary>
    public string EndpointName { get; set; } = string.Empty;

    public string ProviderType { get; set; } = string.Empty; // Ollama, OpenAiCompatible

    public string Endpoint { get; set; } = string.Empty;
    public string HttpBasicAuthUsername { get; set; } = string.Empty;
    public string HttpBasicAuthPassword { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool TrustUntrustedCertificates { get; set; }

    /// <summary>When false, this connection is excluded from load balancing and will not receive requests.</summary>
    public bool IsEnabled { get; set; } = true;

    public List<ModelConfig> Models { get; set; } = new();
}

public class ModelConfig
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "Text"; // Text, Vision
    /// <summary>Maximum context window size in tokens for this model. Used by compaction service.</summary>
    public int MaxContextTokens { get; set; } = 16384;
    public TokenCountingOptions TokenCounting { get; set; } = new();

    /// <summary>
    /// Model-specific thought/channel token prefixes to strip from reasoning output.
    /// E.g. ["<channel|>Thought", "<channel|>", "</channel>", "<|thought|>"].
    /// </summary>
    public List<string> ThoughtPrefixesToStrip { get; set; } = new();
}

public class AgentSystemOptions
{
    public int MaxConcurrentAgents { get; set; } = 3;
    public string DatabaseProvider { get; set; } = "Sqlite"; // Sqlite, Postgres, SqlServer
    public string DatabaseConnectionString { get; set; } = "Data Source=broker.db";
    public string AuthDatabaseConnectionString { get; set; } = "Data Source=auth.db";
    public string TokensDatabaseConnectionString { get; set; } = "Data Source=tokens.db";
    public string DefaultAdminUsername { get; set; } = "admin";
    public string DefaultAdminPassword { get; set; } = "admin";
    public int SessionDays { get; set; } = 7;
    public int MaxFailedAttempts { get; set; } = 10;
    public int LockoutMinutes { get; set; } = 5;
    
    public string TraceLogsDirectory { get; set; } = "TraceLogs";

    /// <summary>Maximum characters returned by FileIOPlugin.ReadFromWorkspace before truncation. Default 8000.</summary>
    public int MaxFileReadChars { get; set; } = 8000;

    /// <summary>
    /// Directory to scan for plugin DLLs. Relative paths are resolved from the application directory.
    /// Default is "Plugins" in the application root.
    /// </summary>
    public string PluginsDirectory { get; set; } = "Plugins";
    
    public string TaskWorkspacesPath { get; set; } = "TaskWorkspaces";

    /// <summary>Number of archived implementation plans to keep per task. Null means keep all.</summary>
    public int? ArchivedPlansToKeep { get; set; } = null;
    public bool CleanupWorkspacesOnTaskDelete { get; set; } = true;

    /// <summary>Maximum upload file size in megabytes. Used by both workspace upload and task creation upload. Default 10 MB.</summary>
    public int MaxUploadSizeMb { get; set; } = 10;

    /// <summary>Maximum file size in megabytes allowed for workspace file downloads. Default 50 MB.</summary>
    public int MaxDownloadSizeMb { get; set; } = 50;

    /// <summary>Maximum file size in megabytes allowed for text file edits in workspace. Default 50 MB.</summary>
    public int MaxFileEditSizeMb { get; set; } = 50;

    /// <summary>Maximum file size in megabytes allowed for @/app/workdir/ file auto-loading into tool parameters. Default 50 MB.</summary>
    public int MaxAutoLoadFileSizeMb { get; set; } = 50;

    /// <summary>Maximum execution timeout in minutes for agent tasks. Default 360 (6 hours).</summary>
    public int GlobalTaskTimeoutMinutes { get; set; } = 360;

    /// <summary>Optional timeout in seconds (primarily for testing or short-lived agent tasks). Overrides GlobalTaskTimeoutMinutes if set.</summary>
    public int? GlobalTaskTimeoutSeconds { get; set; }

    public CertificateOptions? Certificate { get; set; }
    public RestApiOptions RestApi { get; set; } = new();
    
    public bool IsTasksCleanupServiceEnabled { get; set; } = true;
    public int TasksCleanupDays { get; set; } = 14;

    /// <summary>
    /// CORS configuration. If null or AllowAll is true, all origins are allowed.
    /// Otherwise, only the origins in AllowedOrigins are permitted.
    /// </summary>
    public CorsOptions? Cors { get; set; }

    /// <summary>
    /// Security configuration for the HttpClient agent plugin (SSRF protection).
    /// </summary>
    public HttpClientSecurityOptions HttpClientSecurity { get; set; } = new();
}

/// <summary>
/// Security configuration for the HttpClient agent plugin (SSRF protection).
/// </summary>
public class HttpClientSecurityOptions
{
    /// <summary>When true, URLs matching BlockedPatterns are rejected.</summary>
    public bool EnableBlocklist { get; set; } = true;

    /// <summary>When true, only URLs matching AllowedPatterns are permitted.</summary>
    public bool EnableAllowlist { get; set; } = false;

    /// <summary>Blocked URL patterns (checked first). Default blocks private/loopback IPs.</summary>
    public List<string> BlockedPatterns { get; set; } = new()
    {
        "127.*", "10.*", "172.16.*", "172.17.*", "172.18.*", "172.19.*",
        "172.20.*", "172.21.*", "172.22.*", "172.23.*", "172.24.*",
        "172.25.*", "172.26.*", "172.27.*", "172.28.*", "172.29.*",
        "172.30.*", "172.31.*", "192.168.*", "0.*", "169.254.*",
        "::1", "fc00::*", "fe80::*", "localhost"
    };

    /// <summary>Allowed URL patterns (only checked if EnableAllowlist is true).</summary>
    public List<string> AllowedPatterns { get; set; } = new();
}

public class CorsOptions
{
    /// <summary>When true, all origins are allowed (equivalent to SetIsOriginAllowed(_ => true)).</summary>
    public bool AllowAll { get; set; } = true;
    
    /// <summary>Explicit list of allowed origins. Only used when AllowAll is false.</summary>
    public List<string> AllowedOrigins { get; set; } = new();
}

public class RestApiOptions
{
    public bool IsEnabled { get; set; } = true;
    public string Host { get; set; } = "0.0.0.0";
    public int? HttpPort { get; set; } = null;
    public int? HttpsPort { get; set; } = 5100;
    public int Port { get; set; } = 5100;
    public bool RedirectHttpToHttps { get; set; } = false;

    /// <summary>
    /// When true, ForwardedHeaders trusts all upstream proxies.
    /// WARNING: Should only be enabled in secure, isolated internal networks or during local debugging.
    /// </summary>
    public bool TrustAllProxies { get; set; } = false;

    /// <summary>
    /// Explicit list of trusted proxy IP addresses (e.g. ["127.0.0.1", "10.0.0.1"]).
    /// If empty and TrustAllProxies is false, defaults to loopback IPs (127.0.0.1, ::1).
    /// </summary>
    public List<string> KnownProxies { get; set; } = new();

    /// <summary>
    /// Explicit list of trusted IP networks in CIDR format (e.g. ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]).
    /// </summary>
    public List<string> KnownNetworks { get; set; } = new();
}

public class CertificateOptions
{
    public string? StoreLocation { get; set; }
    public string? StoreName { get; set; }
    public string? Thumbprint { get; set; }
    public string? Path { get; set; }
    public string? Password { get; set; }
}
/// <summary>
/// Controls the sliding-window context compaction behavior.
/// When the estimated token count exceeds the threshold ratio of MaxContextTokens,
/// older messages are summarized into a compact recap.
/// </summary>
public class ContextCompactionOptions
{
    /// <summary>Trigger compaction when usage exceeds this ratio (0.0 - 1.0) of MaxContextTokens.</summary>
    public double CompactionThreshold { get; set; } = 0.80;

    /// <summary>Number of most-recent messages to always keep in full (not summarized).</summary>
    public int KeepRecentMessages { get; set; } = 6;

    /// <summary>Number of recent tool observations to keep in full fidelity in the state injection window.</summary>
    public int RecentObservationWindowSize { get; set; } = 3;

    /// <summary>Maximum tokens to allocate for the summary of older messages.</summary>
    public int MaxSummaryTokens { get; set; } = 1200;

    /// <summary>
    /// Returns an effective compaction threshold scaled to the model's context window size.
    /// Smaller contexts compact earlier to prevent overflow.
    /// </summary>
    public double GetEffectiveThreshold(int maxContextTokens) => maxContextTokens switch
    {
        >= 262144 => Math.Max(CompactionThreshold, 0.90), // 256K+: very relaxed
        >= 65536  => Math.Max(CompactionThreshold, 0.85), // 64K+
        >= 32768  => Math.Min(CompactionThreshold, 0.65), // 32K: earlier compaction for better quality
        >= 16384  => Math.Min(CompactionThreshold, 0.55), // 16K: aggressive
        >= 8192   => Math.Min(CompactionThreshold, 0.45), // 8K: very aggressive
        _         => Math.Min(CompactionThreshold, 0.40)
    };

    /// <summary>
    /// Returns the number of recent messages to keep after compaction, scaled by context size.
    /// </summary>
    public int GetEffectiveKeepCount(int maxContextTokens) => maxContextTokens switch
    {
        >= 65536  => Math.Max(KeepRecentMessages, 10),
        >= 32768  => Math.Max(KeepRecentMessages, 8),
        >= 16384  => Math.Min(KeepRecentMessages, 4),
        >= 8192   => Math.Min(KeepRecentMessages, 2),
        _         => Math.Min(KeepRecentMessages, 2)
    };

    /// <summary>
    /// Returns the hard ceiling for messages before force-compaction triggers, scaled by context size.
    /// </summary>
    public int GetMaxMessagesBeforeForceCompaction(int maxContextTokens) => maxContextTokens switch
    {
        >= 262144 => 100,
        >= 65536  => 60,
        >= 32768  => 40,
        >= 16384  => 20,
        >= 8192   => 12,
        _         => 8
    };

    /// <summary>When true, the engine tracks completed steps and injects structured progress into every LLM call.</summary>
    public bool EnableStructuredStateTracking { get; set; } = true;

    /// <summary>After this many steps, reduce the observation nudge suffix to minimal 'Thought:' prompt.</summary>
    public int NudgeReductionAfterStep { get; set; } = 5;

    /// <summary>Maximum characters for the step log in context injection. Older steps are collapsed.</summary>
    public int MaxStepLogChars { get; set; } = 1500;

    /// <summary>
    /// Maximum tokens allowed for the state injection block sent to each subagent.
    /// This is the total token budget for the [TASK STATE] section including objective,
    /// completed steps, files manifest, key facts, plan, and raw last observation.
    /// For a 32K context model with 8K output, 16000 is a safe default (leaves ~8K for system prompt + safety).
    /// Set to 0 to disable token-budget enforcement (injection will be unlimited).
    /// </summary>
    public int MaxStateInjectionTokens { get; set; } = 16000;

    /// <summary>
    /// Maximum characters to store as the full raw observation from the most recent step.
    /// This untruncated observation is injected into the state so the subagent can reason
    /// about exact data (error messages, partial results) without information loss.
    /// Set to 0 to disable raw observation injection.
    /// </summary>
    public int RecentStepFullObservationChars { get; set; } = 4000;
}

/// <summary>
/// Inference-level settings applied to LLM prompt execution.
/// Extracted from previously hardcoded values throughout the engine.
/// </summary>
public class InferenceOptions
{
    /// <summary>Maximum number of tools the agent can execute concurrently in a single step.</summary>
    public int MaxParallelToolCalls { get; set; } = 3;

    /// <summary>
    /// Model-specific stop markers. When any of these appear in the streaming output,
    /// the engine terminates the stream to prevent infinite hallucination loops.
    /// </summary>
    public List<string> StopMarkers { get; set; } = ["<channel|>", "<tool_call|>", "<end_of_turn>", "<eos>"];

    /// <summary>Maximum number of retry attempts for transient LLM failures (timeout, connection refused, HTTP 5xx/429). Default -1 (infinite). Set to -1 for infinite retries.</summary>
    public int MaxLlmRetries { get; set; } = -1;

    /// <summary>Base delay in seconds between LLM retries when an endpoint is unreachable or failing. Default 5.</summary>
    public int LlmRetryDelaySeconds { get; set; } = 5;

    /// <summary>Maximum cap in seconds for exponential backoff delay between LLM retries. Default 30.</summary>
    public int MaxLlmRetryDelaySeconds { get; set; } = 30;

    /// <summary>Maximum format correction retries before aborting. Default 3.</summary>
    public int MaxFormatRetries { get; set; } = 3;

    /// <summary>FinalAnswer char limit before auto-spilling to workspace file. Default 4000.</summary>
    public int MaxFinalAnswerInlineChars { get; set; } = 4000;

    /// <summary>Freeform extension data for main ReAct execution PromptExecutionSettings.</summary>
    public Dictionary<string, object>? MainExecutionPromptSettings { get; set; }

    /// <summary>Freeform extension data for nudged retry execution when reasoning is truncated.</summary>
    public Dictionary<string, object>? NudgedExecutionPromptSettings { get; set; }

    /// <summary>Freeform extension data for planning phase PromptExecutionSettings.</summary>
    public Dictionary<string, object>? PlanningPromptSettings { get; set; }

    /// <summary>Freeform extension data for context summarization PromptExecutionSettings.</summary>
    public Dictionary<string, object>? SummarizationPromptSettings { get; set; }

    /// <summary>String replacements applied to LLM output (e.g. for fixing malformed markdown/artifacts).</summary>
    public Dictionary<string, string> ArtifactReplacements { get; set; } = new();

    /// <summary>
    /// Global thought/channel token prefixes to strip from reasoning output across all models.
    /// E.g. ["<channel|>Thought", "<channel|>", "</channel>", "<|thought|>", "<thought>", "</thought>", "[THOUGHT]"].
    /// </summary>
    public List<string> ThoughtPrefixesToStrip { get; set; } = ["<channel|>Thought", "<channel|>", "</channel>", "<|thought|>", "<thought>", "</thought>", "[THOUGHT]"];
}

/// <summary>
/// Configuration for the Python sandbox Docker container.
/// </summary>
public class PythonSandboxOptions
{
    /// <summary>The absolute path or name in the container mapped to the host.</summary>
    public string ContainerDataPath { get; set; } = "/app/workdir";

    /// <summary>The base directory on the host to create session workspaces in.</summary>
    public string HostDataPath { get; set; } = "TaskWorkspaces";

    /// <summary>Docker image name for the Python sandbox.</summary>
    public string ImageName { get; set; } = "ai-sandbox-python:latest";

    /// <summary>Maximum execution time in seconds before the container is killed.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Memory limit for the container in megabytes.</summary>
    public int MemoryLimitMb { get; set; } = 512;

    /// <summary>Maximum container output in megabytes before truncation and stream termination. Default 50 MB.</summary>
    public int MaxOutputMb { get; set; } = 50;

    /// <summary>
    /// Optional Docker daemon URI override. If empty, auto-detects based on OS
    /// (npipe://./pipe/docker_engine on Windows, unix:///var/run/docker.sock on Linux).
    /// Also respects the DOCKER_HOST environment variable.
    /// </summary>
    public string DockerUri { get; set; } = string.Empty;

    /// <summary>CPU quota in microseconds per CpuPeriod. Null = no limit. E.g., 100000 = 1 core.</summary>
    public long? CpuQuota { get; set; } = null;

    /// <summary>CPU CFS period in microseconds. Default 100000 (100ms). Only used when CpuQuota is set.</summary>
    public long CpuPeriod { get; set; } = 100_000;
}

/// <summary>
/// Configuration for endpoint health checking.
/// </summary>
public class HealthCheckOptions
{
    /// <summary>Whether endpoint health checking is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Interval in seconds between health check probes.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Timeout in seconds for each health check request.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Health check endpoint paths per provider type. 
    /// Key = ProviderType (case-insensitive), Value = relative path to ping.
    /// </summary>
    public Dictionary<string, string> EndpointPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenAiCompatible"] = "/v1/models"
    };
}

/// <summary>
/// Configuration for the ML.Tokenizers-based token counting.
/// </summary>
public class TokenCountingOptions
{
    /// <summary>Tokenizer model ID for ML.Tokenizers (e.g. "gpt-4o", "gpt-4"). Default: "gpt-4o".</summary>
    public string TokenizerModel { get; set; } = "gpt-4o";

    /// <summary>Fallback character divisor when tokenizer is unavailable. Default: 2.5.</summary>
    public double FallbackDivisor { get; set; } = 2.5;

    /// <summary>Token overhead per message for role markers and delimiters. Default: 20.</summary>
    public int PerMessageOverhead { get; set; } = 20;

    /// <summary>Path to tokenizer.json for custom tokenizers (e.g. gemma4).</summary>
    public string? TokenizerJsonPath { get; set; }
}
