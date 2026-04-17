# Copilot Studio A2A Server

Exposes a Microsoft Copilot Studio agent as an [A2A (Agent-to-Agent) protocol](https://github.com/a2aproject/A2A) server using the [Microsoft Agent Framework](https://github.com/microsoft/Agents-for-net).

## How It Works

This application acts as a bridge between the A2A protocol and Microsoft Copilot Studio. A2A clients communicate with this server using JSON-RPC 2.0, and the server translates those requests into Bot Framework Direct Line API calls to reach the Copilot Studio agent.

```
A2A Client ──JSON-RPC 2.0──▶ This Server ──Direct Line API──▶ Copilot Studio Agent
```

> 📐 See [docs/architecture.md](docs/architecture.md) for detailed diagrams including the SSO token exchange flow.

### Request Flow

1. An A2A client sends a JSON-RPC 2.0 `message/send` request to `/a2a/copilot-studio`
2. The Microsoft Agent Framework (`MapA2A()`) handles JSON-RPC 2.0 natively and routes the request to **CopilotStudioChatClient**, which:
   - Exchanges credentials for a **Direct Line token** (via secret or regional token endpoint)
   - Opens a new Direct Line **conversation**
   - Sends the user's message as a Direct Line **activity**
   - **Polls** for the bot's reply (configurable timeout and interval)
3. The framework wraps the response into a JSON-RPC 2.0 envelope and returns it to the client

### Agent Discovery

Other A2A agents discover this one by calling `GET /a2a/copilot-studio/v1/card`, which returns the agent card containing the agent's name, description, URL, and capabilities.

## Project Structure

```
├── Program.cs                          # App startup, DI, endpoint mapping, agent card config
├── Services/
│   ├── CopilotStudioChatClient.cs      # IChatClient implementation proxying to Direct Line API
│   └── CopilotStudioOptions.cs         # Strongly-typed configuration (bound to appsettings)
├── docs/
│   ├── architecture.md                 # Architecture diagrams (Mermaid)
│   └── authentication.md               # Authentication setup guide (Entra ID / Azure AD)
├── appsettings.json                    # Default configuration (endpoints, polling, agent card)
├── appsettings.Development.json        # Development overrides
├── CopilotStudioA2A.csproj             # .NET 10 project file and NuGet dependencies
└── samples/
    └── google_adk_client/              # Google ADK sample client (see below)
```

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A published Copilot Studio agent with the **Direct Line** channel enabled

## Configuration

### Copilot Studio Credentials

You need either a Direct Line **secret** or a **token endpoint URL** from Copilot Studio. Never commit secrets to source control — use `dotnet user-secrets` for local development and environment variables for production.

#### Option A: Direct Line Secret

1. In Copilot Studio → **Settings → Channels → Direct Line**
2. Copy the **Secret key**
3. Store it locally:

```bash
dotnet user-secrets init
dotnet user-secrets set "CopilotStudio:DirectLineSecret" "YOUR_SECRET_HERE"
```

#### Option B: Token Endpoint (regional deployments)

If your Copilot Studio agent uses a regional endpoint:

```bash
dotnet user-secrets set "CopilotStudio:TokenEndpoint" "https://defaultXXXXXX.XX.environment.api.powerplatform.com/powervirtualagents/botsbyschema/{bot-schema}/directline/token?api-version=2022-03-01-preview"
```

When a token endpoint is configured, it takes priority over the Direct Line secret.

### A2A Agent Card

Customize the agent card metadata in `appsettings.json`. The `AgentUrl` should match the public URL where this server is deployed:

```json
{
  "A2A": {
    "AgentName": "My Copilot Studio Agent",
    "AgentDescription": "Handles customer support inquiries.",
    "AgentUrl": "https://your-deployed-url.com/a2a/copilot-studio"
  }
}
```

### Authentication (Optional)

You can protect the A2A endpoints with Microsoft Entra ID (Azure AD) bearer token authentication. When enabled, callers must present a valid token, and the server derives a per-user identity for Direct Line.

See **[docs/authentication.md](docs/authentication.md)** for the full setup guide.

Quick start:

```bash
dotnet user-secrets set "CopilotStudio:EnableAuthPassthrough" "true"
dotnet user-secrets set "CopilotStudio:AzureAd:TenantId" "<your-tenant-id>"
dotnet user-secrets set "CopilotStudio:AzureAd:ClientId" "<your-client-id>"
```

### Tuning

The following settings in the `CopilotStudio` section of `appsettings.json` control Direct Line communication behavior:

| Setting | Default | Description |
|---|---|---|
| `DirectLineEndpoint` | `https://directline.botframework.com/v3/directline` | Base URL for the Direct Line API |
| `ResponseTimeoutSeconds` | `60` | Maximum seconds to wait for a bot response before timing out |
| `PollingIntervalMs` | `500` | Milliseconds between polls to Direct Line for new activities |

## Running Locally

```bash
dotnet restore
dotnet run
```

By default the app starts on `http://localhost:5173` (configured in `Properties/launchSettings.json`).

### Endpoints

| Endpoint | Method | Description |
|---|---|---|
| `/health` | GET | Health check |
| `/a2a/copilot-studio/v1/card` | GET | A2A Agent Card (discovery) |
| `/a2a/copilot-studio` | POST | A2A JSON-RPC task endpoint |
| `/swagger` | GET | Swagger UI |

## Deployment

### Azure App Service

1. Publish the application:

   ```bash
   dotnet publish -c Release -o ./publish
   ```

2. Deploy the `./publish` folder to an Azure App Service configured with the **.NET 10** runtime.

3. Set secrets as **Application Settings** (Configuration) in the Azure portal. Use double underscores (`__`) as the section separator:

   ```
   CopilotStudio__DirectLineSecret = YOUR_SECRET_HERE
   ```

   Or, if using a token endpoint:

   ```
   CopilotStudio__TokenEndpoint = https://defaultXXXXXX.XX.environment.api.powerplatform.com/...
   ```

4. Update the agent card URL to your App Service domain:

   ```
   A2A__AgentUrl = https://your-app.azurewebsites.net/a2a/copilot-studio
   ```

### Docker / Container

Build a standard ASP.NET Core container and pass secrets via environment variables:

```bash
docker build -t copilot-studio-a2a .
docker run -p 5000:8080 \
  -e CopilotStudio__DirectLineSecret="YOUR_SECRET" \
  -e A2A__AgentUrl="https://your-public-url/a2a/copilot-studio" \
  copilot-studio-a2a
```

> **Note:** A Dockerfile is not included yet — use the standard [ASP.NET Core container image](https://learn.microsoft.com/en-us/dotnet/core/docker/build-container) as a base.

### Dev Tunnels / ngrok

For quick testing without a full deployment, expose your local server publicly:

```bash
# Using VS Dev Tunnels
devtunnel host -p 5173

# Using ngrok
ngrok http 5173
```

Then use the generated public URL as your `A2A:AgentUrl`.

## Connecting from Copilot Studio

Once this server is publicly accessible:

1. In Copilot Studio → **Add agent → Connect to external agent → Agent2Agent**
2. Paste the URL: `https://your-public-url/a2a/copilot-studio`
3. Copilot Studio will fetch the agent card and begin routing relevant tasks to your agent

## Testing Locally

> **Note:** The examples below assume the Copilot Studio agent being connected to is a **virtual banking agent** that can answer questions about branch hours, account inquiries, transfers, and general banking help. Replace the sample messages with questions relevant to your own agent.

### 1. Verify the Server Starts

```bash
dotnet run
```

You should see:

```
Now listening on: http://localhost:5173
Application started. Press Ctrl+C to shut down.
```

### 2. Health Check (no credentials needed)

```bash
curl http://localhost:5173/health
```

Expected response:

```json
{ "status": "healthy" }
```

### 3. Agent Card Discovery (no credentials needed)

```bash
curl http://localhost:5173/a2a/copilot-studio/v1/card
```

Expected response — a JSON object containing `name`, `description`, `url`, `capabilities`, and `protocolVersion`.

### 4. Send a Message (requires Direct Line credentials)

The A2A protocol (v0.3.0) uses JSON-RPC 2.0 with the `message/send` method. Each message requires a `kind`, `messageId`, `role`, and `parts` array:

```bash
curl -X POST http://localhost:5173/a2a/copilot-studio \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": "1",
    "method": "message/send",
    "params": {
      "message": {
        "kind": "message",
        "messageId": "msg-001",
        "role": "user",
        "parts": [{ "kind": "text", "text": "Hello!" }]
      }
    }
  }'
```

Expected response:

```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "result": {
    "kind": "message",
    "role": "agent",
    "parts": [{ "kind": "text", "text": "Hello, how can I help you today?" }],
    "messageId": "...",
    "contextId": "..."
  }
}
```

### Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `Failed to generate Direct Line token` | Missing or invalid Direct Line secret | Verify secret with `dotnet user-secrets list` |
| `IntegratedAuthenticationNotSupportedInChannel` | Copilot Studio agent uses an incompatible auth provider | Switch to **"Microsoft Entra ID v2 with client secrets"** — see [docs/authentication.md](docs/authentication.md) |
| `No response from Copilot Studio within 60 seconds` | Bot didn't reply in time | Increase `ResponseTimeoutSeconds` in `appsettings.json`, or check that the agent is published |
| Bot says "I'll need you to sign in" | SSO not configured or misconfigured | Follow the Phase 2 setup in [docs/authentication.md](docs/authentication.md) |

### PowerShell (Windows)

```powershell
# Health check
Invoke-RestMethod http://localhost:5173/health

# Send a message
$body = @{
    jsonrpc = "2.0"
    id = "1"
    method = "message/send"
    params = @{
        message = @{
            kind = "message"
            messageId = "msg-001"
            role = "user"
            parts = @(@{ kind = "text"; text = "Hello!" })
        }
    }
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Uri http://localhost:5173/a2a/copilot-studio `
  -Method Post -ContentType "application/json" -Body $body
```

## Contributing

This project welcomes contributions and suggestions. Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit [https://cla.opensource.microsoft.com](https://cla.opensource.microsoft.com).

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

Please see [CONTRIBUTING.md](CONTRIBUTING.md) for detailed contribution guidelines.

## Known Limitations

- The A2A NuGet package (`Microsoft.Agents.AI.Hosting.A2A.AspNetCore`) is a **preview** release — APIs may change in future versions
- Direct Line does not support true streaming, so the `Streaming` capability is set to `false` in the agent card
- Each A2A request opens a new Direct Line conversation — there is no conversation persistence across requests

## Sample Clients

### Google ADK Client (LLM-Orchestrated)

A full Google ADK orchestrator that uses Gemini to decide when to delegate to the Copilot Studio agent. See [`samples/google_adk_client/README.md`](samples/google_adk_client/README.md) for details.

**Prerequisites:**

- Python 3.12+
- A [Google Cloud project](https://console.cloud.google.com/) with the **Generative Language API** enabled
- A [Gemini API key](https://aistudio.google.com/apikey) with an active billing account (free tier may work for limited testing)

**Quick start:**

```bash
# Install dependencies
pip install "google-adk[a2a]"

# Set your Gemini API key
# macOS / Linux:
export GOOGLE_API_KEY=your-gemini-api-key
# PowerShell:
$env:GOOGLE_API_KEY = "your-gemini-api-key"

# Start the A2A server (from repo root)
dotnet run

# In a second terminal, run the ADK client
cd samples/google_adk_client
python client.py
```

The examples assume the Copilot Studio agent is a **virtual banking agent** that can answer questions about branch hours, accounts, and general banking help. The orchestrator (Gemini) analyzes the user's question and delegates banking-related queries to Copilot Studio via the A2A protocol. Non-banking questions are handled directly by Gemini. Adjust the orchestrator instructions and sample queries for your own agent.

### ADK Web UI (Interactive Browser Chat)

The Google ADK includes a built-in web interface for testing agents interactively. This is the easiest way to experiment with different messages:

```bash
# Set your Gemini API key (if not already set)
# macOS / Linux:
export GOOGLE_API_KEY=your-gemini-api-key
# PowerShell:
$env:GOOGLE_API_KEY = "your-gemini-api-key"

# Start the A2A server (from repo root)
dotnet run

# In a second terminal, run the ADK web UI from the samples/ directory
cd samples
adk web .
```

Then open **http://127.0.0.1:8000** in your browser, select **google_adk_client** from the agent dropdown, and start chatting.

> **Important:** Run `adk web .` from the `samples/` directory (the parent of the agent folder), not from inside `google_adk_client/`. ADK discovers agents by scanning subdirectories for `__init__.py` files that export a `root_agent`.

### Direct A2A Client (No LLM Required)

A lightweight client that sends A2A messages directly — no Gemini key or LLM needed. Useful for testing the A2A server in isolation.

```bash
# Install dependencies
pip install httpx
# or: pip install "google-adk[a2a]"  (httpx is included)

# Start the A2A server (from repo root)
dotnet run

# In a second terminal
cd samples/google_adk_client
python direct_client.py
```

### Automated Test Script

A Python script that hits all server endpoints and reports pass/fail:

```bash
# Install dependencies
pip install httpx

# Start the A2A server (from repo root)
dotnet run

# In a second terminal, run the test suite
python samples/test_server.py
```

Options:

| Flag | Description |
|---|---|
| `--url URL` | Base URL of the A2A server (default: `http://localhost:5173`) |
| `--message TEXT` | Custom message to send in the message/send test (default: `Hello!`) |
| `--skip-send` | Skip the message/send test (if no Direct Line credentials are configured) |

Example with a custom message:

```bash
python samples/test_server.py --message "what hours are your branches open?"
```

Example skipping the Direct Line call (tests health + agent card only):

```bash
python samples/test_server.py --skip-send
```

## Security

If you believe you have found a security vulnerability, please see [SECURITY.md](SECURITY.md) for responsible disclosure instructions. **Do not report security vulnerabilities through public GitHub issues.**

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.

## License

This project is licensed under the [MIT License](LICENSE).
