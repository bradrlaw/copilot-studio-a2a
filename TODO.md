# Project TODO

Planned enhancements and improvements for the Copilot Studio A2A Server.

## Security & Authentication

- [x] **A2A endpoint authentication** — Entra ID (Azure AD) bearer token validation on A2A endpoints (opt-in via `EnableAuthPassthrough`). See [docs/authentication.md](docs/authentication.md)
- [x] **Authentication passthrough (Phase 1)** — Validated user identity is derived from JWT claims and passed as an opaque user ID to Direct Line. See [docs/authentication.md](docs/authentication.md)
- [x] **Authentication passthrough (Phase 2: SSO)** — Full SSO token exchange so Copilot Studio receives the caller's Entra ID token via the OAuthCard → `signin/tokenExchange` flow. The bot can make downstream API calls on behalf of the authenticated user. See [docs/authentication.md](docs/authentication.md)
- [ ] **Rate limiting** — Add per-client rate limiting to prevent abuse of the A2A endpoint
- [ ] **A2A security schemes in agent card** — Populate the `securitySchemes` and `security` fields in the agent card so clients know what auth is required

## Conversation & State Management

- [ ] **Conversation persistence** — Reuse Direct Line conversations across A2A requests (currently each request opens a new conversation, losing context)
- [ ] **Context ID mapping** — Map A2A `contextId` to Direct Line `conversationId` for multi-turn conversations
- [ ] **Session timeout / cleanup** — Expire idle conversations after a configurable period to free resources
- [ ] **State transition history** — Enable the `StateTransitionHistory` capability in the agent card and track message states (submitted → working → completed)

## Streaming & Performance

- [ ] **SSE streaming support** — Implement `message/stream` using Direct Line's WebSocket `streamUrl` to stream partial responses as they arrive
- [ ] **Connection pooling** — Reuse Direct Line tokens and connections where possible instead of creating new ones per request
- [ ] **Response caching** — Cache agent card responses (they rarely change) to reduce load

## Multi-Agent Support

- [ ] **Multiple agent registration** — Support hosting multiple Copilot Studio agents on different paths (e.g., `/a2a/banking`, `/a2a/support`) from a single server instance
- [ ] **Dynamic agent discovery** — Load agent configurations from a config file or database instead of hardcoding a single agent
- [ ] **Agent card skills** — Populate the `skills` array in the agent card with the Copilot Studio agent's actual topic list

## Reliability & Observability

- [ ] **Startup validation** — Validate that Direct Line credentials are configured and reachable on startup, fail fast with a clear error if not
- [ ] **Structured logging** — Add correlation IDs that trace a request from A2A → Direct Line → Copilot Studio and back
- [ ] **Health check: deep** — Extend `/health` to optionally validate Direct Line connectivity (e.g., `/health?deep=true`)
- [ ] **Metrics / telemetry** — Add OpenTelemetry or Application Insights for request latency, error rates, and Direct Line round-trip times
- [ ] **Retry logic** — Add configurable retries with exponential backoff for transient Direct Line failures (429, 503)

## Deployment & Infrastructure

- [ ] **Dockerfile** — Add a production-ready Dockerfile based on the ASP.NET Core container image
- [ ] **Docker Compose** — Add a `docker-compose.yml` for one-command local testing (server + sample client)
- [ ] **CI/CD pipeline** — Add GitHub Actions workflow for build, test, and publish
- [ ] **Helm chart / Azure deployment template** — Provide one-click deployment to Azure Container Apps or AKS

## Testing

- [ ] **Unit tests** — Add xUnit tests for `CopilotStudioChatClient` with mocked HttpClient (token exchange, conversation, polling)
- [ ] **Integration tests** — Add end-to-end tests using `WebApplicationFactory` that test the full A2A JSON-RPC flow with a mock Direct Line backend
- [ ] **Load testing** — Add a k6 or Locust script for basic load testing

## Developer Experience

- [ ] **Configuration validation** — Use `IValidateOptions` to catch misconfigurations at startup (missing secret, invalid URLs)
- [ ] **`appsettings.template.json`** — Provide a template config file showing all available options with comments
- [ ] **Push notifications** — Implement the A2A push notification capability for long-running agent tasks
- [ ] **File / attachment support** — Support A2A `file` parts by sending them as Direct Line attachments and returning bot attachments as A2A file parts
- [ ] **Rich content mapping** — Map Copilot Studio Adaptive Cards to A2A data parts or structured output

## Documentation

- [x] **Architecture diagram** — Mermaid diagrams showing high-level overview, SSO sequence flow, and deployment architecture. See [docs/architecture.md](docs/architecture.md)
- [ ] **Contributing guide** — Add CONTRIBUTING.md with dev setup, coding standards, and PR process
- [x] **Changelog** — CHANGELOG.md following [Keep a Changelog](https://keepachangelog.com) format with SemVer
- [ ] **Additional sample clients** — Add samples for other A2A client frameworks (Semantic Kernel, LangGraph, CrewAI)
