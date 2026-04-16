"""
End-to-end test script for the Copilot Studio A2A server.

Hits all public endpoints and reports pass/fail for each.
The A2A server must be running before executing this script.

Prerequisites:
    pip install httpx

Usage:
    1. Start the A2A server:
       cd <repo-root> && dotnet run

    2. Run the tests:
       python samples/test_server.py

    Optionally pass a custom base URL:
       python samples/test_server.py --url http://localhost:5173
"""

import argparse
import json
import sys
import uuid

import httpx

DEFAULT_BASE_URL = "http://localhost:5173"

PASS = "\033[92m✓ PASS\033[0m"
FAIL = "\033[91m✗ FAIL\033[0m"


def test_health(client: httpx.Client, base: str) -> bool:
    """GET /health — expects 200 with status: healthy."""
    url = f"{base}/health"
    print(f"\n{'='*60}")
    print(f"TEST: Health Check  (GET {url})")
    print(f"{'='*60}")
    try:
        r = client.get(url)
        data = r.json()
        ok = r.status_code == 200 and data.get("status") == "healthy"
        print(f"  Status: {r.status_code}")
        print(f"  Body:   {json.dumps(data)}")
        print(f"  Result: {PASS if ok else FAIL}")
        return ok
    except Exception as e:
        print(f"  Error:  {e}")
        print(f"  Result: {FAIL}")
        return False


def test_agent_card(client: httpx.Client, base: str) -> bool:
    """GET /a2a/copilot-studio/v1/card — expects 200 with required fields."""
    url = f"{base}/a2a/copilot-studio/v1/card"
    print(f"\n{'='*60}")
    print(f"TEST: Agent Card  (GET {url})")
    print(f"{'='*60}")
    try:
        r = client.get(url)
        data = r.json()
        required = ["name", "description", "url", "protocolVersion", "capabilities"]
        missing = [f for f in required if f not in data]
        ok = r.status_code == 200 and not missing
        print(f"  Status: {r.status_code}")
        print(f"  Name:   {data.get('name')}")
        print(f"  URL:    {data.get('url')}")
        print(f"  Proto:  {data.get('protocolVersion')}")
        if missing:
            print(f"  Missing fields: {missing}")
        print(f"  Result: {PASS if ok else FAIL}")
        return ok
    except Exception as e:
        print(f"  Error:  {e}")
        print(f"  Result: {FAIL}")
        return False


def test_message_send(client: httpx.Client, base: str, message: str) -> bool:
    """POST /a2a/copilot-studio — sends an A2A message/send and expects a reply."""
    url = f"{base}/a2a/copilot-studio"
    print(f"\n{'='*60}")
    print(f"TEST: Message Send  (POST {url})")
    print(f"  Sending: \"{message}\"")
    print(f"{'='*60}")
    payload = {
        "jsonrpc": "2.0",
        "id": str(uuid.uuid4()),
        "method": "message/send",
        "params": {
            "message": {
                "kind": "message",
                "messageId": str(uuid.uuid4()),
                "role": "user",
                "parts": [{"kind": "text", "text": message}],
            }
        },
    }
    try:
        r = client.post(url, json=payload, headers={"Content-Type": "application/json"})
        data = r.json()

        if "error" in data:
            print(f"  Status:  {r.status_code}")
            print(f"  Error:   {data['error'].get('message', data['error'])}")
            print(f"  Result:  {FAIL}")
            return False

        result = data.get("result", {})
        parts = result.get("parts", [])
        text = "\n".join(p.get("text", "") for p in parts if p.get("text"))
        ok = r.status_code == 200 and bool(text)
        print(f"  Status:  {r.status_code}")
        print(f"  Role:    {result.get('role')}")
        print(f"  Reply:   {text[:200]}{'...' if len(text) > 200 else ''}")
        print(f"  Result:  {PASS if ok else FAIL}")
        return ok
    except Exception as e:
        print(f"  Error:  {e}")
        print(f"  Result: {FAIL}")
        return False


def test_invalid_method(client: httpx.Client, base: str) -> bool:
    """POST with an invalid JSON-RPC method — expects an error response (not a crash)."""
    url = f"{base}/a2a/copilot-studio"
    print(f"\n{'='*60}")
    print(f"TEST: Invalid Method  (POST {url})")
    print(f"{'='*60}")
    payload = {
        "jsonrpc": "2.0",
        "id": "bad-1",
        "method": "nonexistent/method",
        "params": {},
    }
    try:
        r = client.post(url, json=payload, headers={"Content-Type": "application/json"})
        # We expect either a JSON-RPC error response or a non-200 — either is acceptable
        ok = r.status_code in (200, 400, 404, 405)
        print(f"  Status:  {r.status_code}")
        try:
            data = r.json()
            if "error" in data:
                print(f"  Error:   {data['error'].get('message', data['error'])}")
            print(f"  Body:    {json.dumps(data)[:200]}")
        except Exception:
            print(f"  Body:    {r.text[:200]}")
        print(f"  Result:  {PASS if ok else FAIL} (server handled gracefully)")
        return ok
    except Exception as e:
        print(f"  Error:  {e}")
        print(f"  Result: {FAIL}")
        return False


def main():
    parser = argparse.ArgumentParser(description="Test the Copilot Studio A2A server")
    parser.add_argument(
        "--url",
        default=DEFAULT_BASE_URL,
        help=f"Base URL of the A2A server (default: {DEFAULT_BASE_URL})",
    )
    parser.add_argument(
        "--message",
        default="Hello!",
        help="Message to send in the message/send test (default: 'Hello!')",
    )
    parser.add_argument(
        "--skip-send",
        action="store_true",
        help="Skip the message/send test (useful if no Direct Line credentials are configured)",
    )
    args = parser.parse_args()

    print(f"Copilot Studio A2A Server — Test Suite")
    print(f"Target: {args.url}")

    results = []
    with httpx.Client(timeout=120.0) as client:
        # Tests that don't require credentials
        results.append(("Health Check", test_health(client, args.url)))
        results.append(("Agent Card", test_agent_card(client, args.url)))
        results.append(("Invalid Method", test_invalid_method(client, args.url)))

        # Test that requires Direct Line credentials
        if args.skip_send:
            print(f"\n{'='*60}")
            print("SKIPPED: Message Send (--skip-send)")
            print(f"{'='*60}")
        else:
            results.append(("Message Send", test_message_send(client, args.url, args.message)))

    # Summary
    passed = sum(1 for _, ok in results if ok)
    total = len(results)
    print(f"\n{'='*60}")
    print(f"RESULTS: {passed}/{total} passed")
    print(f"{'='*60}")
    for name, ok in results:
        print(f"  {PASS if ok else FAIL}  {name}")

    sys.exit(0 if passed == total else 1)


if __name__ == "__main__":
    main()
