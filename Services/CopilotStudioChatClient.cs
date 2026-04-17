using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace CopilotStudioA2A.Services;

/// <summary>
/// IChatClient implementation that proxies chat requests to a Copilot Studio
/// agent via the Bot Framework Direct Line API.
/// Supports optional Entra ID auth passthrough and SSO token exchange.
/// </summary>
public class CopilotStudioChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly CopilotStudioOptions _options;
    private readonly ILogger<CopilotStudioChatClient> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CopilotStudioChatClient(
        HttpClient httpClient,
        IOptions<CopilotStudioOptions> options,
        ILogger<CopilotStudioChatClient> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public ChatClientMetadata Metadata => new("CopilotStudio-DirectLine");

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
        var userId = ResolveDirectLineUserId();

        try
        {
            var token = await GetTokenAsync(userId, cancellationToken);
            var (conversationId, _) = await StartConversationAsync(token, cancellationToken);
            _logger.LogInformation("Started Direct Line conversation {ConversationId} for user {UserId}",
                conversationId, userId ?? "anonymous");

            await SendMessageAsync(token, conversationId, messageText, userId, cancellationToken);
            var botResponse = await PollForResponseAsync(token, conversationId, userId, cancellationToken);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, botResponse));
        }
        catch (SsoTokenExchangeException ex)
        {
            _logger.LogError(ex, "SSO token exchange failed for conversation");
            throw new InvalidOperationException(
                $"Authentication failed: {ex.Message}. Ensure the caller's token and Copilot Studio auth are configured correctly.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Copilot Studio");
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
        => serviceType == typeof(CopilotStudioChatClient) ? this : null;

    public void Dispose() { }

    #region User identity

    /// <summary>
    /// Resolves a stable, opaque Direct Line user ID from the authenticated HttpContext.
    /// Returns null when auth passthrough is disabled or the user is not authenticated.
    /// </summary>
    private string? ResolveDirectLineUserId()
    {
        if (!_options.EnableAuthPassthrough)
            return null;

        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        var objectId = user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
            ?? user.FindFirstValue("oid");
        var tenantId = user.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid")
            ?? user.FindFirstValue("tid");
        var subject = objectId
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");

        if (string.IsNullOrEmpty(subject))
        {
            _logger.LogWarning("Authenticated user has no identifiable claim (oid/sub/nameidentifier)");
            return null;
        }

        var raw = string.IsNullOrEmpty(tenantId) ? subject : $"{tenantId}:{subject}";
        return $"dl_{ComputeStableHash(raw)}";
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

    private static string ComputeStableHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash)[..16];
    }

    #endregion

    #region SSO / OBO token exchange

    /// <summary>
    /// Performs an On-Behalf-Of token exchange to get a token suitable for
    /// Copilot Studio's auth connection, using the caller's inbound bearer token.
    /// </summary>
    private async Task<string> ExchangeTokenOboAsync(CancellationToken cancellationToken)
    {
        var inboundToken = GetCallerBearerToken()
            ?? throw new SsoTokenExchangeException("No bearer token available for OBO exchange.");

        var clientId = _options.AzureAd.ClientId ?? _options.ClientId
            ?? throw new SsoTokenExchangeException("ClientId is required for OBO token exchange.");
        var clientSecret = _options.AzureAd.ClientSecret ?? _options.ClientSecret
            ?? throw new SsoTokenExchangeException("ClientSecret is required for OBO token exchange.");
        var tenantId = _options.AzureAd.TenantId ?? _options.TenantId
            ?? throw new SsoTokenExchangeException("TenantId is required for OBO token exchange.");
        var scopes = _options.SsoScopes?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            ?? throw new SsoTokenExchangeException("SsoScopes is required for OBO token exchange.");

        var app = ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .Build();

        try
        {
            var result = await app
                .AcquireTokenOnBehalfOf(scopes, new UserAssertion(inboundToken))
                .ExecuteAsync(cancellationToken);

            _logger.LogDebug("OBO token exchange succeeded, new token expires at {Expiry}", result.ExpiresOn);
            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex, "OBO token exchange failed: {Error}", ex.Message);
            throw new SsoTokenExchangeException($"OBO token exchange failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends a signin/tokenExchange invoke activity to Direct Line,
    /// responding to an OAuthCard challenge from the bot.
    /// </summary>
    private async Task SendTokenExchangeAsync(
        string dlToken,
        string conversationId,
        string? userId,
        string connectionName,
        string? exchangeResourceId,
        string ssoToken,
        CancellationToken cancellationToken)
    {
        var activity = new JsonObject
        {
            ["type"] = "invoke",
            ["name"] = "signin/tokenExchange",
            ["from"] = new JsonObject { ["id"] = userId ?? "a2a-client" },
            ["value"] = new JsonObject
            {
                ["connectionName"] = connectionName,
                ["token"] = ssoToken,
            }
        };

        // Echo back the tokenExchangeResource.id if present (required by Direct Line SSO protocol)
        if (!string.IsNullOrEmpty(exchangeResourceId))
        {
            activity["value"]!["id"] = exchangeResourceId;
        }

        var content = new StringContent(activity.ToJsonString(), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.DirectLineEndpoint}/conversations/{conversationId}/activities")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", dlToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Token exchange failed with {Status}: {Body}", response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode(); // throw
        }
        _logger.LogInformation("Sent tokenExchange for connection '{Connection}' in conversation {ConversationId}",
            connectionName, conversationId);
    }

    #endregion

    #region Direct Line helpers

    private async Task<string> GetTokenAsync(string? userId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_options.TokenEndpoint))
        {
            var tokenResponse = await _httpClient.GetAsync(_options.TokenEndpoint, cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();
            var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
            return tokenJson?["token"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Token endpoint did not return a token.");
        }

        var body = new JsonObject();
        if (!string.IsNullOrEmpty(userId))
        {
            body["user"] = new JsonObject { ["id"] = userId };
            _logger.LogDebug("Including user identity {UserId} in Direct Line token request", userId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.DirectLineEndpoint}/tokens/generate")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.DirectLineSecret);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        return json?["token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Failed to generate Direct Line token.");
    }

    private async Task<(string ConversationId, string? StreamUrl)> StartConversationAsync(
        string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.DirectLineEndpoint}/conversations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        var conversationId = json?["conversationId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Failed to start conversation.");
        var streamUrl = json?["streamUrl"]?.GetValue<string>();

        return (conversationId, streamUrl);
    }

    private async Task SendMessageAsync(
        string token, string conversationId, string text, string? userId,
        CancellationToken cancellationToken)
    {
        var activity = new JsonObject
        {
            ["type"] = "message",
            ["from"] = new JsonObject { ["id"] = userId ?? "a2a-client" },
            ["text"] = text
        };

        var content = new StringContent(activity.ToJsonString(), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.DirectLineEndpoint}/conversations/{conversationId}/activities")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Sent message to conversation {ConversationId}", conversationId);
    }

    /// <summary>
    /// Polls Direct Line for the bot's response, handling OAuthCard challenges
    /// with SSO token exchange when auth passthrough is enabled.
    /// Uses a state machine to avoid returning pre-auth responses and to
    /// prevent infinite exchange retry loops.
    /// </summary>
    private async Task<string> PollForResponseAsync(
        string token, string conversationId, string? userId,
        CancellationToken cancellationToken)
    {
        var senderId = userId ?? "a2a-client";
        string? watermark = null;
        var timeout = TimeSpan.FromSeconds(_options.ResponseTimeoutSeconds);
        var pollingInterval = TimeSpan.FromMilliseconds(_options.PollingIntervalMs);
        var deadline = DateTime.UtcNow + timeout;

        var exchangeAttempted = false; // Only one SSO exchange attempt allowed
        var awaitingPostExchange = false; // After exchange, wait for the real response

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"{_options.DirectLineEndpoint}/conversations/{conversationId}/activities";
            if (watermark != null)
                url += $"?watermark={watermark}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
            watermark = json?["watermark"]?.GetValue<string>();

            var activities = json?["activities"]?.AsArray();
            if (activities is not null)
            {
                _logger.LogDebug("Poll returned {Count} activities (exchangeAttempted={Attempted})",
                    activities.Count, exchangeAttempted);

                // First pass: check for OAuthCard challenges (before returning any text)
                if (_options.EnableAuthPassthrough && !exchangeAttempted)
                {
                    var oauthInfo = ExtractOAuthCardInfo(activities, senderId);
                    if (oauthInfo is not null)
                    {
                        exchangeAttempted = true;
                        _logger.LogInformation("OAuthCard detected (connection: {Connection}), performing SSO token exchange",
                            oauthInfo.Value.ConnectionName);

                        try
                        {
                            // Use the caller's original bearer token — the bot's auth connection
                            // expects a token with its own app as the audience, which matches
                            // the Token Exchange URL (api://<clientId>).
                            var callerToken = GetCallerBearerToken();
                            string ssoToken;
                            if (!string.IsNullOrEmpty(callerToken))
                            {
                                _logger.LogDebug("Using caller's original bearer token for reactive SSO");
                                ssoToken = callerToken;
                            }
                            else
                            {
                                _logger.LogDebug("No caller bearer token, falling back to OBO");
                                ssoToken = await ExchangeTokenOboAsync(cancellationToken);
                            }

                            await SendTokenExchangeAsync(token, conversationId, userId,
                                oauthInfo.Value.ConnectionName,
                                oauthInfo.Value.ExchangeResourceId,
                                ssoToken, cancellationToken);

                            awaitingPostExchange = true;
                            // Extend deadline after successful exchange
                            deadline = DateTime.UtcNow + timeout;
                            await Task.Delay(pollingInterval, cancellationToken);
                            continue;
                        }
                        catch (SsoTokenExchangeException)
                        {
                            throw; // Let auth failures propagate
                        }
                    }
                }

                // Second pass: look for the bot's text response
                foreach (var activity in activities)
                {
                    var fromId = activity?["from"]?["id"]?.GetValue<string>();
                    var type = activity?["type"]?.GetValue<string>();
                    var activityText = activity?["text"]?.GetValue<string>();

                    if (type == "message" && fromId != senderId && !string.IsNullOrEmpty(activityText))
                    {
                        // If we're awaiting a post-exchange response, skip pre-auth fallback messages
                        // like "Please sign in" that may arrive in the same batch as the OAuthCard
                        if (awaitingPostExchange)
                        {
                            // After exchange, return the next real response
                            _logger.LogInformation("Received post-SSO response from conversation {ConversationId}",
                                conversationId);
                        }
                        else
                        {
                            _logger.LogInformation("Received response from conversation {ConversationId}",
                                conversationId);
                        }
                        return activityText;
                    }
                }
            }

            await Task.Delay(pollingInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"No response from Copilot Studio within {_options.ResponseTimeoutSeconds} seconds.");
    }

    /// <summary>
    /// Scans activities for an OAuthCard attachment, extracting the connection name
    /// and optional token exchange resource ID.
    /// </summary>
    private OAuthCardInfo? ExtractOAuthCardInfo(JsonArray activities, string senderId)
    {
        foreach (var activity in activities)
        {
            var fromId = activity?["from"]?["id"]?.GetValue<string>();
            if (fromId == senderId) continue;

            var type = activity?["type"]?.GetValue<string>();
            var attachments = activity?["attachments"]?.AsArray();

            _logger.LogDebug("Checking activity: type={Type}, from={From}, attachmentCount={Count}",
                type, fromId, attachments?.Count ?? 0);

            if (attachments is null) continue;

            foreach (var attachment in attachments)
            {
                var contentType = attachment?["contentType"]?.GetValue<string>();
                _logger.LogDebug("  Attachment contentType: {ContentType}", contentType);

                if (contentType != "application/vnd.microsoft.card.oauth") continue;

                var card = attachment?["content"];
                var connectionName = card?["connectionName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(connectionName)) continue;

                var exchangeResourceId = card?["tokenExchangeResource"]?["id"]?.GetValue<string>();

                _logger.LogInformation("Found OAuthCard: connection={Connection}, exchangeResourceId={Id}",
                    connectionName, exchangeResourceId ?? "(none)");
                return new OAuthCardInfo(connectionName, exchangeResourceId);
            }
        }
        return null;
    }

    private readonly record struct OAuthCardInfo(string ConnectionName, string? ExchangeResourceId);

    #endregion
}

/// <summary>
/// Exception thrown when SSO token exchange fails. Surfaces as a protocol-level
/// error rather than a fake bot response.
/// </summary>
public class SsoTokenExchangeException : Exception
{
    public SsoTokenExchangeException(string message) : base(message) { }
    public SsoTokenExchangeException(string message, Exception inner) : base(message, inner) { }
}
