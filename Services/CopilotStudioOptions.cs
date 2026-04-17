namespace CopilotStudioA2A.Services;

/// <summary>
/// How the server connects to Copilot Studio.
/// </summary>
public enum ConnectionMode
{
    /// <summary>
    /// Connect via Bot Framework Direct Line API (default).
    /// Requires DirectLineSecret or TokenEndpoint.
    /// </summary>
    DirectLine,

    /// <summary>
    /// Connect via the Copilot Studio SDK (Direct-to-Engine API).
    /// Requires EnvironmentId + SchemaName and authenticated callers.
    /// </summary>
    CopilotStudioSdk
}

/// <summary>
/// Configuration for connecting to a Copilot Studio agent.
/// Supports both Direct Line and Copilot Studio SDK connection modes.
/// </summary>
public class CopilotStudioOptions
{
    /// <summary>
    /// The connection mode to use. Defaults to DirectLine for backward compatibility.
    /// </summary>
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.DirectLine;

    #region Direct Line settings

    /// <summary>
    /// The Direct Line secret from Copilot Studio's web channel configuration.
    /// Required when ConnectionMode is DirectLine.
    /// </summary>
    public string DirectLineSecret { get; set; } = string.Empty;

    /// <summary>
    /// The Direct Line endpoint. Defaults to the standard Bot Framework endpoint.
    /// </summary>
    public string DirectLineEndpoint { get; set; } = "https://directline.botframework.com/v3/directline";

    /// <summary>
    /// Optional: The Copilot Studio token endpoint URL for regional deployments.
    /// If set, tokens are obtained from this endpoint instead of using the Direct Line secret.
    /// </summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// Milliseconds between polls to Direct Line for new activities.
    /// Only used in DirectLine mode.
    /// </summary>
    public int PollingIntervalMs { get; set; } = 500;

    #endregion

    #region Copilot Studio SDK settings

    /// <summary>
    /// The Power Platform environment ID containing the Copilot Studio agent.
    /// Required when ConnectionMode is CopilotStudioSdk.
    /// Found in Copilot Studio: Settings → Advanced → Metadata.
    /// </summary>
    public string? EnvironmentId { get; set; }

    /// <summary>
    /// The schema name of the Copilot Studio agent.
    /// Required when ConnectionMode is CopilotStudioSdk.
    /// Found in Copilot Studio: Settings → Advanced → Metadata.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// Optional: Direct connect URL for the Copilot Studio agent.
    /// Alternative to EnvironmentId + SchemaName. Used for custom/regional endpoints.
    /// </summary>
    public string? DirectConnectUrl { get; set; }

    #endregion

    #region Shared settings

    /// <summary>
    /// Seconds to wait for a response from Copilot Studio before timing out.
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// When true, the server validates Entra ID bearer tokens on the A2A endpoint
    /// and passes the authenticated user identity to Copilot Studio.
    /// In DirectLine mode, this enables SSO token exchange via OAuthCard.
    /// In CopilotStudioSdk mode, this is always effectively true (SDK requires auth).
    /// Default is false for backward compatibility with DirectLine "No authentication" agents.
    /// </summary>
    public bool EnableAuthPassthrough { get; set; } = false;

    /// <summary>
    /// The Entra ID (Azure AD) tenant ID for token validation.
    /// Required when EnableAuthPassthrough is true or ConnectionMode is CopilotStudioSdk.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The Entra ID application (client) ID that represents this A2A server.
    /// Used as the expected audience when validating incoming bearer tokens.
    /// Required when EnableAuthPassthrough is true or ConnectionMode is CopilotStudioSdk.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Nested Entra ID / Azure AD configuration for Microsoft.Identity.Web.
    /// Bound from CopilotStudio:AzureAd in appsettings.
    /// </summary>
    public AzureAdOptions AzureAd { get; set; } = new();

    #endregion
}

/// <summary>
/// Nested Azure AD configuration section used by Microsoft.Identity.Web.
/// </summary>
public class AzureAdOptions
{
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
