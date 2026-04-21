# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2026-04-21

### Added

- **Copilot Studio SDK Connection Mode** — Alternative to Direct Line using the `Microsoft.Agents.CopilotStudio.Client` SDK (Direct-to-Engine API). Set `ConnectionMode: CopilotStudioSdk` to enable. Uses SSE streaming instead of HTTP polling for lower latency.
- **OBO (On-Behalf-Of) Token Exchange** — SDK mode exchanges the caller's bearer token for a Copilot Studio API token via MSAL, enabling authenticated API calls with the `CopilotStudio.Copilots.Invoke` scope.
- **SDK SSO Token Exchange** — Detects OAuthCard challenges in SDK activity streams and performs `signin/tokenExchange` via the SDK's `ExecuteAsync`, enabling full SSO in SDK mode.
- **Power Platform Cloud Configuration** — New `Cloud` setting (default: `Prod`) supports sovereign clouds (Gov, High, DoD, Mooncake).
- **Azure AI Foundry Sample Clients** — Portal-based (no code) and Python SDK samples for connecting Foundry agents to the A2A server.

### Changed

- `ConnectionMode` enum added to `CopilotStudioOptions` — controls whether Direct Line or SDK is used. Defaults to `DirectLine` for backward compatibility.
- Health endpoint now returns `{ "status": "healthy", "mode": "<ConnectionMode>" }`.
- SDK mode automatically enables authentication (callers must present a valid bearer token).

### Technical Details

- New file: `Services/CopilotStudioSdkChatClient.cs` — IChatClient implementation for SDK mode
- New NuGet packages: `Microsoft.Agents.CopilotStudio.Client` (1.4.83), `Microsoft.Identity.Client` (4.83.3)
- `CopilotStudioOptions` extended with SDK settings: `ConnectionMode`, `EnvironmentId`, `SchemaName`, `DirectConnectUrl`, `Cloud`
- Fresh `CopilotClient` created per request to avoid conversation state bleed (SDK client is stateful)
- OAuthCard deserialization uses case-insensitive JSON parsing to handle SDK's `JsonElement` content

## [1.0.0] - 2026-04-17

Initial stable release of the Copilot Studio A2A bridge server.

### Added

- **A2A Protocol Bridge** — Exposes Microsoft Copilot Studio agents as A2A (Agent-to-Agent) protocol servers using the Microsoft Agent Framework's `MapA2A()` endpoint
- **Direct Line Integration** — Translates A2A JSON-RPC 2.0 requests into Bot Framework Direct Line API calls (token exchange → conversation → message → poll → response)
- **Agent Card Discovery** — `GET /a2a/copilot-studio/v1/card` returns A2A v0.3.0-compliant agent card for agent discovery
- **Health Check** — `GET /health` endpoint for monitoring
- **Entra ID Authentication (Phase 1)** — Optional JWT bearer token validation on A2A endpoints with opaque user identity passthrough to Direct Line
- **SSO Token Exchange (Phase 2)** — Full Single Sign-On flow: intercepts OAuthCard challenges from Copilot Studio and performs `signin/tokenExchange` with the caller's bearer token, enabling the bot to make API calls on behalf of the authenticated user
- **Google ADK Sample Client** — Complete sample with Gemini-powered orchestrator (`client.py`), direct A2A client (`direct_client.py`), and ADK web UI support
- **Automated Test Script** — Python test suite (`samples/test_server.py`) covering health check, agent card, error handling, and message send
- **Architecture Documentation** — Mermaid diagrams showing component overview, SSO sequence flow, and deployment architecture
- **Authentication Guide** — Step-by-step guide for Entra ID app registration, Copilot Studio SSO configuration, and troubleshooting

### Technical Details

- .NET 10 / ASP.NET Core
- A2A Protocol v0.3.0
- Microsoft Agent Framework (preview NuGet)
- Configurable Direct Line polling interval and response timeout
- Stateless design — each request creates a new Direct Line conversation, enabling horizontal scaling

[1.0.0]: https://github.com/bradrlaw/copilot-studio-a2a/releases/tag/v1.0.0
