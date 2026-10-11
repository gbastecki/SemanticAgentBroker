# Securing vLLM with Nginx HTTP Basic Authentication

This guide explains how to secure a **vLLM** inference cluster with an **Nginx reverse proxy** enforcing **HTTP Basic Authentication** (`htpasswd`), and how to configure **Semantic Agent Broker** to authenticate seamlessly through the proxy.

---

## 1. Why Front vLLM with Basic Auth?

By default, vLLM is designed for high-performance GPU inference but provides minimal built-in authentication (only a single static `--api-key` flag). In enterprise, air-gapped, or shared team environments:

1. **User Access Control**: You can assign distinct username/password pairs to different agents, teams, or services using standard Apache `htpasswd` files.
2. **Perimeter Security**: Isolate vLLM's internal port `8000` to a private Docker/VPC network, exposing only the hardened Nginx port `8080` (or `443` HTTPS).
3. **Optimized Streaming**: Nginx is configured with `proxy_buffering off;` and `proxy_read_timeout 3600s;` so Server-Sent Events (SSE) token streaming flows immediately to Semantic Agent Broker without premature timeouts or buffering hiccups.
4. **TLS / SSL Termination**: Easily bind custom certificates or Let's Encrypt SSL/TLS certificates at the proxy layer.

---

## 2. Architecture & Flow

```
┌────────────────────────────────────────────────────────┐
│              Semantic Agent Broker                    │
│   (BasicAuthDelegatingHandler injects Basic Auth)      │
└───────────────────────────┬────────────────────────────┘
                            │ HTTP POST /v1/chat/completions
                            │ Authorization: Basic <base64(user:pass)>
                            ▼
┌────────────────────────────────────────────────────────┐
│                   Nginx Reverse Proxy                  │
│                      (Port 8080)                       │
│  - Verifies credentials against /etc/nginx/.htpasswd   │
│  - Disables response buffering (proxy_buffering off)   │
│  - Forwards requests to upstream vllm:8000             │
└───────────────────────────┬────────────────────────────┘
                            │ Internal Docker Network (vllm_net)
                            │ Unauthenticated HTTP
                            ▼
┌────────────────────────────────────────────────────────┐
│                   vLLM Inference Server                │
│                      (Port 8000)                       │
│  - GPU-accelerated tensor-parallel inference           │
└────────────────────────────────────────────────────────┘
```

---

## 3. How Semantic Agent Broker Handles Basic Auth

Semantic Agent Broker includes a specialized DelegatingHandler:
[`BasicAuthDelegatingHandler.cs`](../../../SemanticAgentBroker.Core/Infrastructure/Http/BasicAuthDelegatingHandler.cs).

When a connection in `appsettings.json` has:
```json
"HttpBasicAuthUsername": "agent_user",
"HttpBasicAuthPassword": "SecretAgentPassword123!",
"ApiKey": ""
```

The broker:
1. Detects that `ApiKey` is empty and `HttpBasicAuthUsername` is populated.
2. Registers `BasicAuthDelegatingHandler` in the ASP.NET Core `IHttpClientFactory` pipeline.
3. Automatically encodes `agent_user:SecretAgentPassword123!` to Base64 (`YWdlbnRfdXNlcjpTZWNyZXRBZ2VudFBhc3N3b3JkMTIzIQ==`).
4. Overwrites the `Authorization` header with `Basic YWdlbnRfdXNlcjpTZWNyZXRBZ2VudFBhc3N3b3JkMTIzIQ==` immediately before the request hits the network, preventing Microsoft Semantic Kernel from substituting a dummy Bearer token.

---

## 4. Quick Start with Docker Compose

### Step 1: Clone and Navigate to Proxy Directory
```bash
cd Samples/Configs/Proxy
```

### Step 2: Create Credentials (`.htpasswd`)
Create or edit `.htpasswd` (you can copy the provided `.htpasswd.example`):
```bash
# Using apache2-utils:
htpasswd -bc .htpasswd agent_user SecretAgentPassword123!

# Or using Docker:
docker run --rm httpd:alpine htpasswd -nbB agent_user SecretAgentPassword123! > .htpasswd
```

### Step 3: Launch Containers
```bash
docker compose -f docker-compose.vllm-nginx.yml up -d
```

---

## 5. Testing with `curl`

### 1. Test Without Authentication (Expect `401 Unauthorized`)
```bash
curl -i http://localhost:8080/v1/models
```
**Expected Response:**
```http
HTTP/1.1 401 Unauthorized
Server: nginx
WWW-Authenticate: Basic realm="Protected vLLM AI Inference Service"
Content-Type: text/html
Content-Length: 179
```

### 2. Test With Invalid Credentials (Expect `401 Unauthorized`)
```bash
curl -i -u wrong_user:wrong_pass http://localhost:8080/v1/models
```

### 3. Test With Valid Credentials (Expect `200 OK`)
```bash
curl -i -u agent_user:SecretAgentPassword123! http://localhost:8080/v1/models
```
**Expected Response:**
```http
HTTP/1.1 200 OK
Server: nginx
Content-Type: application/json
...
```

### 4. Test Chat Completions & Streaming
```bash
curl -X POST http://localhost:8080/v1/chat/completions \
  -u agent_user:SecretAgentPassword123! \
  -H "Content-Type: application/json" \
  -d '{
    "model": "Qwen/Qwen3.8-27B",
    "messages": [
      {"role": "system", "content": "You are a helpful AI assistant."},
      {"role": "user", "content": "Hello from Semantic Agent Broker!"}
    ],
    "temperature": 0.0,
    "max_tokens": 100
  }'
```

---

## 6. Broker Configuration Example

In your `appsettings.json` (or copy from [`appsettings.vllm-basic-auth.json`](../appsettings.vllm-basic-auth.json)):

```json
{
  "AiProviders": {
    "ModelRouting": {
      "PrimaryTextModel": {
        "ModelId": "Qwen/Qwen3.8-27B"
      },
      "SummarizationModel": {
        "ModelId": "google/gemma-4-26B-A4B-it"
      }
    },
    "Connections": [
      {
        "Id": "vLLMProxyBasicAuth",
        "EndpointName": "vLLM Secured via Nginx Basic Auth (localhost:8080)",
        "ProviderType": "OpenAiCompatible",
        "Endpoint": "http://localhost:8080/v1",
        "IsEnabled": true,
        "HttpBasicAuthUsername": "agent_user",
        "HttpBasicAuthPassword": "SecretAgentPassword123!",
        "ApiKey": "",
        "TrustUntrustedCertificates": true,
        "Models": [
          {
            "Id": "Qwen/Qwen3.8-27B",
            "Type": "Text",
            "MaxContextTokens": 32768,
            "TokenCounting": {
              "TokenizerModel": "gpt-4o",
              "TokenizerJsonPath": "%CURRENT_USER%\\SemanticAgentBrokerData\\Tokenizers\\qwen_tokenizer.json",
              "FallbackDivisor": 2.3,
              "PerMessageOverhead": 20
            }
          }
        ]
      }
    ]
  }
}
```

---

## 7. Production Hardening Checklist

1. **Enable HTTPS / TLS**:
   Bind SSL certificates inside `nginx-vllm-basic-auth.conf`:
   ```nginx
   listen 8443 ssl http2;
   ssl_certificate /etc/nginx/certs/fullchain.pem;
   ssl_certificate_key /etc/nginx/certs/privkey.pem;
   ```
2. **Strong Passwords & Bcrypt**:
   Generate passwords using bcrypt with at least 12 cost rounds (`htpasswd -B -C 12 .htpasswd username password`).
3. **Firewall / Network Isolation**:
   Ensure vLLM port `8000` is bound only to `127.0.0.1` or internal Docker container networks, never exposed on `0.0.0.0`.
4. **Timeouts**:
   Maintain `proxy_read_timeout 3600s;` to prevent connections from being severed while large models generate complex code or long ReAct plans.
