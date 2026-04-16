# Google ADK → Copilot Studio A2A Client

A sample client that uses **Google's Agent Development Kit (ADK)** to connect to a Copilot Studio agent through the A2A (Agent-to-Agent) protocol.

## Architecture

```
User ──▶ Google ADK Orchestrator ──A2A──▶ Copilot Studio A2A Server ──Direct Line──▶ Copilot Studio Agent
```

The ADK orchestrator agent decides when to delegate to the remote Copilot Studio banking agent based on the user's query.

## Prerequisites

- Python 3.12+
- [Google ADK](https://adk.dev/) with A2A support
- A [Gemini API key](https://aistudio.google.com/apikey) (for the orchestrator's LLM)
- The Copilot Studio A2A server running locally

## Setup

1. **Install dependencies:**

   ```bash
   pip install "google-adk[a2a]"
   ```

2. **Set your Gemini API key:**

   ```bash
   # macOS / Linux
   export GOOGLE_API_KEY=your-gemini-api-key

   # PowerShell
   $env:GOOGLE_API_KEY = "your-gemini-api-key"
   ```

3. **Start the Copilot Studio A2A server** (from the repo root):

   ```bash
   dotnet run
   ```

## Running

### Interactive CLI

```bash
cd samples/google-adk-client
python client.py
```

Example session:

```
Google ADK ↔ Copilot Studio A2A Client
Connected to: http://localhost:5173/a2a/copilot-studio/v1/card
Type 'quit' to exit.

You: what hours are your branches open?
Agent: Our branches are open Monday–Friday 9:00 AM to 5:00 PM, and Saturday 9:00 AM to 1:00 PM.
```

### ADK Web UI

ADK includes an interactive web UI for testing agents:

```bash
cd samples/google-adk-client
adk web .
```

This opens a browser-based chat interface where you can interact with the orchestrator and see it delegate to Copilot Studio.

## How It Works

1. **`RemoteA2aAgent`** connects to the Copilot Studio A2A server by fetching its agent card from `/a2a/copilot-studio/v1/card`
2. The **orchestrator agent** (powered by Gemini) receives user messages and decides whether to handle them directly or delegate to the banking sub-agent
3. When delegated, ADK sends an A2A `message/send` JSON-RPC request to the Copilot Studio A2A server
4. The server proxies the request to Copilot Studio via Direct Line and returns the response

## Customization

- **Change the A2A server URL** — update `COPILOT_STUDIO_A2A_URL` in `client.py`
- **Change the LLM model** — update `model="gemini-2.0-flash"` to another Gemini model
- **Add more sub-agents** — add additional `RemoteA2aAgent` instances to the `sub_agents` list
