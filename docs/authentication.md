# Authentication Guide

This guide explains how to configure authenticated access to the Copilot Studio A2A bridge server. Authentication is modular — you can enable just Phase 1 (endpoint protection) or go further with Phase 2 (Copilot Studio SSO integration).

| Phase | What It Does | Copilot Studio Auth Required? |
|-------|-------------|-------------------------------|
| **Phase 1** | Validates caller tokens, blocks unauthenticated requests, passes opaque user ID to Direct Line | No — agent can use "No authentication" |
| **Phase 2** | Everything in Phase 1 + configures Copilot Studio to recognize authenticated users via SSO | Yes — agent must use "Entra ID v2 with client secrets" |

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

Phase 2 configures Copilot Studio to recognize authenticated users, enabling the agent to provide personalized responses without prompting users to sign in again. This builds on Phase 1 — complete Phase 1 first.

> **Current status**: When configured as described below, Copilot Studio accepts authenticated requests via Direct Line without displaying sign-in prompts. Reactive OAuthCard token exchange handling is implemented in the server for scenarios where the agent requires explicit SSO. See [Known Limitations](#known-limitations) for details.

### Step 2.1: Add a Client Secret to Your App Registration

1. In [Azure Portal → App Registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade), open your app
2. Go to **Certificates & secrets → Client secrets**
3. Click **New client secret**
   - **Description**: `Copilot Studio A2A` (or your choice)
   - **Expires**: Choose an appropriate expiration
4. Click **Add**
5. **Copy the secret value immediately** — it won't be shown again

> ⚠️ **Security**: This client secret will be shared between the A2A server and Copilot Studio. Treat compromise of either system as compromise of the integration. Store it in a vault or user-secrets, never in source control. When rotating the secret, update both the A2A server config and Copilot Studio simultaneously.

### Step 2.2: Add a Redirect URI

The Bot Framework's token service needs a redirect URI registered in your app.

1. In your app registration, go to **Authentication**
2. Click **Add a platform → Web**
3. Set **Redirect URI**: `https://token.botframework.com/.auth/web/redirect`
4. Under **Implicit grant and hybrid flows**, check:
   - ✅ **Access tokens**
   - ✅ **ID tokens**
5. Click **Configure**

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

### Step 2.5: Configure the A2A Server for SSO

Add the client secret and SSO scopes to your server configuration:

```bash
dotnet user-secrets set "CopilotStudio:AzureAd:ClientSecret" "<your-client-secret>"
dotnet user-secrets set "CopilotStudio:SsoScopes" "profile openid"
```

For production, use environment variables:

```bash
CopilotStudio__AzureAd__ClientSecret=<your-client-secret>
CopilotStudio__SsoScopes=profile openid
```

### Step 2.6: Test Phase 2

1. Restart the server to pick up the new configuration
2. Get a fresh token and send a request:
   ```bash
   TOKEN=$(az account get-access-token --resource "api://<client-id>" --query accessToken -o tsv)

   curl -X POST http://localhost:5173/a2a/copilot-studio \
     -H "Content-Type: application/json" \
     -H "Authorization: Bearer $TOKEN" \
     -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","messageId":"m1","role":"user","parts":[{"kind":"text","text":"What hours is the bank open?"}]}}}'
   ```
3. Verify the bot responds with content (not a sign-in prompt like "I'll need you to sign in")

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
| `EnableAuthPassthrough` | No | `false` | Enable JWT validation on A2A endpoints |
| `AzureAd:Instance` | No | `https://login.microsoftonline.com/` | Entra ID authority base URL |
| `AzureAd:TenantId` | Phase 1+ | *(empty)* | Your Entra ID tenant ID (GUID) |
| `AzureAd:ClientId` | Phase 1+ | *(empty)* | App Registration client ID (GUID) |
| `AzureAd:ClientSecret` | Phase 2 | *(empty)* | App Registration client secret (for OBO token exchange) |
| `SsoScopes` | Phase 2 | *(empty)* | Space-separated scopes for SSO token exchange (e.g., `profile openid`) |

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

When authentication is enabled:

1. The server validates the bearer token using Microsoft Identity Web
2. It extracts the `oid` (object ID) claim — or falls back to `sub` (subject)
3. It combines the tenant ID + object ID and hashes them with SHA-256
4. The resulting hash is used as the Direct Line `user.id` in the format `dl_<hash>`

This ensures:
- **Privacy**: Raw identity claims are never sent to Copilot Studio
- **Determinism**: The same user always gets the same Direct Line user ID
- **Isolation**: Different users get separate conversation contexts

When authentication is **disabled** (default), the server uses a static user ID, so all callers share the same identity context.

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

---

## Known Limitations

- **Proactive SSO**: Sending a `signin/tokenExchange` invoke before the first message consistently returns 502 from Direct Line. The server uses reactive OAuthCard handling instead (responds to OAuthCard challenges when the bot sends them).
- **SSO scope**: With "Entra ID v2 with client secrets" configured as described, Copilot Studio typically does not send OAuthCard challenges on Direct Line, so the reactive exchange may not be triggered. The bot responds normally without requiring explicit SSO sign-in.
- **Token expiration**: Bearer tokens typically expire after 1 hour. Long-running clients should refresh tokens periodically. The ADK web UI requires restarting with a fresh `A2A_BEARER_TOKEN` when the token expires.

See [TODO.md](../TODO.md) for the full roadmap including planned SSO improvements.
