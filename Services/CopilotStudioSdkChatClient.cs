using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Agents.CopilotStudio.Client.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace CopilotStudioA2A.Services;

/// <summary>
/// IChatClient implementation that proxies chat requests to a Copilot Studio
/// agent via the Copilot Studio SDK (Direct-to-Engine API).
/// Uses OBO (On Behalf Of) flow to exchange the caller's bearer token for a
/// Copilot Studio token with CopilotStudio.Copilots.Invoke scope.
/// </summary>
public class CopilotStudioSdkChatClient : IChatClient
{
    private readonly CopilotStudioOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CopilotStudioSdkChatClient> _logger;
    private readonly IConfidentialClientApplication _msalApp;
    private readonly ConnectionSettings _connectionSettings;

    private const string HttpClientName = "copilot-studio-sdk";

    public CopilotStudioSdkChatClient(
        IHttpClientFactory httpClientFactory,
        IOptions<CopilotStudioOptions> options,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CopilotStudioSdkChatClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        ValidateConfiguration();

        _connectionSettings = CreateConnectionSettings();

        // Build the MSAL confidential client for OBO token exchange.
        // The A2A server acts as a confidential client, exchanging the caller's
        // token for a Copilot Studio API token.
        _msalApp = ConfidentialClientApplicationBuilder
            .Create(_options.AzureAd.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _options.AzureAd.TenantId)
            .WithClientSecret(_options.AzureAd.ClientSecret)
            .Build();
    }

    public ChatClientMetadata Metadata => new("CopilotStudio-SDK");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var userMessage = chatMessages.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMessage is null)
        {
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "No user message provided."));
        }

        var messageText = userMessage.Text ?? string.Empty;

        try
        {
            // Create a fresh CopilotClient per request to avoid conversation state bleed.
            // The SDK's CopilotClient is stateful (tracks conversation internally).
            var copilotClient = await CreateCopilotClientAsync(cancellationToken);
            var conversationId = $"a2a-{Guid.NewGuid():N}";

            _logger.LogInformation(
                "Starting SDK conversation {ConversationId} for message",
                conversationId);

            // Start a conversation and collect the greeting (if any)
            var startRequest = new StartRequest
            {
                EmitStartConversationEvent = true,
                ConversationId = conversationId
            };

            await foreach (var activity in copilotClient.StartConversationAsync(startRequest, cancellationToken))
            {
                _logger.LogDebug("Start activity: type={Type}, text={Text}",
                    activity?.Type, activity?.Text?[..Math.Min(activity.Text.Length, 100)]);
            }

            // Send the user's message and collect the response
            var messageActivity = Activity.CreateMessageActivity();
            messageActivity.Text = messageText;

            var responseBuilder = new StringBuilder();
            var timeout = TimeSpan.FromSeconds(_options.ResponseTimeoutSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            await foreach (var activity in copilotClient.ExecuteAsync(
                conversationId, (Activity)messageActivity, cts.Token))
            {
                if (activity is null) continue;

                _logger.LogDebug(
                    "Response activity: type={Type}, text={Text}",
                    activity.Type,
                    activity.Text != null
                        ? activity.Text[..Math.Min(activity.Text.Length, 100)]
                        : "(none)");

                if (activity.Type == "message" && !string.IsNullOrEmpty(activity.Text))
                {
                    if (responseBuilder.Length > 0)
                        responseBuilder.AppendLine();
                    responseBuilder.Append(activity.Text);
                }
            }

            var botResponse = responseBuilder.Length > 0
                ? responseBuilder.ToString()
                : "No response received from Copilot Studio.";

            _logger.LogInformation(
                "Received SDK response from conversation {ConversationId} ({Length} chars)",
                conversationId, botResponse.Length);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, botResponse));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No response from Copilot Studio within {_options.ResponseTimeoutSeconds} seconds.");
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            _logger.LogError(ex, "Error communicating with Copilot Studio via SDK");
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                $"Error communicating with Copilot Studio: {ex.Message}"));
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        var text = response.Messages.LastOrDefault()?.Text ?? string.Empty;

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(text)]
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(CopilotStudioSdkChatClient) ? this : null;

    public void Dispose() { }

    /// <summary>
    /// Creates a fresh CopilotClient with OBO authentication for the current request.
    /// </summary>
    private async Task<CopilotClient> CreateCopilotClientAsync(CancellationToken cancellationToken)
    {
        // Get the scope for the Copilot Studio API
        var scope = CopilotClient.ScopeFromSettings(_connectionSettings);

        // Get the caller's bearer token for OBO exchange
        var callerToken = GetCallerBearerToken();
        if (string.IsNullOrEmpty(callerToken))
        {
            throw new InvalidOperationException(
                "SDK mode requires an authenticated caller. No bearer token found on the request.");
        }

        _logger.LogDebug("Performing OBO token exchange for Copilot Studio scope: {Scope}", scope);

        // Exchange the caller's token for a Copilot Studio API token via OBO
        var oboResult = await _msalApp
            .AcquireTokenOnBehalfOf([scope], new UserAssertion(callerToken))
            .ExecuteAsync(cancellationToken);

        _logger.LogDebug("OBO token acquired, expires: {Expiry}", oboResult.ExpiresOn);

        // Create the CopilotClient with the token provider function.
        // The token provider is called by the SDK when it needs to authenticate HTTP requests.
        var sdkLogger = _logger;
        var copilotClient = new CopilotClient(
            _connectionSettings,
            _httpClientFactory,
            (string _) =>
            {
                sdkLogger.LogDebug("SDK token provider invoked, returning OBO token");
                return Task.FromResult(oboResult.AccessToken);
            },
            _logger,
            HttpClientName);

        return copilotClient;
    }

    private ConnectionSettings CreateConnectionSettings()
    {
        var settings = new ConnectionSettings();

        if (!string.IsNullOrEmpty(_options.DirectConnectUrl))
        {
            settings.DirectConnectUrl = _options.DirectConnectUrl;
        }
        else
        {
            settings.EnvironmentId = _options.EnvironmentId!;
            settings.SchemaName = _options.SchemaName!;
        }

        return settings;
    }

    /// <summary>
    /// Extracts the raw bearer token from the current HTTP request's Authorization header.
    /// </summary>
    private string? GetCallerBearerToken()
    {
        var authHeader = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        return authHeader["Bearer ".Length..].Trim();
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_options.DirectConnectUrl)
            && (string.IsNullOrEmpty(_options.EnvironmentId) || string.IsNullOrEmpty(_options.SchemaName)))
        {
            throw new InvalidOperationException(
                "CopilotStudioSdk mode requires either DirectConnectUrl, or both EnvironmentId and SchemaName. " +
                "Set these in the CopilotStudio configuration section.");
        }

        if (string.IsNullOrEmpty(_options.AzureAd.ClientId))
        {
            throw new InvalidOperationException(
                "CopilotStudioSdk mode requires AzureAd:ClientId for OBO token exchange.");
        }

        if (string.IsNullOrEmpty(_options.AzureAd.TenantId))
        {
            throw new InvalidOperationException(
                "CopilotStudioSdk mode requires AzureAd:TenantId for OBO token exchange.");
        }

        if (string.IsNullOrEmpty(_options.AzureAd.ClientSecret))
        {
            throw new InvalidOperationException(
                "CopilotStudioSdk mode requires AzureAd:ClientSecret for OBO token exchange. " +
                "Store it in user secrets or environment variables.");
        }
    }
}
