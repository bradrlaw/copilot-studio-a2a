"""
Google ADK client that connects to a Copilot Studio agent via the A2A protocol.

This example uses Google's Agent Development Kit (ADK) to create a local
orchestrator agent that delegates to a remote Copilot Studio agent exposed
through the copilot-studio-a2a server.

Prerequisites:
    pip install "google-adk[a2a]"

Usage:
    1. Start the Copilot Studio A2A server:
       cd ../.. && dotnet run

    2. Set your Gemini API key:
       export GOOGLE_API_KEY=your-gemini-api-key      (macOS/Linux)
       $env:GOOGLE_API_KEY = "your-gemini-api-key"    (PowerShell)

    3. (Optional) If the A2A server has auth enabled, set a bearer token:
       $env:A2A_BEARER_TOKEN = az account get-access-token --resource "api://<client-id>" --query accessToken -o tsv

    4. Run this script:
       python client.py

    5. Or run with ADK's interactive web UI:
       cd ..   # must run from parent directory of agent folder
       adk web .
"""

import asyncio
import os

import httpx
from a2a.client.client import ClientConfig
from a2a.client.client_factory import ClientFactory
from google.adk.agents import Agent
from google.adk.agents.remote_a2a_agent import RemoteA2aAgent
from google.adk.runners import Runner
from google.adk.sessions import InMemorySessionService
from google.genai import types

# URL of the Copilot Studio A2A server's agent card
COPILOT_STUDIO_A2A_URL = "http://localhost:5173/a2a/copilot-studio/v1/card"


def _build_a2a_client_factory() -> ClientFactory | None:
    """Build an A2A client factory with auth headers if a bearer token is set."""
    token = os.environ.get("A2A_BEARER_TOKEN")
    if not token:
        return None

    http_client = httpx.AsyncClient(
        headers={"Authorization": f"Bearer {token}"},
        timeout=600.0,
    )
    return ClientFactory(ClientConfig(httpx_client=http_client))


def build_agent() -> Agent:
    """Build an orchestrator agent that delegates to Copilot Studio via A2A."""

    factory = _build_a2a_client_factory()
    if factory:
        print("[auth] Using bearer token from A2A_BEARER_TOKEN env var")

    copilot_studio_agent = RemoteA2aAgent(
        name="copilot_studio_banking",
        agent_card=COPILOT_STUDIO_A2A_URL,
        description="A virtual banking assistant powered by Copilot Studio. "
        "Handles questions about branch hours, account inquiries, and general banking help.",
        a2a_client_factory=factory,
    )

    root_agent = Agent(
        name="orchestrator",
        model="gemini-2.5-flash",
        instruction="""You are a helpful orchestrator agent. When the user asks about
banking topics (branch hours, accounts, transfers, etc.), delegate to the
copilot_studio_banking agent. For other topics, respond directly.""",
        sub_agents=[copilot_studio_agent],
    )

    return root_agent


root_agent = build_agent()


async def main():
    """Run an interactive conversation with the orchestrator agent."""

    session_service = InMemorySessionService()
    runner = Runner(agent=root_agent, app_name="adk-copilot-studio", session_service=session_service)
    session = await session_service.create_session(app_name="adk-copilot-studio", user_id="user-1")

    print("Google ADK ↔ Copilot Studio A2A Client")
    print(f"Connected to: {COPILOT_STUDIO_A2A_URL}")
    print("Type 'quit' to exit.\n")

    while True:
        user_input = input("You: ").strip()
        if not user_input or user_input.lower() in ("quit", "exit"):
            break

        message = types.Content(
            role="user",
            parts=[types.Part.from_text(text=user_input)],
        )

        print("Agent: ", end="", flush=True)
        async for event in runner.run_async(
            user_id="user-1",
            session_id=session.id,
            new_message=message,
        ):
            if event.content and event.content.parts:
                for part in event.content.parts:
                    if part.text:
                        print(part.text, end="", flush=True)
        print()


if __name__ == "__main__":
    asyncio.run(main())
