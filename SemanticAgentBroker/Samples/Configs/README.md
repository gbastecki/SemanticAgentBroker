# Semantic Agent Broker - Configuration Samples & Guides

This directory contains production-ready configuration examples, reverse proxy templates, and tokenizer resources for deploying **Semantic Agent Broker** with local and frontier cloud LLM providers:

- **Local Inference Engines**: [Ollama](appsettings.ollama.json), [vLLM](appsettings.vllm.json), and [vLLM with Nginx Basic Auth](appsettings.vllm-basic-auth.json)
- **Frontier Cloud Providers**: [OpenAI](appsettings.openai.json) (`gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`), [Anthropic Claude](appsettings.claude.json) (`claude-sonnet-5`, `claude-opus-5`, `claude-fable-5`), [Google Gemini](appsettings.gemini.json) (`gemini-3.6-flash`, `gemini-3.5-flash-lite`)
- **Unified Multi-Provider**: [Master Load Balanced Config](appsettings.all-providers-unified.json)
- **Proxy Architecture**: [Nginx Basic Auth Reverse Proxy](Proxy/README.md)
- **Tokenizers & ML.Tokenizers**: [Tokenizer Guide & Downloader](Tokenizers/README.md)

---

## 1. Quick Navigation Matrix

| Configuration File | Provider | Target Models | Auth Mode | Tokenizer Type |
| :--- | :--- | :--- | :--- | :--- |
| [`appsettings.ollama.json`](appsettings.ollama.json) | **Ollama** | Google Gemma 4 (`gemma4:26b-a4b-it`), Alibaba Qwen 3.8 (`qwen3.8:27b`) | None / Local | Custom BPE `tokenizer.json` / Tiktoken |
| [`appsettings.vllm.json`](appsettings.vllm.json) | **vLLM (Direct)** | `google/gemma-4-26B-A4B-it`, `Qwen/Qwen3.8-27B` | Bearer API Key | Custom BPE `tokenizer.json` / Tiktoken |
| [`appsettings.vllm-basic-auth.json`](appsettings.vllm-basic-auth.json) | **vLLM (Nginx Proxy)** | `google/gemma-4-26B-A4B-it`, `Qwen/Qwen3.8-27B` | **HTTP Basic Auth** (`user:pass`) | Custom BPE `tokenizer.json` / Tiktoken |
| [`appsettings.openai.json`](appsettings.openai.json) | **OpenAI** | `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna` | Bearer `sk-...` | **Built-in Tiktoken** (`o200k_base`) |
| [`appsettings.claude.json`](appsettings.claude.json) | **Anthropic Claude** | `claude-sonnet-5`, `claude-opus-5`, `claude-fable-5` | Bearer Token / Gateway | Tiktoken (`gpt-4o`) + Divisor `2.7` |
| [`appsettings.gemini.json`](appsettings.gemini.json) | **Google Gemini** | `gemini-3.6-flash`, `gemini-3.5-flash-lite` | Google AI Studio Key | Tiktoken (`gpt-4o`) + Divisor `3.0` |
| [`appsettings.all-providers-unified.json`](appsettings.all-providers-unified.json) | **Multi-Provider Unified** | `gemini-3.6-flash`, `claude-sonnet-5`, `gpt-5.6-sol`, `gemma4:26b-a4b-it`, `qwen3.8:27b` | Hybrid / Mixed | Dynamic Per-Model Resolution |

---

## 2. Provider Architectural Deep Dive

### A. Ollama (Local)
- **Endpoint**: `http://localhost:11434/v1`
- **ProviderType**: `"Ollama"`
- **Special Capabilities**:
  - **Automatic Context Window (`num_ctx`) Synchronization**: On startup, `EndpointHealthCheckService` probes Ollama via `POST /api/show`. If `num_ctx` is smaller than `MaxContextTokens` (e.g. 32768), the broker dynamically updates Ollama's registry via `POST /api/create` and reloads the runner via `POST /api/generate` with zero manual CLI intervention.
  - **Payload Interception**: `OllamaCompatibilityDelegatingHandler` automatically injects top-level `"num_ctx"` and nested `"options": { "num_ctx": 32768 }` into every HTTP POST payload.
  - **Finish Reason Normalization**: Intercepts non-standard Ollama finish reasons (`"tool_call"` / `"function_call"`) and converts them to standard `"tool_calls"` so the underlying .NET OpenAI client does not crash with `ArgumentOutOfRangeException`.

---

### B. vLLM (Direct & Basic Auth Reverse Proxy)
- **Endpoint**: `http://localhost:8000/v1` (Direct) or `http://localhost:8080/v1` (via Nginx Basic Auth Proxy)
- **ProviderType**: `"OpenAiCompatible"`
- **Special Capabilities**:
  - **High Concurrency & Continuous Batching**: Optimized for tensor-parallel multi-GPU serving.
  - **Securing vLLM with Basic Auth**: vLLM natively lacks multi-user credentials. The included [Nginx Reverse Proxy](Proxy/README.md) fronts vLLM with HTTP Basic Authentication (`.htpasswd`), disables buffering (`proxy_buffering off;`), and sets timeouts to `3600s` for streaming long ReAct execution cycles.
  - **C# DelegatingHandler**: When `HttpBasicAuthUsername` and `HttpBasicAuthPassword` are configured and `ApiKey` is empty, `BasicAuthDelegatingHandler.cs` automatically injects `Authorization: Basic <base64>` headers into all outbound requests.

---

### C. OpenAI (Cloud)
- **Endpoint**: `https://api.openai.com/v1`
- **ProviderType**: `"OpenAi"`
- **Special Capabilities**:
  - Direct integration with `gpt-5.6-sol`, `gpt-5.6-terra`, and `gpt-5.6-luna`.
  - **Zero-Configuration Tokenizer**: Microsoft.ML.Tokenizers bundles OpenAI's `o200k_base` and `cl100k_base` BPE tables directly inside the compiled C# assembly. **Zero external files or downloads are needed**-simply specify `"TokenizerModel": "gpt-4o"`.

---

### D. Anthropic Claude (Cloud / Proxy)
- **Endpoint**: `http://localhost:4000/v1` (LiteLLM / OpenRouter / Cloudflare AI Gateway)
- **ProviderType**: `"OpenAiCompatible"`
- **Models**:
  - `claude-sonnet-5` (Primary ReAct reasoning)
  - `claude-opus-5` (Deep multi-step planning)
  - `claude-fable-5` (Creative & specialized workflows)
- **Token Counting**:
  Anthropic's proprietary BPE tokenizer is estimated with >97% accuracy using Tiktoken (`"TokenizerModel": "gpt-4o"`) paired with `"FallbackDivisor": 2.7`.

---

### E. Google Gemini (Cloud / Official OpenAI Endpoint)
- **Endpoint**: `https://generativelanguage.googleapis.com/v1beta/openai/`
- **ProviderType**: `"OpenAiCompatible"`
- **Models**:
  - `gemini-3.6-flash` (Sub-second latency ReAct execution, massive 1M+ context window)
  - `gemini-3.5-flash-lite` (High-speed context compaction summarization)
- **Token Counting**:
  Configured with `"TokenizerModel": "gpt-4o"` and `"FallbackDivisor": 3.0` to match Gemini's SentencePiece subword tokenization density.

---

## 3. Tokenizer Architecture & Model Guide

All token counting is handled by [`MlTokenCounter.cs`](../../SemanticAgentBroker.Core/Features/TokenCounting/Implementations/MlTokenCounter.cs).

### Where is each tokenizer?

```
┌────────────────────────┬─────────────────────────────┬────────────────────────────────────────────────────────┐
│ Model Family           │ Where is Tokenizer Located? │ How to Configure in Semantic Agent Broker              │
├────────────────────────┼─────────────────────────────┼────────────────────────────────────────────────────────┤
│ OpenAI                 │ Embedded inside .NET DLL    │ Set "TokenizerModel": "gpt-4o" (No file needed)        │
│ Google Gemma 4         │ Hugging Face BPE JSON       │ Download via script -> Set "TokenizerJsonPath"         │
│ Alibaba Qwen 3.8       │ Hugging Face BPE JSON       │ Download via script -> Set "TokenizerJsonPath"         │
│ Anthropic Claude       │ Tiktoken Correlation        │ Set "TokenizerModel": "gpt-4o", "FallbackDivisor": 2.7 │
│ Google Gemini          │ Tiktoken Correlation        │ Set "TokenizerModel": "gpt-4o", "FallbackDivisor": 3.0 │
└────────────────────────┴─────────────────────────────┴────────────────────────────────────────────────────────┘
```

> [!TIP]
> **Built-in Tiktoken Zero-File Mode**:
> When using `"TokenizerModel": "gpt-4o"`, the `TokenizerJsonPath` property is optional and can be set to `null` or omitted entirely. `Microsoft.ML.Tokenizers` initializes the tokenizer directly in-memory from embedded assembly tables without requiring any external file.

### Downloading Custom Tokenizers (`download_tokenizers.py`)
Run the bundled Python script to download official `tokenizer.json` files for Google Gemma 4 and Alibaba Qwen 3.8:
```powershell
python Tokenizers\download_tokenizers.py --output-dir "./Tokenizers"
```

For complete tokenizer details, see the [Tokenizer Reference Guide](Tokenizers/README.md).

---

## 4. How to Apply a Configuration File

To activate any configuration:

1. Copy the desired JSON file content (e.g. `appsettings.vllm-basic-auth.json` or `appsettings.all-providers-unified.json`).
2. Paste into `SemanticAgentBroker.Api/appsettings.json`.
3. Replace any placeholder API keys (`sk-...`, `AIzaSy...`) with your actual secrets.
4. Start or restart the API:
   ```powershell
   cd SemanticAgentBroker.Api
   dotnet run
   ```

---

## 5. Model Routing & Load Balancing

Semantic Agent Broker separates agent roles in `ModelRouting`:

```json
"ModelRouting": {
  "PrimaryTextModel": {
    "ModelId": "gemini-3.6-flash"
  },
  "SummarizationModel": {
    "ModelId": "gemini-3.5-flash-lite"
  }
}
```

- **`PrimaryTextModel`**: Executes main ReAct turns (thought generation, multi-action tool invocation, final answers).
- **`SummarizationModel`**: Used by `ContextCompactionService` to distill conversation history into recaps when context exceeds `CompactionThreshold`.
- **Dynamic Load Balancing**: If `ProviderId` is omitted, `ConnectionLoadBalancer` automatically distributes requests across all active healthy connections that advertise that `ModelId`.
- **Sticky Routing**: Set `"ProviderId": "ConnectionId"` to force requests to a specific endpoint.

---

## 6. Thought Prefixes & Control Token Stripping

Reasoning models emit internal reasoning channel tokens. Configure `ThoughtPrefixesToStrip` per model or globally under `Inference` to ensure clean UI presentation:

```json
"ThoughtPrefixesToStrip": [
  "<channel|>Thought",
  "<channel|>",
  "</channel>",
  "<|thought|>",
  "<thought>",
  "</thought>",
  "[THOUGHT]"
]
```
