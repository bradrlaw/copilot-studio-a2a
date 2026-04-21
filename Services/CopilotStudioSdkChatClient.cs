using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Agents.CopilotStudio.Client.Discovery;
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
/// Supports SSO token exchange when the agent has authentication enabled.
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
            var copilotClient = await CreateCopilotClientAsync(cancellationToken);
            var conversationId = $"a2a-{Guid.NewGuid():N}";

            _logger.LogInformation(
                "Starting SDK conversation {ConversationId} for message",
                conversationId);

            // Start a conversation and check for SSO challenge in greeting
            var startRequest = new StartRequest
            {
                EmitStartConversationEvent = true,
                ConversationId = conversationId
            };

            OAuthCardInfo? oauthCard = null;
            await foreach (var activity in copilotClient.StartConversationAsync(startRequest, cancellationToken))
            {
                if (activity is null) continue;
                LogActivity("Start", activity);

                // Check for OAuthCard in startup activities
                oauthCard ??= ExtractOAuthCard(activity);
            }

            // If bot sent an OAuthCard during startup, perform SSO token exchange
            if (oauthCard is not null)
            {
                await PerformSsoTokenExchangeAsync(
                    copilotClient, conversationId, oauthCard.Value, cancellationToken);
            }

            // Send the user's message and collect the response
            var messageActivity = Activity.CreateMessageActivity();
            messageActivity.Text = messageText;

            var responseBuilder = new StringBuilder();
            var timeout = TimeSpan.FromSeconds(_options.ResponseTimeoutSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            bool ssoExchangedDuringExecute = false;
            await foreach (var activity in copilotClient.ExecuteAsync(
                conversationId, (Activity)messageActivity, cts.Token))
            {
                if (activity is null) continue;
                LogActivity("Response", activity);

                // OAuthCard can also appear in response to the user message
                if (!ssoExchangedDuringExecute && oauthCard is null)
                {
                    var cardInResponse = ExtractOAuthCard(activity);
                    if (cardInResponse is not null)
                    {
                        oauthCard = cardInResponse;
                        ssoExchangedDuringExecute = true;
                        await PerformSsoTokenExchangeAsync(
                            copilotClient, conversationId, cardInResponse.Value, cancellationToken);
                        // Don't resend the message — bot already has it.
                        // Continue consuming activities for the post-auth continuation.
                        continue;
                    }
                }

                if (activity.Type == "message" && !string.IsNullOrEmpty(activity.Text))
                {
                    if (responseBuilder.Length > 0)
                        responseBuilder.AppendLine();
                    responseBuilder.Append(activity.Text);
                }
            }

            // If SSO happened during ExecuteAsync, the post-auth response may come
            // as a separate stream. Wait for it by sending an empty activity.
            if (ssoExchangedDuringExecute && responseBuilder.Length == 0)
            {
                _logger.LogInformation("Waiting for post-SSO continuation...");
                await foreach (var activity in copilotClient.ExecuteAsync(
                    conversationId, (Activity)Activity.CreateMessageActivity(), cts.Token))
                {
                    if (activity is null) continue;
                    LogActivity("PostSSO", activity);

                    if (activity.Type == "message" && !string.IsNullOrEmpty(activity.Text))
                    {
                        if (responseBuilder.Length > 0)
                            responseBuilder.AppendLine();
                        responseBuilder.Append(activity.Text);
                    }
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

    #region SSO Token Exchange

    /// <summary>
    /// Performs SSO token exchange with the bot via the SDK.
    /// Sends a signin/tokenExchange invoke activity with the caller's bearer token.
    /// </summary>
    private async Task PerformSsoTokenExchangeAsync(
        CopilotClient copilotClient,
        string conversationId,
        OAuthCardInfo oauthCard,
        CancellationToken cancellationToken)
    {
        var callerToken = GetCallerBearerToken();
        if (string.IsNullOrEmpty(callerToken))
        {
            _logger.LogWarning("SSO token exchange skipped: no caller bearer token available");
            return;
        }

        // Validate that the caller's token audience matches the OAuthCard's requested resource
        if (!string.IsNullOrEmpty(oauthCard.ExchangeResourceUri))
        {
            var tokenAudience = GetTokenAudience(callerToken);
            if (!string.IsNullOrEmpty(tokenAudience) &&
                !string.Equals(tokenAudience, oauthCard.ExchangeResourceUri, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SSO token exchange skipped: caller token audience '{Audience}' does not match " +
                    "OAuthCard resource URI '{ResourceUri}'",
                    tokenAudience, oauthCard.ExchangeResourceUri);
                return;
            }
        }

        _logger.LogInformation(
            "Performing SSO token exchange for connection '{Connection}' in conversation {ConversationId}",
            oauthCard.ConnectionName, conversationId);

        // Build the token exchange invoke activity
        var tokenExchangeActivity = new Activity
        {
            Type = "invoke",
            Name = "signin/tokenExchange",
            Value = new TokenExchangeInvokeRequest
            {
                ConnectionName = oauthCard.ConnectionName,
                Id = oauthCard.ExchangeResourceId,
                Token = callerToken
            }
        };

        try
        {
            await foreach (var activity in copilotClient.ExecuteAsync(
                conversationId, tokenExchangeActivity, cancellationToken))
            {
                if (activity is null) continue;
                LogActivity("SSO-Exchange", activity);
            }

            _logger.LogInformation("SSO token exchange completed for conversation {ConversationId}",
                conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SSO token exchange failed for connection '{Connection}' in conversation {ConversationId}",
                oauthCard.ConnectionName, conversationId);
        }
    }

    /// <summary>
    /// Scans an activity for OAuthCard attachments, extracting connection name
    /// and token exchange resource info.
    /// </summary>
    private OAuthCardInfo? ExtractOAuthCard(IActivity activity)
    {
        if (activity.Attachments is null || activity.Attachments.Count == 0)
            return null;

        foreach (var attachment in activity.Attachments)
        {
            if (attachment.ContentType != OAuthCard.ContentType)
                continue;

            // The Content may be an OAuthCard directly, or a deserialized JSON object
            OAuthCard? card = null;
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            if (attachment.Content is OAuthCard typedCard)
            {
                card = typedCard;
            }
            else if (attachment.Content is JsonElement jsonElement)
            {
                card = JsonSerializer.Deserialize<OAuthCard>(jsonElement.GetRawText(), jsonOptions);
            }
            else if (attachment.Content is not null)
            {
                try
                {
                    var json = JsonSerializer.Serialize(attachment.Content);
                    card = JsonSerializer.Deserialize<OAuthCard>(json, jsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize OAuthCard from attachment content type {Type}",
                        attachment.Content.GetType().Name);
                }
            }

            if (card is null || string.IsNullOrEmpty(card.ConnectionName))
                continue;

            _logger.LogInformation(
                "Found OAuthCard: connection={Connection}, exchangeResourceId={Id}, exchangeResourceUri={Uri}",
                card.ConnectionName,
                card.TokenExchangeResource?.Id ?? "(none)",
                card.TokenExchangeResource?.Uri ?? "(none)");

            return new OAuthCardInfo(
                card.ConnectionName,
                card.TokenExchangeResource?.Id,
                card.TokenExchangeResource?.Uri);
        }

        return null;
    }

    /// <summary>
    /// Extracts the audience claim from a JWT token without full validation.
    /// </summary>
    private static string? GetTokenAudience(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (handler.CanReadToken(jwt))
            {
                var token = handler.ReadJwtToken(jwt);
                return token.Audiences.FirstOrDefault();
            }
        }
        catch { /* Best-effort extraction */ }
        return null;
    }

    private readonly record struct OAuthCardInfo(
        string ConnectionName, string? ExchangeResourceId, string? ExchangeResourceUri);

    #endregion

    #region Activity Logging

    private void LogActivity(string phase, IActivity activity)
    {
        var text = activity.Text;
        var truncated = text is not null
            ? text[..Math.Min(text.Length, 100)]
            : "(none)";
        var attachmentCount = activity.Attachments?.Count ?? 0;

        _logger.LogDebug(
            "{Phase} activity: type={Type}, text={Text}, attachments={AttachmentCount}",
            phase, activity.Type, truncated, attachmentCount);
    }

    #endregion

    #region Setup

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

        // Parse the Power Platform cloud from configuration (default: Prod)
        if (!Enum.TryParse<PowerPlatformCloud>(_options.Cloud, ignoreCase: true, out var cloud))
            cloud = PowerPlatformCloud.Prod;
        settings.Cloud = cloud;

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

    #endregion
}
