# Copilot Studio A2A — Copilot Instructions

## Project Overview

This is a .NET 10 ASP.NET Core application that exposes a **Microsoft Copilot Studio** agent as an **A2A (Agent-to-Agent) protocol** server. It acts as a bridge: A2A clients send JSON-RPC 2.0 requests, which this server translates into Bot Framework Direct Line API calls to a Copilot Studio agent, then returns the responses in A2A format.

### Architecture

```
A2A Client ──JSON-RPC 2.0──▶ This Server ──Direct Line API──▶ Copilot Studio Agent
```

## Tech Stack

- **Runtime**: .NET 10 / ASP.NET Core
- **A2A Protocol**: `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` (preview NuGet package from Microsoft Agent Framework)
- **Chat Abstraction**: `Microsoft.Extensions.AI.IChatClient` — the standard .NET AI chat interface
- **Bot Connectivity**: Bot Framework Direct Line API v3
- **API Docs**: Swagger / OpenAPI via Swashbuckle

## Key Components

| File | Purpose |
|---|---|
| `Program.cs` | App startup — registers DI services, maps A2A endpoints, configures the agent card |
| `Services/CopilotStudioChatClient.cs` | `IChatClient` implementation that proxies to Copilot Studio via Direct Line (token exchange → start conversation → send message → poll for response) |
| `Services/CopilotStudioOptions.cs` | Strongly-typed config POCO bound to `CopilotStudio` section in appsettings |
| `Middleware/JsonRpcMiddleware.cs` | ASP.NET middleware that unwraps incoming JSON-RPC 2.0 envelopes and re-wraps responses, providing protocol compatibility |
| `appsettings.json` | Default configuration including Direct Line endpoint, polling settings, and A2A agent card metadata |

## Endpoints

| Route | Method | Description |
|---|---|---|
| `/health` | GET | Health check |
| `/a2a/copilot-studio/v1/card` | GET | A2A Agent Card (discovery) |
| `/a2a/copilot-studio` | POST | A2A JSON-RPC task endpoint |
| `/swagger` | GET | Swagger UI |

## Configuration

Secrets (Direct Line secret or token endpoint URL) should be stored via `dotnet user-secrets` and never committed. The `CopilotStudio` config section in `appsettings.json` controls:

- `DirectLineSecret` — the Direct Line channel secret
- `DirectLineEndpoint` — base URL for Direct Line API (default: `https://directline.botframework.com/v3/directline`)
- `TokenEndpoint` — optional regional token endpoint (used instead of secret when set)
- `ResponseTimeoutSeconds` — max wait for bot response (default: 60)
- `PollingIntervalMs` — Direct Line polling interval (default: 500ms)

The `A2A` config section controls the agent card metadata (`AgentName`, `AgentDescription`, `AgentUrl`).

## Coding Conventions

- Use file-scoped namespaces
- Use nullable reference types (`<Nullable>enable</Nullable>`)
- Use implicit usings
- Follow standard ASP.NET Core patterns (DI, options pattern, middleware pipeline)
- XML doc comments on public types and methods
- Keep the project minimal — this is a focused bridge/proxy, not a framework

## Important Notes

- The A2A NuGet package is a **preview** (`1.0.0-preview.*`); APIs may change
- Direct Line does not support true streaming — `GetStreamingResponseAsync` returns a single update
- The JSON-RPC middleware handles both SSE-format and plain JSON responses from the inner pipeline
- Error responses from the inner pipeline are wrapped in JSON-RPC error objects with the original status code
