# Tokenizers & Token Counting in Semantic Agent Broker

This guide explains how token counting works in **Semantic Agent Broker**, where tokenizers for different models are located, and how to configure accurate token counting for **Google Gemma 4 (`google/gemma-4-26B-A4B-it`)**, **Alibaba Qwen 3.8 (`Qwen/Qwen3.8-27B`)**, **OpenAI (`gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`)**, **Anthropic Claude (`claude-sonnet-5`, `claude-opus-5`, `claude-fable-5`)**, and **Google Gemini (`gemini-3.6-flash`, `gemini-3.5-flash-lite`)**.

---

## 1. How Token Counting Works in Semantic Agent Broker

Token counting is managed by [`MlTokenCounter.cs`](../../../SemanticAgentBroker.Core/Features/TokenCounting/Implementations/MlTokenCounter.cs) using the official [`Microsoft.ML.Tokenizers`](https://www.nuget.org/packages/Microsoft.ML.Tokenizers) library.

The token counter resolves tokenizer engines using the following priority pipeline:

```
                  ┌────────────────────────────────────────────────┐
                  │ ModelConfig.TokenCounting in appsettings.json  │
                  └───────────────────────┬────────────────────────┘
                                          │
                        Is TokenizerJsonPath configured & file exists?
                                  ┌───────┴───────┐
                             YES  │               │  NO
                                  ▼               ▼
                   ┌────────────────────────┐   ┌───────────────────────────┐
                   │ Custom File Loader     │   │ Built-in Tiktoken Model   │
                   │ - .json: HF BPE Loader │   │ TiktokenTokenizer         │
                   │ - .model: SentencePiece│   │ .CreateForModel("gpt-4o") │
                   │ - other: Tiktoken file │   └─────────────┬─────────────┘
                   └──────────┬─────────────┘                 │
                              │                               │
                              └──────────────┬────────────────┘
                                             │
                                  Did Tokenizer load & run?
                                       ┌─────┴─────┐
                                  YES  │           │  NO (or file missing)
                                       ▼           ▼
                         ┌───────────────────┐   ┌─────────────────────────┐
                         │ Exact BPE Token   │   │ Heuristic Fallback      │
                         │ Count             │   │ (text.Length / Divisor) │
                         └───────────────────┘   └─────────────────────────┘
```

---

## 2. Model Breakdown & Where Tokenizers Live

> [!IMPORTANT]
> **Decision Rule for `TokenizerJsonPath`**:
> - **Use a Tokenizer File (`"TokenizerJsonPath": "Tokenizers/..."`)**: Whenever the model has its own official BPE/SentencePiece `tokenizer.json` file on disk (such as **Google Gemma 4** or **Alibaba Qwen 3.8**).
> - **Use Built-in Tiktoken (`"TokenizerJsonPath": null`)**: When using **OpenAI** models (where tokenizer tables are embedded in memory), when an official standalone tokenizer file is not required (**Claude**, **Gemini**), or when choosing to use Tiktoken as a zero-file lightweight approximation.

### Summary Table

| Model Family | Representative Models | Tokenizer Strategy | Need External File? | Recommended Config |
| :--- | :--- | :--- | :--- | :--- |
| **OpenAI** | `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna` | Built-in Tiktoken (`o200k_base`) | ❌ **NO** (Embedded in C# DLL) | `"TokenizerModel": "gpt-4o"` |
| **Google Gemma 4** | `google/gemma-4-26B-A4B-it` (`gemma4:26b-a4b-it`) | Hugging Face BPE `tokenizer.json` | ✅ **YES** (or fallback divisor 2.5) | `"TokenizerJsonPath": "Tokenizers/gemma4_tokenizer.json"` |
| **Alibaba Qwen 3.8** | `Qwen/Qwen3.8-27B` (`qwen3.8:27b`) | Hugging Face BPE `tokenizer.json` | ✅ **YES** (or fallback `gpt-4o` + divisor 2.3) | `"TokenizerJsonPath": "Tokenizers/qwen_tokenizer.json"` |
| **Anthropic Claude** | `claude-sonnet-5`, `claude-opus-5`, `claude-fable-5` | Tiktoken `o200k_base` / Fallback Divisor | ❌ **NO** (Tiktoken correlation >97%) | `"TokenizerModel": "gpt-4o"`, `"FallbackDivisor": 2.7` |
| **Google Gemini** | `gemini-3.6-flash`, `gemini-3.5-flash-lite` | Tiktoken / Fallback Divisor | ❌ **NO** (or Gemma `tokenizer.json`) | `"TokenizerModel": "gpt-4o"`, `"FallbackDivisor": 3.0` |

---

### A. OpenAI Models (`gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`)

#### Where is the tokenizer?
**It is built directly into the `Microsoft.ML.Tokenizers` assembly.**
`Microsoft.ML.Tokenizers` contains embedded BPE data tables for OpenAI's `o200k_base` and `cl100k_base`.

**No external file is needed.**

#### Configuration:
```json
"TokenCounting": {
  "TokenizerModel": "gpt-4o",
  "TokenizerJsonPath": null,
  "FallbackDivisor": 2.5,
  "PerMessageOverhead": 20
}
```

---

### B. Google Gemma 4 (`google/gemma-4-26B-A4B-it`, `gemma4:26b-a4b-it`)

#### Where is the tokenizer?
Official Google Gemma 4 256k-vocabulary BPE tokenizer:
- **Repository**: [`https://huggingface.co/google/gemma-4-26B-A4B-it`](https://huggingface.co/google/gemma-4-26B-A4B-it)
- **Tokenizer**: [`https://huggingface.co/google/gemma-4-26B-A4B-it/blob/main/tokenizer.json`](https://huggingface.co/google/gemma-4-26B-A4B-it/blob/main/tokenizer.json)

Download via the included Python script:
```bash
python download_tokenizers.py --repo "google/gemma-4-26B-A4B-it" --name "gemma4_tokenizer.json" --output-dir "./Tokenizers"
```

#### Configuration:
```json
"TokenCounting": {
  "TokenizerModel": "gpt-4o",
  "TokenizerJsonPath": "%CURRENT_USER%\\SemanticAgentBrokerData\\Tokenizers\\gemma4_tokenizer.json",
  "FallbackDivisor": 2.5,
  "PerMessageOverhead": 20
}
```

---

### C. Alibaba Qwen 3.8 (`Qwen/Qwen3.8-27B`, `qwen3.8:27b`)

#### Where is the tokenizer?
Official Alibaba Qwen 3.8 248k-vocabulary BPE tokenizer:
- **Repository**: [`https://huggingface.co/Qwen/Qwen3.8-27B`](https://huggingface.co/Qwen/Qwen3.8-27B)
- **Tokenizer**: [`https://huggingface.co/Qwen/Qwen3.8-27B/blob/main/tokenizer.json`](https://huggingface.co/Qwen/Qwen3.8-27B/blob/main/tokenizer.json)

Download via the included Python script:
```bash
python download_tokenizers.py --repo "Qwen/Qwen3.8-27B" --name "qwen_tokenizer.json" --output-dir "./Tokenizers"
```

#### Configuration:
```json
"TokenCounting": {
  "TokenizerModel": "gpt-4o",
  "TokenizerJsonPath": "%CURRENT_USER%\\SemanticAgentBrokerData\\Tokenizers\\qwen_tokenizer.json",
  "FallbackDivisor": 2.3,
  "PerMessageOverhead": 20
}
```

---

### D. Anthropic Claude (`claude-sonnet-5`, `claude-opus-5`, `claude-fable-5`)

#### How tokenization works with Claude:
Anthropic uses a proprietary BPE tokenizer. Semantic Agent Broker uses `Microsoft.ML.Tokenizers`'s Tiktoken engine with a calibrated `FallbackDivisor: 2.7`.

#### Configuration:
```json
"TokenCounting": {
  "TokenizerModel": "gpt-4o",
  "FallbackDivisor": 2.7,
  "PerMessageOverhead": 20
}
```

---

### E. Google Gemini (`gemini-3.6-flash`, `gemini-3.5-flash-lite`)

#### How tokenization works with Gemini:
Configured with `gpt-4o` Tiktoken and a calibrated `FallbackDivisor: 3.0`.

#### Configuration:
```json
"TokenCounting": {
  "TokenizerModel": "gpt-4o",
  "FallbackDivisor": 3.0,
  "PerMessageOverhead": 20
}
```

---

## 3. Automated Tokenizer Downloader Utility

[`download_tokenizers.py`](./download_tokenizers.py) is provided in this directory.

### Quick Usage:

```bash
# Download all standard tokenizers into the local Tokenizers folder:
python download_tokenizers.py --output-dir "./Tokenizers"
```

### Output:
```
======================================================================
Downloading Default Tokenizers for Semantic Agent Broker
======================================================================

--- Google Gemma 4 BPE Tokenizer (Official Google) ---
Downloading: google/gemma-4-26B-A4B-it
  [SUCCESS] Downloaded 32,169,626 bytes.
  [OK] Validated BPE schema: 262,144 vocab entries, 514,906 merges.

--- Alibaba Qwen 3.8 BPE Tokenizer (Official Alibaba Qwen) ---
Downloading: Qwen/Qwen3.8-27B
  [SUCCESS] Downloaded 12,809,320 bytes.
  [OK] Validated BPE schema: 248,044 vocab entries, 247,587 merges.

======================================================================
Done! Successfully downloaded and validated 2/2 tokenizers.
======================================================================
```

### For Gated Hugging Face Models (e.g., Gemma):
If a model requires accepting terms on Hugging Face:
```bash
python download_tokenizers.py --hf-token "hf_your_actual_token_here" --output-dir "./Tokenizers"
```

### Validation:
The downloader automatically verifies that the downloaded file conforms to the BPE schema expected by `MlTokenCounter.LoadHuggingFaceTokenizerJson`:
- Top-level `"model"` property
- `"model.type" == "BPE"`
- `"vocab"` dictionary / list
- `"merges"` array
