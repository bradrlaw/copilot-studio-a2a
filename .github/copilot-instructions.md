# Copilot Studio A2A — Copilot Instructions

## Project Overview

This is a .NET 10 ASP.NET Core application that exposes a **Microsoft Copilot Studio** agent as an **A2A (Agent-to-Agent) protocol** server. It acts as a bridge: A2A clients send JSON-RPC 2.0 requests, which this server translates into Bot Framework Direct Line API calls to a Copilot Studio agent, then returns the responses in A2A format.

### Architecture

```
A2A Client ──JSON-RPC 2.0──▶ This Server ──Direct Line API──▶ Copilot Studio Agent
```

## Tech Stack

- **Runtime**: .NET 10 / ASP.NET Core
- **A2A Protocol**: `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` (preview NuGet package from Microsoft Agent Framework) — handles JSON-RPC 2.0 natively via `MapA2A()`
- **Chat Abstraction**: `Microsoft.Extensions.AI.IChatClient` — the standard .NET AI chat interface
- **Bot Connectivity**: Bot Framework Direct Line API v3
- **API Docs**: Swagger / OpenAPI via Swashbuckle

## Key Components

| File | Purpose |
|---|---|
| `Program.cs` | App startup — registers DI services, maps A2A endpoints, configures the agent card |
| `Services/CopilotStudioChatClient.cs` | `IChatClient` implementation that proxies to Copilot Studio via Direct Line (token exchange → start conversation → send message → poll for response) |
| `Services/CopilotStudioOptions.cs` | Strongly-typed config POCO bound to `CopilotStudio` section in appsettings |
| `appsettings.json` | Default configuration including Direct Line endpoint, polling settings, and A2A agent card metadata |
| `samples/google_adk_client/` | Google ADK sample: orchestrator client (`client.py`) and direct A2A client (`direct_client.py`) |

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
- `EnableAuthPassthrough` — enable Entra ID bearer token validation on A2A endpoints (default: `false`)
- `AzureAd` — nested config for Entra ID auth (`Instance`, `TenantId`, `ClientId`); only used when `EnableAuthPassthrough` is `true`

The `A2A` config section controls the agent card metadata (`AgentName`, `AgentDescription`, `AgentUrl`).

### Authentication

When `EnableAuthPassthrough` is `true`:
- A2A endpoints require a valid Entra ID bearer token
- **SSO mode**: When a bearer token is present and Copilot Studio auth is configured, the server performs an SSO token exchange:
  1. Direct Line token is generated **without** a trusted `dl_` user ID (to allow the bot's Sign In topic to trigger)
  2. The bot sends an OAuthCard challenge
  3. The server intercepts it and sends a `signin/tokenExchange` invoke with the caller's original bearer token
  4. The bot receives the user's identity and can make API calls on their behalf
- **Phase 1 only** (no SSO config): Uses an opaque per-user ID (SHA-256 of tenant+subject) passed as the Direct Line `user.id`
- See [docs/authentication.md](../docs/authentication.md) for the full setup guide

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
- The A2A protocol version is **0.3.0** — uses `message/send` method (not `tasks/send`), `kind` field (not `type`), and requires `messageId` on messages
- Each A2A request opens a new Direct Line conversation — there is no conversation persistence across requests
