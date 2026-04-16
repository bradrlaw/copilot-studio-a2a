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
}
