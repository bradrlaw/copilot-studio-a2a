# Authentication Guide

This guide explains how to configure authenticated access to the Copilot Studio A2A bridge server. Authentication is modular — you can enable just Phase 1 (endpoint protection) or go further with Phase 2 (Copilot Studio SSO integration).

| Phase | What It Does | Copilot Studio Auth Required? |
|-------|-------------|-------------------------------|
| **Phase 1** | Validates caller tokens, blocks unauthenticated requests, passes opaque user ID to Direct Line | No — agent can use "No authentication" |
| **Phase 2** | Everything in Phase 1 + configures Copilot Studio to recognize authenticated users via SSO | Yes — agent must use "Entra ID v2 with client secrets" |
| **SDK Mode** | OBO token exchange for API auth + automatic SSO via SDK | Yes — agent must use "Entra ID v2 with client secrets" |

## Overview

```
Caller (A2A Client)
  │
  │  Authorization: Bearer <Entra ID token>
  ▼
┌─────────────────────────────────┐
│   Copilot Studio A2A Server     │
│                                 │
│  1. Validate JWT (Entra ID)     │
│  2. Extract oid + tid claims    │
│  3. SHA256 → opaque user ID     │
│  4. Pass user ID to Direct Line │
└────────────────┬────────────────┘
                 │
                 │  Direct Line API
                 │  (user.id = "dl_<hash>")
                 ▼
         ┌───────────────┐
         │ Copilot Studio │
         │    Agent       │
         └───────────────┘
```

## Prerequisites

- A **Microsoft Entra ID** (Azure AD) tenant with admin access
- An [Azure Portal](https://portal.azure.com) account
- A published **Copilot Studio agent** with the **Direct Line** channel enabled
- The Direct Line **secret** (see main [README](../README.md) for how to obtain it)

---

## Phase 1: A2A Endpoint Protection

Phase 1 adds JWT bearer token validation to the A2A endpoints. Unauthenticated requests are rejected with `401 Unauthorized`. Each authenticated user gets a unique, deterministic Direct Line user ID derived from their Entra ID claims.

> **Copilot Studio auth is NOT required for Phase 1.** Your agent can remain set to "No authentication". Phase 1 only protects the A2A server boundary.

### Step 1.1: Create an App Registration

1. Go to [Azure Portal → App Registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **New registration**
3. Configure:
   - **Name**: `Copilot Studio A2A Server` (or your choice)
   - **Supported account types**: Choose based on your needs:
     - *Single tenant* — only users/apps in your Entra ID tenant
     - *Multitenant* — users/apps from any Entra ID tenant
   - **Redirect URI**: Leave blank for now (we'll add one in Phase 2 if needed)
4. Click **Register**
5. Note the **Application (client) ID** and **Directory (tenant) ID** — you'll need both

### Step 1.2: Expose an API Scope

This defines the audience that callers must request when acquiring tokens.

1. In your app registration, go to **Expose an API**
2. Click **Set** next to "Application ID URI" and accept the default (`api://<client-id>`) or customize it
3. Click **Add a scope**:
   - **Scope name**: `access_as_user` (or any name you prefer)
   - **Who can consent**: Admins and users
   - **Admin consent display name**: `Access Copilot Studio A2A Server`
   - **Admin consent description**: `Allows the app to call the Copilot Studio A2A server on behalf of the signed-in user`
   - **State**: Enabled
4. Click **Add scope**

> **Important**: The Application ID URI (e.g., `api://<client-id>`) becomes the token audience. Callers must request this as their `resource` or `scope` when acquiring tokens.

### Step 1.3: Configure the A2A Server

Store configuration securely using [user-secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) for local development:

```bash
dotnet user-secrets set "CopilotStudio:EnableAuthPassthrough" "true"
dotnet user-secrets set "CopilotStudio:AzureAd:TenantId" "<your-tenant-id>"
dotnet user-secrets set "CopilotStudio:AzureAd:ClientId" "<your-client-id>"
```

For production deployments, use environment variables:

```bash
CopilotStudio__EnableAuthPassthrough=true
CopilotStudio__AzureAd__TenantId=<your-tenant-id>
CopilotStudio__AzureAd__ClientId=<your-client-id>
```

### Step 1.4: Test Phase 1

1. Start the server:
   ```bash
   dotnet run
   ```

2. Verify unauthenticated requests are blocked:
   ```bash
   curl -s -o /dev/null -w "%{http_code}" -X POST http://localhost:5173/a2a/copilot-studio \
     -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","messageId":"m1","role":"user","parts":[{"kind":"text","text":"Hello"}]}}}'
   # Expected: 401
   ```

3. Get a token and verify authenticated requests work:
   ```bash
   # Sign in (one-time)
   az login

   # Get a token for your app
   TOKEN=$(az account get-access-token --resource "api://<client-id>" --query accessToken -o tsv)

   # Send an authenticated request
   curl -X POST http://localhost:5173/a2a/copilot-studio \
     -H "Content-Type: application/json" \
     -H "Authorization: Bearer $TOKEN" \
     -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","messageId":"m1","role":"user","parts":[{"kind":"text","text":"Hello"}]}}}'
   # Expected: 200 with bot response
   ```

   **PowerShell:**
   ```powershell
   $token = az account get-access-token --resource "api://<client-id>" --query accessToken -o tsv
   $body = '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","messageId":"m1","role":"user","parts":[{"kind":"text","text":"Hello"}]}}}'
   Invoke-WebRequest -Uri http://localhost:5173/a2a/copilot-studio -Method POST -ContentType "application/json" -Headers @{ "Authorization" = "Bearer $token" } -Body $body
   ```

Phase 1 is now complete. Your A2A endpoint is protected, and each authenticated user gets an isolated identity in Direct Line.

---

## Phase 2: Copilot Studio SSO Integration

Phase 2 configures Copilot Studio to authenticate users via SSO (Single Sign-On). When a user sends an authenticated request to the A2A endpoint, their Entra ID token is exchanged with Copilot Studio so the agent can make API calls **on behalf of the user** — without prompting them to sign in again. This builds on Phase 1 — complete Phase 1 first.

### How SSO Works

```
Caller (A2A Client)
  │  Authorization: Bearer <Entra ID token>
  ▼
┌──────────────────────────────────────────┐
│      Copilot Studio A2A Server           │
│                                          │
│  1. Validate JWT (Entra ID)              │
│  2. Start Direct Line conversation       │
│     (no trusted dl_ user ID)             │
│  3. Send user's message                  │
│  4. Bot sends OAuthCard challenge        │
│  5. Server intercepts OAuthCard          │
│  6. Server sends signin/tokenExchange    │
│     with caller's original bearer token  │
│  7. Bot Framework validates token        │
│  8. Bot receives user's identity + token │
│  9. Bot responds with authenticated data │
└──────────────────────────────────────────┘
```

> **Key insight**: In SSO mode, the server does NOT embed a `dl_`-prefixed user ID in the Direct Line token. Using a `dl_` prefix creates a "trusted" session that suppresses the bot's Sign In topic, preventing it from sending the OAuthCard needed for SSO. Instead, the user's identity is established entirely through the token exchange.

### Step 2.1: Add a Client Secret to Your App Registration

1. In [Azure Portal → App Registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade), open your app
2. Go to **Certificates & secrets → Client secrets**
3. Click **New client secret**
   - **Description**: `Copilot Studio A2A` (or your choice)
   - **Expires**: Choose an appropriate expiration
4. Click **Add**
5. **Copy the secret value immediately** — it won't be shown again

> ⚠️ **Security**: This client secret will be shared between the A2A server and Copilot Studio. Treat compromise of either system as compromise of the integration. Store it in a vault or user-secrets, never in source control.

### Step 2.2: Add a Redirect URI

The Bot Framework's token service needs a redirect URI registered in your app.

1. In your app registration, go to **Authentication**
2. Click **Add a platform → Web**
3. Set **Redirect URI**: `https://token.botframework.com/.auth/web/redirect`
4. Under **Implicit grant and hybrid flows**, check:
   - ✅ **Access tokens**
   - ✅ **ID tokens**
5. Click **Configure**

> ⚠️ If you skip this step or forget to enable implicit grant tokens, you'll get `AADSTS500113: No reply address is registered for the application` errors in Copilot Studio, and the token exchange will fail with 502 from Direct Line.

### Step 2.3: Add API Permissions

Copilot Studio needs your app to have identity-related permissions for SSO.

1. In your app registration, go to **API permissions**
2. Click **Add a permission → Microsoft Graph → Delegated permissions**
3. Add:
   - `openid`
   - `profile`
4. Click **Add permissions**
5. Click **Grant admin consent for \<your tenant\>** (requires admin role)
6. Verify all permissions show ✅ **Granted** status

### Step 2.4: Configure Copilot Studio Authentication

1. Open your agent in [Copilot Studio](https://copilotstudio.microsoft.com)
2. Go to **Settings → Security → Authentication**
3. Enable **Require users to sign in**
4. Configure the authentication connection:

   | Field | Value |
   |-------|-------|
   | **Service provider** | **Microsoft Entra ID v2 with client secrets** |
   | **Client ID** | Your app registration's Application (client) ID |
   | **Client secret** | The secret you created in Step 2.1 |
   | **Token Exchange URL** | `api://<client-id>` (must match your Application ID URI) |
   | **Scopes** | `profile openid` |

   > The **Redirect URL** field will auto-populate with `https://token.botframework.com/.auth/web/redirect` — this should match what you configured in Step 2.2.

5. Click **Save**
6. **Publish the agent** — authentication changes don't take effect until published

> ⚠️ **Important**: Use **"Microsoft Entra ID v2 with client secrets"** as the service provider. Other options like "Federated Credentials" are not compatible with the Direct Line SSO flow used by this bridge.

### Step 2.5: Test Phase 2

No additional A2A server configuration is needed beyond Phase 1. The SSO token exchange uses the caller's original bearer token directly.

1. Restart the server:
   ```bash
   dotnet run
   ```

2. Get a token and send a request:
   ```bash
   TOKEN=$(az account get-access-token --resource "api://<client-id>" --query accessToken -o tsv)

   curl -X POST http://localhost:5173/a2a/copilot-studio \
     -H "Content-Type: application/json" \
     -H "Authorization: Bearer $TOKEN" \
     -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","messageId":"m1","role":"user","parts":[{"kind":"text","text":"Hello"}]}}}'
   ```

3. In the server logs, you should see:
   - `ssoMode=True` — SSO mode is active
   - `Found OAuthCard` — the bot sent an authentication challenge
   - `Token exchange accepted` — the token was successfully exchanged
   - `Received post-SSO response` — the bot responded after authentication

4. The bot should respond with content appropriate for the authenticated user, not a sign-in prompt

---

## SDK Mode: Copilot Studio SDK Authentication

SDK mode uses the Copilot Studio SDK (`Microsoft.CopilotStudio.Client`) to communicate with your agent via the Direct-to-Engine API, instead of Direct Line. This mode replaces Direct Line with a more direct integration path. It builds on Phase 1 — complete Phase 1 first.

### How SDK Auth Works

In SDK mode, authentication has two layers:

1. **API Authentication (OBO)**: The caller's bearer token is exchanged via MSAL's On-Behalf-Of flow for a Copilot Studio API token with `CopilotStudio.Copilots.Invoke` scope. This authenticates the SDK's HTTP calls to the Copilot Studio Direct-to-Engine API.
2. **Bot-Level SSO**: The bot's Sign In topic still triggers and sends an OAuthCard. The server intercepts it and sends a `signin/tokenExchange` invoke via the SDK's `ExecuteAsync`, passing the caller's original bearer token. This authenticates the user within the bot's auth flow.

```
Caller (A2A Client)
  │  Authorization: Bearer <Entra ID token>
  ▼
┌──────────────────────────────────────────────┐
│      Copilot Studio A2A Server               │
│                                              │
│  1. Validate JWT (Entra ID)                  │
│  2. OBO exchange → CopilotStudio API token   │
│  3. SDK connects to Direct-to-Engine API     │
│  4. Send user's message via ExecuteAsync     │
│  5. Bot sends OAuthCard challenge            │
│  6. Server intercepts OAuthCard              │
│  7. Server sends signin/tokenExchange        │
│     via ExecuteAsync with caller's token     │
│  8. Bot receives user's identity + token     │
│  9. Bot responds with authenticated data     │
└──────────────────────────────────────────────┘
```

### Prerequisites (in addition to Phase 1)

- App registration needs:
  - A **client secret** (for OBO) — if you completed Phase 2, you already have this
  - The **CopilotStudio.Copilots.Invoke** delegated permission from Power Platform API (with admin consent)
- Copilot Studio agent metadata:
  - **Environment ID** and **Schema Name** from Settings → Advanced → Metadata

### Step SDK.1: Add the Power Platform API Permission

1. Azure Portal → App registrations → your app → **API permissions**
2. Click **Add a permission**
3. Switch to the **APIs my organization uses** tab
4. Search for **Power Platform API**
5. Select **Delegated permissions**
6. Check **CopilotStudio.Copilots.Invoke**
7. Click **Add permissions**
8. Click **Grant admin consent for \<tenant\>**

### Step SDK.2: Get Agent Metadata

1. Open your agent in [Copilot Studio](https://copilotstudio.microsoft.com)
2. Go to **Settings → Advanced → Metadata**
3. Note the **Environment ID** and **Schema Name**

### Step SDK.3: Configure the A2A Server

Store configuration securely using [user-secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) for local development:

```bash
dotnet user-secrets set "CopilotStudio:ConnectionMode" "CopilotStudioSdk"
dotnet user-secrets set "CopilotStudio:EnvironmentId" "<environment-id>"
dotnet user-secrets set "CopilotStudio:SchemaName" "<schema-name>"
dotnet user-secrets set "CopilotStudio:AzureAd:TenantId" "<tenant-id>"
dotnet user-secrets set "CopilotStudio:AzureAd:ClientId" "<client-id>"
dotnet user-secrets set "CopilotStudio:AzureAd:ClientSecret" "<client-secret>"
```

For production deployments, use environment variables:

```bash
CopilotStudio__ConnectionMode=CopilotStudioSdk
CopilotStudio__EnvironmentId=<environment-id>
CopilotStudio__SchemaName=<schema-name>
CopilotStudio__AzureAd__TenantId=<tenant-id>
CopilotStudio__AzureAd__ClientId=<client-id>
CopilotStudio__AzureAd__ClientSecret=<client-secret>
```

### Step SDK.4: Test SDK Mode

1. Start the server:
   ```bash
   dotnet run
   ```

2. Check the health endpoint to confirm SDK mode:
   ```bash
   curl -s http://localhost:5173/a2a/copilot-studio/health | jq .
   # Look for: "mode": "CopilotStudioSdk"
   ```

3. Get a token and send a request:
   ```bash
   TOKEN=$(az account get-access-token --resource "api://<client-id>" --query accessToken -o tsv)

   curl -X POST http://localhost:5173/a2a/copilot-studio \
     -H "Content-Type: application/json" \
     -H "Authorization: Bearer $TOKEN" \
     -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","messageId":"m1","role":"user","parts":[{"kind":"text","text":"Hello"}]}}}'
   ```

4. In the server logs, you should see:
   - `connectionMode=CopilotStudioSdk` — SDK mode is active
   - `OBO token acquired` — the OBO exchange succeeded
   - `Found OAuthCard` — the bot sent an authentication challenge
   - `Token exchange accepted` — the SSO token was successfully exchanged
   - `Received post-SSO response` — the bot responded after authentication

---

## Using the Google ADK Web UI with Authentication

When authentication is enabled, the ADK client needs the bearer token passed as an environment variable:

```bash
# Get a token
# macOS / Linux:
export A2A_BEARER_TOKEN=$(az account get-access-token --resource "api://<client-id>" --query accessToken -o tsv)
# PowerShell:
$env:A2A_BEARER_TOKEN = (az account get-access-token --resource "api://<client-id>" --query accessToken -o tsv)

# Start the A2A server (from repo root)
dotnet run

# In a second terminal, start the ADK web UI
cd samples
adk web .
```

Then open http://127.0.0.1:8000 and chat with the agent. The token will be included in all A2A requests automatically.

> **Note**: Tokens expire (typically after 1 hour). If you get `401` errors, get a fresh token and restart the ADK web UI.

---

## Configuration Reference

### A2A Server Settings

All settings are under the `CopilotStudio` section in `appsettings.json` or prefixed with `CopilotStudio__` as environment variables.

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `EnableAuthPassthrough` | No | `false` | Enable JWT validation and SSO on A2A endpoints |
| `AzureAd:Instance` | No | `https://login.microsoftonline.com/` | Entra ID authority base URL |
| `AzureAd:TenantId` | Phase 1+ | *(empty)* | Your Entra ID tenant ID (GUID) |
| `AzureAd:ClientId` | Phase 1+ | *(empty)* | App Registration client ID (GUID) |
| `AzureAd:ClientSecret` | SDK mode | *(empty)* | App Registration client secret (required for OBO token exchange in SDK mode) |
| `ConnectionMode` | No | `DirectLine` | Connection mode: `DirectLine` or `CopilotStudioSdk` |
| `EnvironmentId` | SDK mode | *(empty)* | Power Platform environment ID (from Copilot Studio → Settings → Advanced → Metadata) |
| `SchemaName` | SDK mode | *(empty)* | Copilot Studio agent schema name (from Settings → Advanced → Metadata) |
| `Cloud` | No | `Prod` | Power Platform cloud: `Prod`, `Gov`, `High`, `DoD`, `Mooncake` |
| `DirectConnectUrl` | No | *(empty)* | Alternative to EnvironmentId + SchemaName; full Direct-to-Engine URL |

### Copilot Studio Settings (for Phase 2)

| Setting | Value |
|---------|-------|
| Authentication mode | Require users to sign in |
| Service provider | Microsoft Entra ID v2 with client secrets |
| Client ID | Same as A2A server's `AzureAd:ClientId` |
| Client secret | Same as A2A server's `AzureAd:ClientSecret` |
| Token Exchange URL | `api://<client-id>` (matches Application ID URI) |
| Scopes | `profile openid` |

---

## How User Identity Flows

### Without SSO (Phase 1 only or auth disabled)

When authentication is enabled but SSO is not fully configured, or when the bot doesn't require sign-in:

1. The server validates the bearer token using Microsoft Identity Web
2. It extracts the `oid` (object ID) claim — or falls back to `sub` (subject)
3. It combines the tenant ID + object ID and hashes them with SHA-256
4. The resulting hash is used as the Direct Line `user.id` in the format `dl_<hash>`

This ensures:
- **Privacy**: Raw identity claims are never sent to Copilot Studio
- **Determinism**: The same user always gets the same Direct Line user ID
- **Isolation**: Different users get separate conversation contexts

### With SSO (Phase 2)

When SSO is fully configured and a bearer token is present:

1. The server validates the bearer token
2. The Direct Line token is generated **without** a user ID (no `dl_` prefix)
3. The message `from.id` uses an `a2a_<hash>` prefix (non-trusted, allows Sign In topic to trigger)
4. The bot sends an **OAuthCard** as part of its Sign In topic
5. The server intercepts the OAuthCard and sends a `signin/tokenExchange` invoke with the caller's original bearer token
6. Bot Framework validates the token against the Token Exchange URL
7. The bot receives the user's identity and access token — it can now make API calls as the user
8. The bot responds with authenticated content

When authentication is **disabled** (default), the server uses a static user ID, and all callers share the same identity context.

---

## Troubleshooting

### 401 Unauthorized on A2A requests

- Verify the token was issued by the correct tenant
- Check that the token's `aud` (audience) claim matches your `AzureAd:ClientId` or Application ID URI
- Ensure the token hasn't expired (`az account get-access-token` gets a fresh one)
- Confirm `EnableAuthPassthrough` is `true` in your config
- Restart the server after changing configuration

### Bot responds with "I'll need you to sign in"

- This means Phase 2 is not configured correctly
- Verify Copilot Studio auth is set to **"Microsoft Entra ID v2 with client secrets"**
- Check that the **Token Exchange URL** matches your Application ID URI exactly
- Ensure you **published the agent** after saving auth settings
- Verify the client secret in Copilot Studio matches the one in your app registration

### `IntegratedAuthenticationNotSupportedInChannel` error

- This occurs when the Copilot Studio service provider is set to **Federated Credentials** — switch to **"Microsoft Entra ID v2 with client secrets"**
- Also check that the Token Exchange URL is set to `api://<client-id>`

### OAuthCard token exchange fails (502)

- Verify the **redirect URI** (`https://token.botframework.com/.auth/web/redirect`) is registered in your app's Authentication blade
- Check that **Access tokens** and **ID tokens** are enabled in implicit grant settings
- Ensure Microsoft Graph `openid` and `profile` permissions have **admin consent**
- Confirm the Token Exchange URL, Client ID, and Application ID URI all reference the same app

### Token works but bot doesn't know who I am

- This is expected — Phase 1 passes an opaque user ID, not the user's actual identity
- The bot sees a unique user ID but doesn't have the user's name, email, or access tokens
- Full SSO identity propagation requires Phase 2 configuration plus additional Copilot Studio topic design

### `Invalid cluster category value: Unknown` (SDK mode)

- Set `CopilotStudio:Cloud` to `Prod` (or the appropriate sovereign cloud value: `Gov`, `High`, `DoD`, `Mooncake`)
- The SDK needs the cloud to resolve the correct Direct-to-Engine API endpoint

### `AADSTS65001` or consent errors on OBO (SDK mode)

- Grant admin consent for `CopilotStudio.Copilots.Invoke` on the Power Platform API
- Go to Azure Portal → App registrations → your app → API permissions → verify the permission shows ✅ **Granted** status
- If using a multitenant app, the admin of the user's tenant must also grant consent

### `SDK mode requires an authenticated caller` (SDK mode)

- SDK mode always requires a bearer token; anonymous access is not supported
- Ensure `EnableAuthPassthrough` is `true` and you are passing a valid `Authorization: Bearer <token>` header

---

## Known Limitations

- **Per-request conversations**: Each A2A request creates a new Direct Line conversation. The SSO token exchange happens once per conversation. Multi-turn conversation support would benefit from conversation reuse (see TODO.md).
- **Token expiration**: Bearer tokens typically expire after 1 hour. Long-running clients should refresh tokens periodically. The ADK web UI requires restarting with a fresh `A2A_BEARER_TOKEN` when the token expires.
- **Single auth provider**: Only "Microsoft Entra ID v2 with client secrets" is supported. Federated Credentials and other providers cause `IntegratedAuthenticationNotSupportedInChannel` errors.
- **SSO adds latency**: The OAuthCard challenge/response adds approximately 2-5 seconds to the first response in each conversation due to the token exchange round trip.
- **SDK mode requires a client secret**: OBO token exchange in SDK mode requires a client secret; certificate-based authentication is not yet supported.
- **SDK S2S not supported**: Service-to-service (S2S) authentication for SDK mode is in private preview and not supported by this bridge.

See [TODO.md](../TODO.md) for the full roadmap including planned SSO improvements.
