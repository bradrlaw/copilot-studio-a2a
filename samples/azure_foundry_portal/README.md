# Azure AI Foundry Portal → Copilot Studio A2A Connection

Connect a **Microsoft Foundry** agent to your Copilot Studio A2A server using the Foundry portal — no code required.

## Architecture

```
User ──▶ Foundry Agent (GPT-4.1) ──A2A──▶ Copilot Studio A2A Server ──Direct Line──▶ Copilot Studio Agent
```

The Foundry agent uses the built-in **A2A tool** to discover and communicate with the Copilot Studio A2A server. When a user asks a question that falls within the Copilot Studio agent's domain, the Foundry agent delegates to it via the A2A protocol and returns the response.

## Prerequisites

- **Azure subscription** with an active [Microsoft Foundry](https://ai.azure.com) project
- **Contributor** or **Owner** role on the Foundry resource (for creating connections), plus **Azure AI User** role (for building agents)
- **A deployed model** in your Foundry project (e.g., `gpt-4.1-mini`)
- **The Copilot Studio A2A server running** over **HTTPS** and accessible from Azure — see the [main README](../../README.md) for setup

> **Important:** The A2A server must be reachable from Azure's network over HTTPS with a valid TLS certificate. For local testing, use a tunnel service (e.g., [dev tunnels](https://learn.microsoft.com/azure/developer/dev-tunnels/), [ngrok](https://ngrok.com)) to expose your local server with HTTPS. For production, deploy to Azure App Service, Azure Container Apps, or similar.
>
> **Verify reachability:** Before creating the connection, confirm the agent card is accessible:
> ```
> curl https://your-host/a2a/copilot-studio/v1/card
> ```

## Step 1: Determine Your Authentication Method

Choose an authentication approach based on your scenario:

| Scenario | Recommended Method | User Context |
|---|---|---|
| Simple testing / no auth on A2A server | Unauthenticated | No |
| A2A server uses Entra ID, need per-user identity (SSO) | OAuth identity passthrough (Custom OAuth) | Yes |

> **Tip:** If your A2A server has `EnableAuthPassthrough` turned on, you need OAuth identity passthrough for full SSO support. See [docs/authentication.md](../../docs/authentication.md) for how the A2A server validates tokens.
>
> **Note:** Key-based and managed identity authentication are supported by Foundry but are not covered in this guide because the Copilot Studio A2A server uses Entra ID delegated (user) authentication. If you've added a custom auth layer in front of the server (e.g., API Management with subscription keys), you can use key-based auth — refer to the [Foundry A2A docs](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/agent-to-agent) for those options.

## Step 2: Create the A2A Connection

1. Sign in to [Microsoft Foundry](https://ai.azure.com) and make sure the **New Foundry** toggle is enabled (top of the page).

2. Open your **project**.

3. In the left navigation, select **Tools**.

4. Select **Connect tool**.

5. Select the **Custom** tab.

6. Select **Agent2Agent (A2A)**, then select **Create**.

7. Fill in the connection details:

   | Field | Value |
   |---|---|
   | **Name** | `copilot-studio-a2a` (or any descriptive name) |
   | **A2A Agent Endpoint** | Your server URL, e.g., `https://your-host/a2a/copilot-studio` |

8. Configure **Authentication** based on your chosen method (see sections below).

9. Select **Create** to save the connection.

### Option A: Unauthenticated (Testing Only)

Select **None** for authentication. Use this only when your A2A server does not require authentication (`EnableAuthPassthrough` is `false` or not set).

### Option B: OAuth Identity Passthrough (Custom OAuth)

Use this when your A2A server has `EnableAuthPassthrough` enabled and you need per-user identity (including SSO token exchange to Copilot Studio).

> **Important:** This option requires that your Entra ID app registration and Copilot Studio agent are already configured for SSO. If you haven't done this yet, complete the setup in [docs/authentication.md](../../docs/authentication.md) first.

1. In the connection creation dialog, select **Custom OAuth** under Authentication.

2. Fill in the OAuth fields:

   | Field | Value |
   |---|---|
   | **Client ID** | Your Entra ID app registration's Application (client) ID |
   | **Client secret** | A client secret from the same app registration (leave blank if not required) |
   | **Authorization URL** | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/authorize` |
   | **Token URL** | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token` |
   | **Refresh URL** | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token` |
   | **Scopes** | `api://<client-id>/access_as_user` (must match your app registration's exposed API) |

   Replace `<tenant-id>` and `<client-id>` with your actual values.

3. After creating the connection, Foundry provides a **redirect URL**. Copy this URL.

4. In the [Azure Portal](https://portal.azure.com), go to your app registration → **Authentication** → **Add a platform** (or edit Web) → add the Foundry redirect URL to **Redirect URIs**.

   > Your app registration should now have **two** redirect URIs:
   > - The Foundry redirect URL (from step 3 above)
   > - `https://token.botframework.com/.auth/web/redirect` (required for Copilot Studio SSO)

5. Verify your app registration also has:
   - **Expose an API** → Application ID URI set to `api://<client-id>`
   - **Expose an API** → A scope named `access_as_user` (or matching your Scopes value above)
   - **Authentication** → Implicit grant: both **Access tokens** and **ID tokens** checked
   - **API permissions** → Microsoft Graph: `openid`, `profile` (delegated) with admin consent

6. Verify your Copilot Studio agent's authentication is configured:
   - Service provider: **Microsoft Entra ID v2 with client secrets**
   - Token Exchange URL: `api://<client-id>`
   - The agent must be **published** after any auth changes

> **How it works:** When a user first interacts with the Foundry agent and it invokes the A2A tool, the user is prompted to sign in. After consent, Foundry securely stores the user's tokens and includes the access token in all subsequent A2A requests. The Copilot Studio A2A server validates this token and passes it through to Copilot Studio for SSO token exchange, enabling the bot to make API calls on behalf of the user.

## Step 3: Create a Foundry Agent with the A2A Tool

1. In the Foundry portal, go to **Build** → **Agents**.

2. Select **Create agent**.

3. Configure the agent:

   | Setting | Value |
   |---|---|
   | **Name** | `my-copilot-bridge` (or any name) |
   | **Model** | Select a deployed model (e.g., `gpt-4.1-mini`) |
   | **Instructions** | See example below |

4. Under **Tools**, select **Add tool**.

5. Select the `copilot-studio-a2a` connection you created in Step 2.

6. **Save** and optionally **publish** the agent.

### Example Instructions

Adjust the instructions to match your Copilot Studio agent's domain. For example, if the Copilot Studio agent is a virtual banking assistant:

```
You are a helpful assistant. When the user asks about banking, branch hours,
accounts, transfers, or financial services, use the Copilot Studio banking
agent tool to get the answer. For all other questions, answer directly.
```

## Step 4: Test the Connection

1. Open the agent in the Foundry portal.

2. Start a conversation in the **Test** pane.

3. Ask a question that should be delegated to Copilot Studio:

   ```
   What hours are your branches open?
   ```

4. Verify the agent invokes the A2A tool and returns a response from Copilot Studio.

5. Ask a general question to confirm the Foundry agent handles it directly:

   ```
   What is the capital of France?
   ```

### Testing OAuth Identity Passthrough

If you configured Custom OAuth:

1. The first time the A2A tool is invoked, a **consent link** appears in the response.
2. Click the link and sign in with your credentials.
3. Authorize the requested permissions.
4. Return to the agent and ask the same question again — it should now succeed.

## Troubleshooting

| Issue | Cause | Fix |
|---|---|---|
| A2A tool call fails with **401 Unauthorized** | Token expired, invalid credentials, or wrong audience | Regenerate the token/secret and update the connection; verify the app registration's audience matches the scope |
| A2A tool call fails with **400 Bad Request** | Wrong header name or value format in key-based auth | Check the endpoint docs for the expected header format (e.g., `Authorization: Bearer <token>`) |
| A2A tool call fails with **403 Forbidden** | Identity lacks required permissions | Verify the user has the required permissions and that admin consent was granted for API permissions |
| Connection creation unavailable | Insufficient role on Foundry resource | Ensure you have **Contributor** or **Owner** role on the Foundry resource, not just Azure AI User |
| No consent link for OAuth | OAuth not configured or tool not invoked | Verify the connection uses OAuth identity passthrough and trigger a query that requires the A2A tool |
| Consent link fails with sign-in error | Missing redirect URI in app registration | Add the Foundry redirect URL to your app registration's Redirect URIs |
| OAuth consent succeeds but tool call fails | User lacks access to the underlying service | Verify the user has the necessary permissions; check that scopes match the exposed API |
| OAuth stops working after extended period | Refresh token expired | The user needs to go through the consent flow again — this is expected behavior |
| A2A tool call returns **503** or timeout | A2A server not reachable from Azure | Ensure the server is publicly accessible over HTTPS with a valid TLS certificate; check the endpoint URL |
| Response says "I'll need you to sign in" | SSO not configured end-to-end | Complete all steps in Option B above; verify Copilot Studio auth provider, Token Exchange URL, and that the agent is published |
| Tool call succeeds but response is empty | Copilot Studio agent not published or misconfigured | Verify the agent is published and responds correctly in the Copilot Studio test canvas |

## Next Steps

- **SDK sample**: For a programmatic approach using `azure-ai-projects`, see the [Azure Foundry SDK sample](../azure_foundry_sdk/) (coming soon)
- **Authentication deep dive**: See [docs/authentication.md](../../docs/authentication.md) for full Entra ID and SSO configuration
- **Architecture**: See [docs/architecture.md](../../docs/architecture.md) for system diagrams
