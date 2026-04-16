# Copilot Studio A2A Server

Exposes a Microsoft Copilot Studio agent as an [A2A (Agent-to-Agent) protocol](https://github.com/a2aproject/A2A) server using the Microsoft Agent Framework.

## Architecture

```
A2A Client ──JSON-RPC──▶ This Server ──Direct Line API──▶ Copilot Studio Agent
```

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A published Copilot Studio agent with the **Direct Line** channel enabled

## Configuration

### Option A: Direct Line Secret

1. In Copilot Studio → **Settings → Channels → Direct Line**
2. Copy the **Secret key**
3. Set it:

```bash
dotnet user-secrets init
dotnet user-secrets set "CopilotStudio:DirectLineSecret" "YOUR_SECRET_HERE"
```

### Option B: Token Endpoint (regional)

```bash
dotnet user-secrets set "CopilotStudio:TokenEndpoint" "https://defaultXXXXXX.XX.environment.api.powerplatform.com/powervirtualagents/botsbyschema/{bot-schema}/directline/token?api-version=2022-03-01-preview"
```

### A2A Agent Card

Customize in `appsettings.json`:

```json
{
  "A2A": {
    "AgentName": "My Copilot Studio Agent",
    "AgentDescription": "Handles customer support inquiries.",
    "AgentUrl": "https://your-deployed-url.com/a2a/copilot-studio"
  }
}
```

## Running

```bash
dotnet restore
dotnet run
```

Starts on `http://localhost:5000`. Key endpoints:

| Endpoint | Description |
|---|---|
| `GET /health` | Health check |
| `GET /a2a/copilot-studio/v1/card` | A2A Agent Card (discovery) |
| `POST /a2a/copilot-studio` | A2A JSON-RPC task endpoint |
| `/swagger` | Swagger UI |

## Connecting from Copilot Studio

1. Expose publicly (Azure App Service, Dev Tunnels, ngrok)
2. In Copilot Studio → **Add agent → Connect to external agent → Agent2Agent**
3. Paste URL: `https://your-url/a2a/copilot-studio`

## Testing with curl

```bash
# Agent card
curl http://localhost:5000/a2a/copilot-studio/v1/card

# Send a task
curl -X POST http://localhost:5000/a2a/copilot-studio \
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
