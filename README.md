# Copilot Studio A2A Server

Exposes a Microsoft Copilot Studio agent as an [A2A (Agent-to-Agent) protocol](https://github.com/a2aproject/A2A) server using the [Microsoft Agent Framework](https://github.com/microsoft/Agents-for-net).

## How It Works

This application acts as a bridge between the A2A protocol and Microsoft Copilot Studio. A2A clients communicate with this server using JSON-RPC 2.0, and the server translates those requests into Bot Framework Direct Line API calls to reach the Copilot Studio agent.

```
A2A Client ──JSON-RPC 2.0──▶ This Server ──Direct Line API──▶ Copilot Studio Agent
```

### Request Flow

1. An A2A client sends a JSON-RPC 2.0 `tasks/send` request to `/a2a/copilot-studio`
2. **JsonRpcMiddleware** unwraps the JSON-RPC envelope, passing just the `params` payload to the inner pipeline
3. The Microsoft Agent Framework routes the request to **CopilotStudioChatClient**, which:
   - Exchanges credentials for a **Direct Line token** (via secret or regional token endpoint)
   - Opens a new Direct Line **conversation**
   - Sends the user's message as a Direct Line **activity**
   - **Polls** for the bot's reply (configurable timeout and interval)
4. The middleware re-wraps the response into a JSON-RPC 2.0 envelope and returns it to the client

### Agent Discovery

Other A2A agents discover this one by calling `GET /a2a/copilot-studio/v1/card`, which returns the agent card containing the agent's name, description, URL, and capabilities.

## Project Structure

```
├── Program.cs                          # App startup, DI, endpoint mapping, agent card config
├── Middleware/
│   └── JsonRpcMiddleware.cs            # Unwraps/wraps JSON-RPC 2.0 envelopes for A2A compatibility
├── Services/
│   ├── CopilotStudioChatClient.cs      # IChatClient implementation proxying to Direct Line API
│   └── CopilotStudioOptions.cs         # Strongly-typed configuration (bound to appsettings)
├── appsettings.json                    # Default configuration (endpoints, polling, agent card)
├── appsettings.Development.json        # Development overrides
└── CopilotStudioA2A.csproj             # .NET 10 project file and NuGet dependencies
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

## Testing with curl

```bash
# Fetch the agent card
curl http://localhost:5173/a2a/copilot-studio/v1/card

# Send a task
curl -X POST http://localhost:5173/a2a/copilot-studio \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": "1",
    "method": "tasks/send",
    "params": {
      "id": "task-001",
      "message": {
        "role": "user",
        "parts": [{ "type": "text", "text": "Hello!" }]
      }
    }
  }'
```

## Known Limitations

- The A2A NuGet package (`Microsoft.Agents.AI.Hosting.A2A.AspNetCore`) is a **preview** release — APIs may change in future versions
- Direct Line does not support true streaming, so the `Streaming` capability is set to `false` in the agent card
- Each A2A request opens a new Direct Line conversation — there is no conversation persistence across requests
