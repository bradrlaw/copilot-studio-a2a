# Google ADK → Copilot Studio A2A Client

A sample client that uses **Google's Agent Development Kit (ADK)** to connect to a Copilot Studio agent through the A2A (Agent-to-Agent) protocol.

## Architecture

```
User ──▶ Google ADK Orchestrator (Gemini) ──A2A──▶ Copilot Studio A2A Server ──Direct Line──▶ Copilot Studio Agent
```

The ADK orchestrator agent (powered by Gemini) decides when to delegate to the remote Copilot Studio banking agent based on the user's query. Non-banking questions are handled directly by Gemini.

## Prerequisites

- **Python 3.12+** — [Download](https://www.python.org/downloads/)
- **A Google Cloud project** with the [Generative Language API](https://console.cloud.google.com/apis/library/generativelanguage.googleapis.com) enabled
- **A Gemini API key** — [Get one at AI Studio](https://aistudio.google.com/apikey)
- **Billing enabled** on your Google Cloud project — the free tier has strict quota limits; a billing account with credits is recommended
- **The Copilot Studio A2A server running** — see the [main README](../../README.md) for setup

> **Note:** If you don't have a Gemini API key or want to test without an LLM, use `direct_client.py` instead (see below).

## Setup

1. **Install dependencies:**

   ```bash
   pip install "google-adk[a2a]"
   ```

2. **Set your Gemini API key** (choose one method):

   **Option A — Environment variable (per session):**

   ```bash
   # macOS / Linux
   export GOOGLE_API_KEY=your-gemini-api-key

   # PowerShell
   $env:GOOGLE_API_KEY = "your-gemini-api-key"
   ```

   **Option B — `.env` file (persistent, gitignored):**

   Create a `.env` file in this directory:

   ```
   GOOGLE_API_KEY=your-gemini-api-key
   ```

   The `.env` file is listed in `.gitignore` and will not be committed.

3. **Start the Copilot Studio A2A server** (from the repo root):

   ```bash
   dotnet run
   ```

## Running the ADK Orchestrator Client

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

You: what is the capital of France?
Agent: The capital of France is Paris.
```

Banking questions are delegated to Copilot Studio; other questions are answered directly by Gemini.

### ADK Web UI

ADK includes an interactive web UI for testing agents:

```bash
cd samples/google-adk-client
adk web .
```

This opens a browser-based chat interface where you can interact with the orchestrator and see it delegate to Copilot Studio.

## Running the Direct Client (No LLM Required)

If you don't have a Gemini API key, or want to test the A2A server in isolation without an LLM orchestrator:

```bash
cd samples/google-adk-client
python direct_client.py
```

This sends messages directly to the Copilot Studio A2A server using `httpx`. No Gemini key needed.

**Dependencies:** `pip install httpx` (or already installed via `google-adk[a2a]`)

Example session:

```
Fetching agent card...
Connected to: Copilot Studio Agent
Description:  An A2A-compatible agent backed by Microsoft Copilot Studio.
Protocol:     0.3.0
--------------------------------------------------
Type 'quit' to exit.

You: what hours are your branches open?
Agent: Our branches are open Monday–Friday from 9:00 AM to 5:00 PM, and Saturday from 9:00 AM to 1:00 PM.
```

## How It Works

1. **`RemoteA2aAgent`** connects to the Copilot Studio A2A server by fetching its agent card from `/a2a/copilot-studio/v1/card`
2. The **orchestrator agent** (powered by Gemini) receives user messages and decides whether to handle them directly or delegate to the banking sub-agent
3. When delegated, ADK sends an A2A `message/send` JSON-RPC request to the Copilot Studio A2A server
4. The server proxies the request to Copilot Studio via Direct Line and returns the response

## Customization

- **Change the A2A server URL** — update `COPILOT_STUDIO_A2A_URL` in `client.py` (or `A2A_BASE_URL` in `direct_client.py`)
- **Change the LLM model** — update `model="gemini-2.5-flash"` in `client.py` to another [supported Gemini model](https://ai.google.dev/gemini-api/docs/models)
- **Add more sub-agents** — add additional `RemoteA2aAgent` instances to the `sub_agents` list in `client.py`

## Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `429 RESOURCE_EXHAUSTED` | Gemini API quota exceeded or no billing | Enable billing on your Google Cloud project and add credits at [AI Studio](https://ai.studio/projects) |
| `404 NOT_FOUND` for model | Model name deprecated or invalid | Update the `model` parameter in `client.py` to a current model (e.g., `gemini-2.5-flash`) |
| `A2A request failed: HTTP Error 503` | A2A server not running or wrong URL | Ensure the server is running on `http://localhost:5173` and the agent card URL in `client.py` matches |
| `Failed to generate Direct Line token` | Missing Copilot Studio credentials | Configure Direct Line secret — see the [main README](../../README.md#configuration) |
