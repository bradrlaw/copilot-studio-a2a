# Azure AI Foundry SDK → Copilot Studio A2A Client

A Python sample that uses the **Azure AI Foundry SDK** (`azure-ai-projects`) to programmatically create a Foundry agent with the A2A tool, connect it to the Copilot Studio A2A server, and run an interactive chat.

## Architecture

```
User ──▶ client.py ──Foundry SDK──▶ Foundry Agent (GPT-4.1) ──A2A──▶ Copilot Studio A2A Server ──Direct Line──▶ Copilot Studio Agent
```

The script creates a temporary Foundry agent version with the `A2APreviewTool` attached. The agent uses a GPT model to decide when to delegate to Copilot Studio via the A2A protocol. Responses are streamed back to the terminal.

## Prerequisites

- **Python 3.10+** — [Download](https://www.python.org/downloads/)
- **Azure subscription** with an active [Microsoft Foundry](https://ai.azure.com) project
- **A deployed model** in your Foundry project (e.g., `gpt-4.1-mini`)
- **An A2A connection** configured in the Foundry portal — see the [portal guide](../azure_foundry_portal/README.md) for step-by-step instructions
- **Azure CLI** signed in (`az login`) or another [DefaultAzureCredential](https://learn.microsoft.com/python/api/azure-identity/azure.identity.defaultazurecredential) source
- **The Copilot Studio A2A server running** — see the [main README](../../README.md) for setup

## Setup

1. **Install dependencies:**

   ```bash
   cd samples/azure_foundry_sdk
   pip install -r requirements.txt
   ```

2. **Sign in to Azure** (if not already):

   ```bash
   az login
   ```

3. **Set your Foundry project endpoint** (choose one method):

   **Option A — Environment variable:**

   ```bash
   # macOS / Linux
   export FOUNDRY_PROJECT_ENDPOINT="https://<resource>.ai.azure.com/api/projects/<project>"

   # PowerShell
   $env:FOUNDRY_PROJECT_ENDPOINT = "https://<resource>.ai.azure.com/api/projects/<project>"
   ```

   **Option B — CLI argument:**

   ```bash
   python client.py --endpoint "https://<resource>.ai.azure.com/api/projects/<project>"
   ```

   > **Find your endpoint:** In the Foundry portal, open your project → **Settings** → copy the **Project endpoint** URL.

4. **Ensure the A2A connection exists** in your Foundry project. The default connection name is `copilot-studio-a2a`. If you used a different name, pass it with `--connection`:

   ```bash
   python client.py --connection my-custom-connection-name
   ```

## Running

```bash
cd samples/azure_foundry_sdk
python client.py
```

Example session:

```
Connecting to A2A connection 'copilot-studio-a2a'...
  Connection ID: /subscriptions/.../connections/copilot-studio-a2a
Creating agent 'copilot-studio-bridge' with model 'gpt-4.1-mini'...
  Agent created (version: 1)

============================================================
  Azure AI Foundry ↔ Copilot Studio A2A Client
  Agent: copilot-studio-bridge (version 1)
  Type 'quit' to exit.
============================================================

You: what hours are your branches open?
Agent: Our branches are open Monday–Friday 9:00 AM to 5:00 PM, and Saturday 9:00 AM to 1:00 PM.

You: what is the capital of France?
Agent: The capital of France is Paris.

You: quit
Agent version 1 deleted.
```

Banking questions are delegated to Copilot Studio via the A2A tool; other questions are answered directly by the Foundry agent's GPT model.

## Configuration

All settings can be configured via environment variables or CLI arguments:

| Setting | Env Variable | CLI Argument | Default |
|---|---|---|---|
| Project endpoint | `FOUNDRY_PROJECT_ENDPOINT` | `--endpoint` | *(required)* |
| A2A connection name | `A2A_CONNECTION_NAME` | `--connection` | `copilot-studio-a2a` |
| Model deployment | `FOUNDRY_MODEL` | `--model` | `gpt-4.1-mini` |
| Agent name | `FOUNDRY_AGENT_NAME` | `--agent-name` | `copilot-studio-bridge` |
| Agent instructions | — | `--instructions` | Banking assistant prompt |

### Custom Instructions

To use with a different Copilot Studio agent, pass custom instructions:

```bash
python client.py --instructions "You are a helpful assistant. When the user asks about IT support, use the Copilot Studio agent tool. For all other questions, answer directly."
```

## How It Works

1. The script authenticates to Azure using `DefaultAzureCredential` (Azure CLI, managed identity, etc.)
2. It retrieves the A2A connection by name to get the connection ID
3. It creates a **temporary agent version** with `A2APreviewTool` referencing the connection
4. The interactive loop sends user messages to the Foundry agent via the OpenAI Responses API
5. The Foundry agent decides whether to invoke the A2A tool (delegating to Copilot Studio) or respond directly
6. Responses are **streamed** to the terminal as they arrive
7. On exit, the agent version is **automatically deleted** to clean up resources

## Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `DefaultAzureCredential failed` | Not signed in to Azure | Run `az login` and try again |
| `Connection 'copilot-studio-a2a' not found` | A2A connection doesn't exist in the project | Create it in the Foundry portal — see the [portal guide](../azure_foundry_portal/README.md) |
| `Please set your Foundry project endpoint` | Endpoint not configured | Set `FOUNDRY_PROJECT_ENDPOINT` env var or use `--endpoint` |
| `403 Forbidden` on agent creation | Insufficient permissions | Ensure you have **Azure AI User** role on the Foundry project |
| A2A tool call returns empty response | Copilot Studio server not running | Start the A2A server with `dotnet run` from the repo root |
| `RESOURCE_NOT_FOUND` for model | Model not deployed in project | Deploy the model in the Foundry portal under **Models + endpoints** |

## Related Samples

- **[Azure Foundry Portal Guide](../azure_foundry_portal/)** — No-code setup using the Foundry portal
- **[Google ADK Client](../google_adk_client/)** — Google ADK orchestrator with Gemini
