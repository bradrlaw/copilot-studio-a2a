# Authentication Guide

This guide explains how to configure authenticated access to the Copilot Studio A2A bridge server. When enabled, callers must present a valid Microsoft Entra ID (Azure AD) bearer token to invoke the A2A endpoints.

## Overview

By default, authentication is **disabled** — the server accepts anonymous A2A requests (same behavior as before this feature was added). When you set `EnableAuthPassthrough` to `true`, the server:

1. **Validates** incoming bearer tokens against your Entra ID tenant
2. **Derives** an opaque, deterministic user ID from the validated identity claims
3. **Passes** that user ID to Copilot Studio via Direct Line so the agent can personalize responses per-user

```
Caller (A2A Client)
  │
  │  Authorization: Bearer <Entra ID token>
  ▼
┌─────────────────────────────┐
│  Copilot Studio A2A Server  │
│                             │
│  1. Validate JWT (Entra ID) │
│  2. Extract oid / sub claim │
│  3. SHA256 → opaque user ID │
│  4. Pass user ID to DL      │
└──────────────┬──────────────┘
               │
               │  Direct Line API
               │  (user.id = "a2a-<hash>")
               ▼
       ┌───────────────┐
       │ Copilot Studio │
       │    Agent       │
       └───────────────┘
```

## Prerequisites

- A **Microsoft Entra ID** (Azure AD) tenant
- An **App Registration** in that tenant for the A2A server
- Callers must be able to obtain tokens for your app's audience

## Step 1: Create an App Registration

1. Go to the [Azure Portal → App Registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **New registration**
3. Set:
   - **Name**: `Copilot Studio A2A Server` (or your choice)
   - **Supported account types**: Choose based on your needs:
     - *Single tenant* — only users/apps in your tenant
     - *Multitenant* — users/apps from any Entra ID tenant
   - **Redirect URI**: Leave blank (this is a server-side API, not an interactive app)
4. Click **Register**
5. Note the **Application (client) ID** and **Directory (tenant) ID**

### Expose an API (Optional but Recommended)

1. In your app registration, go to **Expose an API**
2. Set the **Application ID URI** (e.g., `api://<client-id>`)
3. Add a scope (e.g., `api://<client-id>/A2A.Invoke`)
4. Grant client applications access to this scope

This lets you control which applications can call your A2A server.

## Step 2: Configure the Server

Add the following to your `appsettings.json` (or use environment variables / user secrets):

```json
{
  "CopilotStudio": {
    "DirectLineSecret": "<your-secret>",
    "EnableAuthPassthrough": true,
    "AzureAd": {
      "Instance": "https://login.microsoftonline.com/",
      "TenantId": "<your-tenant-id>",
      "ClientId": "<your-client-id>"
    }
  }
}
```

### Configuration Reference

| Setting | Description | Default |
|---------|-------------|---------|
| `EnableAuthPassthrough` | Enable/disable JWT validation on A2A endpoints | `false` |
| `AzureAd:Instance` | Entra ID authority base URL | `https://login.microsoftonline.com/` |
| `AzureAd:TenantId` | Your Entra ID tenant ID (GUID) | *(required when auth enabled)* |
| `AzureAd:ClientId` | App Registration client ID (GUID) | *(required when auth enabled)* |

### Using Environment Variables

For production deployments, use environment variables instead of `appsettings.json`:

```bash
CopilotStudio__EnableAuthPassthrough=true
CopilotStudio__AzureAd__Instance=https://login.microsoftonline.com/
CopilotStudio__AzureAd__TenantId=<your-tenant-id>
CopilotStudio__AzureAd__ClientId=<your-client-id>
```

### Using User Secrets (Development)

```bash
dotnet user-secrets set "CopilotStudio:EnableAuthPassthrough" "true"
dotnet user-secrets set "CopilotStudio:AzureAd:TenantId" "<your-tenant-id>"
dotnet user-secrets set "CopilotStudio:AzureAd:ClientId" "<your-client-id>"
```

## Step 3: Obtain a Token (Client Side)

Callers must acquire a token from your Entra ID tenant with the correct audience. Example using the Azure CLI:

```bash
# Interactive login
az login

# Get a token for your app's audience
az account get-access-token --resource api://<client-id> --query accessToken -o tsv
```

Example using `curl` with client credentials (app-to-app):

```bash
# Get token using client credentials flow
curl -X POST "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token" \
  -d "client_id=<caller-client-id>" \
  -d "client_secret=<caller-secret>" \
  -d "scope=api://<server-client-id>/.default" \
  -d "grant_type=client_credentials"
```

Then include it in A2A requests:

```bash
curl -X POST http://localhost:5173/a2a/copilot-studio \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <token>" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "message/send",
    "params": {
      "message": {
        "kind": "message",
        "messageId": "msg-001",
        "role": "user",
        "parts": [{ "kind": "text", "text": "Hello" }]
      }
    }
  }'
```

## How User Identity Flows

When authentication is enabled:

1. The server validates the bearer token using Microsoft Identity Web
2. It extracts the `oid` (object ID) claim — or falls back to `sub` (subject)
3. It combines the tenant ID + object ID and hashes them with SHA-256
4. The resulting hash is used as the Direct Line `user.id` in the format `a2a-<hash>`

This ensures:
- **Privacy**: Raw identity claims are never sent to Copilot Studio
- **Determinism**: The same user always gets the same Direct Line user ID
- **Isolation**: Different users get separate conversation contexts in Copilot Studio

When authentication is **disabled**, the server uses a static user ID (`a2a-anonymous`), so all callers share the same identity context.

## Behavior When Disabled

With `EnableAuthPassthrough` set to `false` (the default):

- No authentication middleware is registered
- All A2A endpoints are publicly accessible
- A static user ID (`a2a-anonymous`) is used for Direct Line
- This is suitable for development, testing, and internal network deployments

## Troubleshooting

### 401 Unauthorized

- Verify the token was issued by the correct tenant
- Check that the token's `aud` (audience) claim matches your `ClientId`
- Ensure the token hasn't expired
- Confirm `EnableAuthPassthrough` is set to `true` in your config

### 403 Forbidden

- The token is valid but the user/app doesn't meet the authorization policy
- Check that the caller's app registration has been granted access to your API's scope

### Token works but Copilot Studio doesn't personalize responses

- This is expected in Phase 1 — the user ID is passed to Direct Line but SSO token exchange is not yet implemented
- Copilot Studio sees a unique user ID but doesn't have access to the user's actual identity/permissions
- Phase 2 (SSO token exchange) will address this in a future update

## Roadmap: Phase 2 — SSO Token Exchange

> **Status**: Planned (not yet implemented)

Phase 2 will add SSO token exchange, allowing the server to forward the caller's identity to Copilot Studio so the agent can access user-specific resources:

1. Server receives a validated bearer token
2. Performs an On-Behalf-Of (OBO) flow to get a token for Copilot Studio
3. Uses Direct Line's `signin/tokenExchange` activity to pass the token
4. Copilot Studio agent can then call downstream APIs as the authenticated user

This requires:
- Copilot Studio agent configured with SSO authentication
- OBO flow permissions configured in Entra ID
- Additional configuration in the server (client secret, downstream scopes)

See [TODO.md](../TODO.md) for the full roadmap.
