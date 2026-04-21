# Architecture

## High-Level Overview

```mermaid
graph LR
    subgraph Clients
        ADK["Google ADK<br/>Agent"]
        SK["Semantic Kernel<br/>Agent"]
        Other["Other A2A<br/>Clients"]
    end

    subgraph A2A["Copilot Studio A2A Server"]
        EP["A2A Endpoint<br/>/a2a/copilot-studio"]
        Auth["JWT Validation<br/>(Entra ID)"]
        Bridge["CopilotStudioChatClient<br/>(IChatClient)"]
        SdkBridge["CopilotStudioSdkChatClient<br/>(IChatClient)"]
        SSO["SSO Token<br/>Exchange"]
        OBO["OBO Token<br/>Exchange (MSAL)"]
    end

    subgraph Microsoft["Microsoft Cloud"]
        DL["Direct Line API<br/>(Bot Framework)"]
        CopilotStudioAPI["Copilot Studio<br/>Direct-to-Engine API"]
        CS["Copilot Studio<br/>Agent"]
        Graph["Microsoft Graph<br/>& Connectors"]
    end

    ADK -- "JSON-RPC 2.0<br/>+ Bearer Token" --> EP
    SK -- "JSON-RPC 2.0<br/>+ Bearer Token" --> EP
    Other -- "JSON-RPC 2.0<br/>+ Bearer Token" --> EP

    EP --> Auth
    Auth --> Bridge
    Auth --> SdkBridge
    Bridge -- "REST API" --> DL
    Bridge <-. "OAuthCard<br/>Challenge" .-> SSO
    SSO -- "signin/tokenExchange" --> DL
    SdkBridge -- "OBO Exchange" --> OBO
    OBO -- "SSE API" --> CopilotStudioAPI
    DL <--> CS
    CopilotStudioAPI <--> CS
    CS -- "User's Token" --> Graph
```

## Direct Line Mode Request Flow

```mermaid
sequenceDiagram
    participant Client as A2A Client
    participant Server as A2A Server
    participant DL as Direct Line
    participant Bot as Copilot Studio

    Client->>Server: POST /a2a/copilot-studio<br/>Authorization: Bearer <token>
    
    Note over Server: Validate JWT (Entra ID)
    
    Server->>DL: POST /tokens/generate<br/>(no user ID in SSO mode)
    DL-->>Server: Direct Line token
    
    Server->>DL: POST /conversations
    DL-->>Server: conversationId
    
    Server->>DL: POST /activities<br/>{ type: "message", text: "..." }
    
    loop Poll for response
        Server->>DL: GET /activities?watermark=N
        DL-->>Server: activities[]
    end

    Bot->>DL: OAuthCard (Sign In topic)
    DL-->>Server: activity with OAuthCard attachment

    Note over Server: Extract connectionName,<br/>tokenExchangeResource.id

    Server->>DL: POST /activities<br/>{ type: "invoke",<br/>  name: "signin/tokenExchange",<br/>  value: { token: <caller's bearer> } }
    
    Note over DL,Bot: Bot Framework validates token<br/>against Token Exchange URL

    Bot->>DL: Authenticated response
    DL-->>Server: activity with bot reply

    Server-->>Client: JSON-RPC 2.0 response<br/>{ result: { parts: [...] } }
```

## SDK Mode Request Flow

```mermaid
sequenceDiagram
    participant Client as A2A Client
    participant Server as A2A Server
    participant MSAL as MSAL (OBO)
    participant CSAPI as Copilot Studio API
    participant Bot as Copilot Studio

    Client->>Server: POST /a2a/copilot-studio<br/>Authorization: Bearer <token>

    Note over Server: Validate JWT (Entra ID)

    Server->>MSAL: OBO exchange<br/>(caller token → Copilot Studio API token)
    MSAL-->>Server: API token

    Server->>CSAPI: StartConversationAsync (SSE)
    CSAPI-->>Server: activities (typing, greeting, OAuthCard)

    Note over Server: Detect OAuthCard

    Server->>CSAPI: ExecuteAsync<br/>(signin/tokenExchange)
    CSAPI-->>Server: invokeResponse +<br/>authenticated greeting

    Server->>CSAPI: ExecuteAsync<br/>(user message)
    CSAPI-->>Server: response activities

    Server-->>Client: JSON-RPC 2.0 response<br/>{ result: { parts: [...] } }
```

## Component Details

### A2A Endpoint Layer

The `MapA2A()` extension from `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` handles JSON-RPC 2.0 protocol details natively — parsing requests, routing to the `IChatClient`, and formatting responses. The server adds:

- **JWT middleware** — validates Entra ID bearer tokens on POST requests when `EnableAuthPassthrough` is enabled
- **Agent card** — served at `GET /a2a/copilot-studio/v1/card` for A2A agent discovery

### CopilotStudioChatClient

The core bridge component implementing `IChatClient`. Responsibilities:

| Method | Purpose |
|--------|---------|
| `GetResponseAsync` | Orchestrates the full flow: token → conversation → message → poll → response |
| `GetTokenAsync` | Exchanges Direct Line secret for a scoped token |
| `StartConversationAsync` | Opens a new Direct Line conversation |
| `SendMessageAsync` | Posts a user message as a Direct Line activity |
| `PollForResponseAsync` | Polls for bot replies, handles OAuthCard interception |
| `SendTokenExchangeAsync` | Sends `signin/tokenExchange` invoke for SSO |
| `ExtractOAuthCardInfo` | Parses OAuthCard attachments from bot activities |
| `ResolveDirectLineUserId` | Derives a stable user ID from JWT claims |

### CopilotStudioSdkChatClient

The SDK-based bridge implementing `IChatClient`, using the `Microsoft.Agents.CopilotStudio.Client` package for direct-to-engine communication. Responsibilities:

| Method | Purpose |
|--------|---------|
| `GetResponseAsync` | Orchestrates: OBO token → StartConversation → SSO exchange → Send message → Collect response |
| `CreateCopilotClientAsync` | Creates a fresh CopilotClient with OBO token provider |
| `PerformSsoTokenExchangeAsync` | Sends signin/tokenExchange invoke via SDK when OAuthCard detected |
| `ExtractOAuthCard` | Parses OAuthCard from SDK Activity attachments |
| `GetCallerBearerToken` | Extracts raw JWT from HTTP Authorization header |

Key differences from Direct Line mode:
- Uses SSE streaming instead of polling
- Requires OBO (On Behalf Of) token exchange via MSAL for API authentication
- Creates a fresh `CopilotClient` per request (the SDK client is stateful)
- SSO token exchange is sent via `ExecuteAsync` instead of Direct Line REST

### SSO Token Exchange (Direct Line Mode)

When SSO is enabled (Phase 2), the server acts as the "canvas" in Microsoft's SSO pattern:

1. **No trusted user ID** — Direct Line tokens are generated without a `dl_`-prefixed user ID, allowing the bot's Sign In system topic to trigger
2. **OAuthCard interception** — when the bot sends an OAuthCard with `tokenExchangeResource`, the server intercepts it instead of displaying a sign-in prompt
3. **Token passthrough** — the caller's original bearer token (audience: `api://<clientId>`) is sent directly via `signin/tokenExchange` — no OBO (On-Behalf-Of) exchange needed since the audience already matches the Token Exchange URL
4. **Retry detection** — if Direct Line returns `"retry"` in the response, the exchange failed and the bot may fall back to a sign-in prompt

> **Note:** SDK mode uses the same SSO pattern (OAuthCard interception → token exchange) but with different transport — token exchange is sent via the SDK's `ExecuteAsync` method instead of Direct Line REST, and the caller's token is first exchanged for a Copilot Studio API token using MSAL OBO.

## Deployment Architecture

```mermaid
graph TB
    subgraph "Client Environment"
        C1["A2A Client 1"]
        C2["A2A Client 2"]
    end

    subgraph "Server Environment"
        LB["Load Balancer"]
        S1["A2A Server<br/>Instance 1"]
        S2["A2A Server<br/>Instance 2"]
    end

    subgraph "Azure / Microsoft 365"
        AAD["Entra ID<br/>(Token Validation)"]
        BF["Bot Framework<br/>(Direct Line)"]
        CSAPI["Copilot Studio<br/>Direct-to-Engine API"]
        CPS["Copilot Studio"]
    end

    C1 --> LB
    C2 --> LB
    LB --> S1
    LB --> S2
    S1 --> AAD
    S2 --> AAD
    S1 -- "Direct Line Mode" --> BF
    S2 -- "Direct Line Mode" --> BF
    S1 -. "SDK Mode" .-> CSAPI
    S2 -. "SDK Mode" .-> CSAPI
    BF --> CPS
    CSAPI --> CPS
```

The server is stateless — each request creates a new conversation (Direct Line conversation or SDK session). This makes horizontal scaling straightforward: deploy multiple instances behind a load balancer with no shared state required.

### Configuration

Secrets are managed via:
- **Local development**: `dotnet user-secrets`
- **Production**: Environment variables or a secrets vault (e.g., Azure Key Vault)

See [authentication.md](authentication.md) for the complete setup guide.
