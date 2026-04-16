"""
Direct A2A client that sends messages to a Copilot Studio agent
without requiring a Gemini API key or any LLM orchestrator.

Uses the a2a-sdk to communicate directly via JSON-RPC 2.0.

Prerequisites:
    pip install "google-adk[a2a]"   (installs a2a-sdk as a dependency)
    # or: pip install a2a-sdk

Usage:
    1. Start the Copilot Studio A2A server:
       cd ../.. && dotnet run

    2. Run this script:
       python direct_client.py
"""

import asyncio
import uuid

import httpx

A2A_BASE_URL = "http://localhost:5173/a2a/copilot-studio"
A2A_CARD_URL = f"{A2A_BASE_URL}/v1/card"


async def get_agent_card(client: httpx.AsyncClient) -> dict:
    """Fetch the agent card for discovery."""
    response = await client.get(A2A_CARD_URL)
    response.raise_for_status()
    return response.json()


async def send_message(client: httpx.AsyncClient, text: str) -> dict:
    """Send an A2A message/send request and return the result."""
    payload = {
        "jsonrpc": "2.0",
        "id": str(uuid.uuid4()),
        "method": "message/send",
        "params": {
            "message": {
                "kind": "message",
                "messageId": str(uuid.uuid4()),
                "role": "user",
                "parts": [{"kind": "text", "text": text}],
            }
        },
    }
    response = await client.post(
        A2A_BASE_URL,
        json=payload,
        headers={"Content-Type": "application/json"},
        timeout=120.0,
    )
    response.raise_for_status()
    return response.json()


async def main():
    async with httpx.AsyncClient() as client:
        # Discover the agent
        print("Fetching agent card...")
        card = await get_agent_card(client)
        print(f"Connected to: {card['name']}")
        print(f"Description:  {card['description']}")
        print(f"Protocol:     {card.get('protocolVersion', 'unknown')}")
        print("-" * 50)
        print("Type 'quit' to exit.\n")

        while True:
            user_input = input("You: ").strip()
            if not user_input or user_input.lower() in ("quit", "exit"):
                break

            result = await send_message(client, user_input)

            if "result" in result:
                parts = result["result"].get("parts", [])
                agent_text = "\n".join(p["text"] for p in parts if p.get("text"))
                print(f"Agent: {agent_text}\n")
            elif "error" in result:
                print(f"Error: {result['error']['message']}\n")


if __name__ == "__main__":
    asyncio.run(main())
