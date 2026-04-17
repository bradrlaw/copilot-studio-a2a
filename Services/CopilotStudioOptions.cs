namespace CopilotStudioA2A.Services;

/// <summary>
/// Configuration for connecting to a Copilot Studio agent via Direct Line.
/// </summary>
public class CopilotStudioOptions
{
    /// <summary>
    /// The Direct Line secret from Copilot Studio's web channel configuration.
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
    /// Seconds to wait for a response from Copilot Studio before timing out.
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Milliseconds between polls to Direct Line for new activities.
    /// </summary>
    public int PollingIntervalMs { get; set; } = 500;

    /// <summary>
    /// When true, the server validates Entra ID bearer tokens on the A2A endpoint
    /// and passes the authenticated user identity to Copilot Studio via Direct Line.
    /// Default is false for backward compatibility with "No authentication" agents.
    /// </summary>
    public bool EnableAuthPassthrough { get; set; } = false;

    /// <summary>
    /// The Entra ID (Azure AD) tenant ID for token validation.
    /// Required when EnableAuthPassthrough is true.
    /// Example: "your-tenant-id" or "common" for multi-tenant.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The Entra ID application (client) ID that represents this A2A server.
    /// Used as the expected audience when validating incoming bearer tokens.
    /// Required when EnableAuthPassthrough is true.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Nested Entra ID / Azure AD configuration for Microsoft.Identity.Web.
    /// Bound from CopilotStudio:AzureAd in appsettings.
    /// </summary>
    public AzureAdOptions AzureAd { get; set; } = new();
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
