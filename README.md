# NetCheck (Proof of Concept)

NetCheck is a .NET 8 ASP.NET Core Web API proof‑of‑concept for validating pull request metadata (title pattern and body hyperlink requirements) using an AI agent pipeline. It currently uses a local Ollama model for experimentation while retaining optional wiring for Azure OpenAI (not yet accessible in this environment).

> Status: POC / experimental. Not production hardened. Interfaces, prompts and data flow are likely to change.

## Why Ollama?
Azure OpenAI access is not yet available for this project, so a local Ollama instance is used to iterate quickly. The code already contains an `AddOpenAIChatClient` path that can be enabled once credentials are provided. Both approaches share the same abstraction (`IChatClient` via `Microsoft.Extensions.AI`).

## Key Features
- AI multi‑phase agent (retrieval → normalisation → rule evaluation → aggregation)
- Optional MCP (Model Context Protocol) client (SSE or stdio transports)
- Local tools (e.g. JSON parsing demo) prepared for function invocation
- Token estimation helpers (GPT + LLaMA)
- Hosted startup validation for ensuring the Ollama model is present
- Minimal coloured console logger
- Unit test project (`NetCheck.Tests`) with baseline coverage for controllers, utilities, and JSON consolidation logic

## High Level Flow
1. `NetCheckController` triggers `AIEngine.RunAgent()`
2. Engine asks MCP for tools and performs one `search_pull_requests` call
3. AI normalises raw tool output into a constrained JSON array
4. Title rule and body hyperlink rule produce failure arrays
5. A consolidated JSON report (with stats) is returned

## Project Structure
```
NetCheck/                Main ASP.NET Core Web API
  Extensions/            Service registration (Ollama, MCP, OpenAI wiring)
  Services/              AI engine + model service abstractions
  HostedServices/        Model validation on startup
  Logging/               Minimal console formatter
  Tools/                 Local AI tool examples
  Utility/               Token estimators
NetCheck.Tests/          xUnit test suite
```

## Configuring AI Backends
Configuration is read from `appsettings*.json` or environment variables.

### Ollama (active by default)
```
AI:Ollama:Endpoint = http://localhost:11434
AI:Ollama:Model    = llama3.2:3b   (or llama3.2:8b etc.)
```
The `OllamaService` will attempt to ensure the model is present (pull if missing).

### Azure OpenAI (optional / currently inactive)
To enable, call `AddOpenAIChatClient` instead of (or alongside) the Ollama registration (code already present but not invoked). Required keys:
```
AI:OpenAI:Url   = https://<your-resource>.openai.azure.com/
AI:OpenAI:Key   = <api-key>
AI:OpenAI:Model = <deployment-name>
```
If values are missing the OpenAI client registration throws an error during DI construction.

### MCP (Optional)
Provide either a URL (SSE) or a command (stdio):
```
AI:Mcp:Url       = https://your-mcp-endpoint
AI:Mcp:Token     = <token-if-required>
# OR
AI:Mcp:Command   = github-mcp-server
AI:Mcp:Arguments = --flag1 value1
```
If neither URL nor command is set, MCP wiring is skipped silently.

## Running Locally
1. Install .NET 8 SDK
2. Install and start Ollama: https://ollama.com
3. (Optional) Pre‑pull model: `ollama pull llama3.2:3b`
4. Clone repository
5. `dotnet run --project NetCheck`
6. Browse: `https://localhost:5001/NetCheck` (or the configured port)

Swagger UI is enabled in Development.

## Tests
Run:
```
dotnet test
```
Current tests cover:
- Controller happy path
- Basic JSON parse utility
- JSON consolidation via reflection of private method
- Ollama service negative path

## Extending
- Enable Azure OpenAI once credentials exist by invoking `AddOpenAIChatClient(builder)` in `AddAIServices`
- Add further MCP tools for deeper repository analysis
- Harden JSON extraction and error recovery strategies

## Limitations (POC)
- Minimal error handling for AI / MCP streaming
- Reflection used in tests (might refactor to expose smaller composition units)
- No authentication / authorisation
- Network calls (token estimator & model checks) are not cached


## Disclaimer
This repository is experimental and not intended for production use without additional security, resilience and validation work.
