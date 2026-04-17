"""
Azure AI Foundry SDK → Copilot Studio A2A Client

Creates a Foundry agent with the A2A tool connected to the Copilot Studio
A2A server, then runs an interactive chat loop with streaming responses.

Prerequisites:
  - An A2A connection named in A2A_CONNECTION_NAME created in the Foundry portal
  - Azure CLI signed in (`az login`) or other DefaultAzureCredential source
  - pip install -r requirements.txt

Usage:
  python client.py
  python client.py --endpoint "https://..." --connection my-conn --model gpt-4.1-mini
"""

import argparse
import os
import sys

from azure.identity import DefaultAzureCredential
from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import PromptAgentDefinition, A2APreviewTool


# ---------------------------------------------------------------------------
# Defaults (override via CLI args or environment variables)
# ---------------------------------------------------------------------------
DEFAULT_PROJECT_ENDPOINT = os.environ.get(
    "FOUNDRY_PROJECT_ENDPOINT",
    "https://<resource>.ai.azure.com/api/projects/<project>",
)
DEFAULT_A2A_CONNECTION = os.environ.get("A2A_CONNECTION_NAME", "copilot-studio-a2a")
DEFAULT_MODEL = os.environ.get("FOUNDRY_MODEL", "gpt-4.1-mini")
DEFAULT_AGENT_NAME = os.environ.get("FOUNDRY_AGENT_NAME", "copilot-studio-bridge")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Azure AI Foundry SDK client for Copilot Studio A2A"
    )
    parser.add_argument(
        "--endpoint",
        default=DEFAULT_PROJECT_ENDPOINT,
        help="Foundry project endpoint URL",
    )
    parser.add_argument(
        "--connection",
        default=DEFAULT_A2A_CONNECTION,
        help="Name of the A2A connection in Foundry (default: copilot-studio-a2a)",
    )
    parser.add_argument(
        "--model",
        default=DEFAULT_MODEL,
        help="Model deployment name (default: gpt-4.1-mini)",
    )
    parser.add_argument(
        "--agent-name",
        default=DEFAULT_AGENT_NAME,
        help="Foundry agent name to create (default: copilot-studio-bridge)",
    )
    parser.add_argument(
        "--instructions",
        default=(
            "You are a helpful assistant. When the user asks about banking, "
            "branch hours, accounts, transfers, or financial services, use the "
            "Copilot Studio agent tool to get the answer. For all other "
            "questions, answer directly."
        ),
        help="System instructions for the Foundry agent",
    )
    return parser.parse_args()


def create_agent(
    project: AIProjectClient,
    connection_name: str,
    agent_name: str,
    model: str,
    instructions: str,
):
    """Create a Foundry agent version with the A2A tool attached."""
    print(f"Connecting to A2A connection '{connection_name}'...")
    a2a_connection = project.connections.get(connection_name)
    print(f"  Connection ID: {a2a_connection.id}")

    tool = A2APreviewTool(project_connection_id=a2a_connection.id)

    print(f"Creating agent '{agent_name}' with model '{model}'...")
    agent = project.agents.create_version(
        agent_name=agent_name,
        definition=PromptAgentDefinition(
            model=model,
            instructions=instructions,
            tools=[tool],
        ),
    )
    print(f"  Agent created (version: {agent.version})")
    return agent


def chat_loop(project: AIProjectClient, agent):
    """Run an interactive chat loop with streaming responses."""
    openai_client = project.get_openai_client()

    print("\n" + "=" * 60)
    print(f"  Azure AI Foundry ↔ Copilot Studio A2A Client")
    print(f"  Agent: {agent.name} (version {agent.version})")
    print(f"  Type 'quit' to exit.")
    print("=" * 60 + "\n")

    while True:
        try:
            user_input = input("You: ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\nExiting...")
            break

        if not user_input:
            continue
        if user_input.lower() in ("quit", "exit", "q"):
            break

        try:
            response_text = ""
            stream = openai_client.responses.create(
                stream=True,
                tool_choice="auto",
                input=user_input,
                extra_body={
                    "agent_reference": {
                        "name": agent.name,
                        "type": "agent_reference",
                    }
                },
            )

            sys.stdout.write("Agent: ")
            sys.stdout.flush()

            for event in stream:
                if event.type == "response.output_text.delta":
                    sys.stdout.write(event.delta)
                    sys.stdout.flush()
                    response_text += event.delta
                elif event.type == "response.completed":
                    pass  # handled by final newline below

            print()  # newline after streaming

            if not response_text:
                print("  (No text response received)")

        except Exception as e:
            print(f"\nError: {e}")
            print("  The agent may need to be re-created if this persists.\n")


def cleanup_agent(project: AIProjectClient, agent):
    """Delete the agent version to clean up resources."""
    try:
        project.agents.delete_version(
            agent_name=agent.name, agent_version=agent.version
        )
        print(f"Agent version {agent.version} deleted.")
    except Exception as e:
        print(f"Warning: Failed to delete agent version: {e}")


def main():
    args = parse_args()

    if "<resource>" in args.endpoint or "<project>" in args.endpoint:
        print("Error: Please set your Foundry project endpoint.")
        print("  Option 1: export FOUNDRY_PROJECT_ENDPOINT='https://...'")
        print("  Option 2: python client.py --endpoint 'https://...'")
        sys.exit(1)

    project = AIProjectClient(
        endpoint=args.endpoint,
        credential=DefaultAzureCredential(),
    )

    agent = None
    try:
        agent = create_agent(
            project=project,
            connection_name=args.connection,
            agent_name=args.agent_name,
            model=args.model,
            instructions=args.instructions,
        )
        chat_loop(project, agent)
    except KeyboardInterrupt:
        print("\nInterrupted.")
    except Exception as e:
        print(f"\nFatal error: {e}")
        sys.exit(1)
    finally:
        if agent:
            cleanup_agent(project, agent)


if __name__ == "__main__":
    main()
