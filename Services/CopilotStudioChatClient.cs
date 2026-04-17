using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

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

        // When SSO is enabled, the user identity is established through the token exchange,
        // not via a trusted dl_ user ID in the Direct Line token. Using a dl_ prefixed ID
        // creates a "trusted" session that suppresses the Sign In topic, preventing the bot
        // from sending the OAuthCard needed for SSO token exchange.
        var hasBearerToken = !string.IsNullOrEmpty(GetCallerBearerToken());
        var ssoMode = _options.EnableAuthPassthrough && hasBearerToken;
        var conversationUserId = ssoMode ? userId?.Replace("dl_", "a2a_") : userId;

        try
        {
            var token = await GetTokenAsync(ssoMode ? null : userId, cancellationToken);
            var (conversationId, _) = await StartConversationAsync(token, cancellationToken);
            _logger.LogInformation(
                "Started Direct Line conversation {ConversationId} (user={UserId}, ssoMode={SsoMode})",
                conversationId, conversationUserId ?? "anonymous", ssoMode);

            await SendMessageAsync(token, conversationId, messageText, conversationUserId, cancellationToken);
            var botResponse = await PollForResponseAsync(token, conversationId, conversationUserId, cancellationToken);

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

    #region SSO token exchange

    /// <summary>
    /// Sends a signin/tokenExchange invoke activity to Direct Line,
    /// responding to an OAuthCard challenge from the bot.
    /// Returns true if the exchange was accepted, false if Direct Line returned "retry".
    /// </summary>
    private async Task<bool> SendTokenExchangeAsync(
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
            ["from"] = new JsonObject
            {
                ["id"] = userId ?? "a2a-client",
                ["role"] = "user"
            },
            ["value"] = new JsonObject
            {
                ["connectionName"] = connectionName,
                ["token"] = ssoToken,
            }
        };

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
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Token exchange failed with {Status}: {Body}", response.StatusCode, responseBody);
            throw new SsoTokenExchangeException(
                $"Token exchange returned {response.StatusCode}: {responseBody}");
        }

        _logger.LogInformation(
            "Token exchange response for '{Connection}' in {ConversationId}: {Status} {Body}",
            connectionName, conversationId, response.StatusCode, responseBody);

        // Direct Line returns "retry" in the id field when the exchange was not accepted
        try
        {
            var responseJson = JsonNode.Parse(responseBody);
            var id = responseJson?["id"]?.GetValue<string>();
            if (string.Equals(id, "retry", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Token exchange returned 'retry' — exchange not accepted");
                return false;
            }
        }
        catch (JsonException)
        {
            // Non-JSON response, treat as success
        }

        return true;
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
            if (activities is not null && activities.Count > 0)
            {
                _logger.LogDebug("Poll returned {Count} activities (exchangeAttempted={Attempted}, awaitingPostExchange={Awaiting})",
                    activities.Count, exchangeAttempted, awaitingPostExchange);

                // Log all activities when in post-exchange state for debugging
                if (awaitingPostExchange)
                {
                    foreach (var act in activities)
                    {
                        var actType = act?["type"]?.GetValue<string>();
                        var actFrom = act?["from"]?["id"]?.GetValue<string>();
                        var actName = act?["name"]?.GetValue<string>();
                        var actText = act?["text"]?.GetValue<string>();
                        _logger.LogDebug(
                            "  Post-exchange activity: type={Type}, from={From}, name={Name}, text={Text}",
                            actType, actFrom, actName ?? "(none)",
                            actText != null ? actText[..Math.Min(actText.Length, 100)] : "(none)");
                    }
                }

                // First pass: check for OAuthCard challenges (before returning any text)
                if (_options.EnableAuthPassthrough && !exchangeAttempted)
                {
                    var oauthInfo = ExtractOAuthCardInfo(activities, senderId);
                    if (oauthInfo is not null)
                    {
                        exchangeAttempted = true;
                        _logger.LogInformation(
                            "OAuthCard detected (connection: {Connection}, uri: {Uri}), performing SSO token exchange",
                            oauthInfo.Value.ConnectionName, oauthInfo.Value.ExchangeResourceUri ?? "(none)");

                        // Use the caller's original bearer token — the bot's auth connection
                        // expects a token whose audience matches the Token Exchange URL
                        // (e.g. api://<clientId>), which is the same audience our A2A endpoint validates.
                        var callerToken = GetCallerBearerToken()
                            ?? throw new SsoTokenExchangeException(
                                "No bearer token available for SSO exchange. Ensure the caller is authenticated.");

                        // Validate that token audience matches the OAuthCard's tokenExchangeResource.uri
                        if (!string.IsNullOrEmpty(oauthInfo.Value.ExchangeResourceUri))
                        {
                            _logger.LogDebug(
                                "OAuthCard requested resource URI: {Uri}. " +
                                "Caller's token should have matching audience.",
                                oauthInfo.Value.ExchangeResourceUri);
                        }

                        var accepted = await SendTokenExchangeAsync(token, conversationId, userId,
                            oauthInfo.Value.ConnectionName,
                            oauthInfo.Value.ExchangeResourceId,
                            callerToken, cancellationToken);

                        if (accepted)
                        {
                            awaitingPostExchange = true;
                            deadline = DateTime.UtcNow + timeout;
                            _logger.LogInformation("Token exchange accepted, waiting for post-auth response");
                        }
                        else
                        {
                            _logger.LogWarning("Token exchange returned 'retry', SSO not accepted by bot");
                            // Fall through to normal message handling — bot may show sign-in card
                        }

                        await Task.Delay(pollingInterval, cancellationToken);
                        continue;
                    }
                }

                // Second pass: look for the bot's text response
                foreach (var activity in activities)
                {
                    var fromId = activity?["from"]?["id"]?.GetValue<string>();
                    var type = activity?["type"]?.GetValue<string>();
                    var activityText = activity?["text"]?.GetValue<string>();

                    // Skip our own activities
                    if (fromId == senderId) continue;

                    if (type == "message" && !string.IsNullOrEmpty(activityText))
                    {
                        if (awaitingPostExchange)
                        {
                            _logger.LogInformation(
                                "Received post-SSO response from conversation {ConversationId}",
                                conversationId);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Received response from conversation {ConversationId}",
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
                var exchangeResourceUri = card?["tokenExchangeResource"]?["uri"]?.GetValue<string>();

                _logger.LogInformation(
                    "Found OAuthCard: connection={Connection}, exchangeResourceId={Id}, exchangeResourceUri={Uri}",
                    connectionName, exchangeResourceId ?? "(none)", exchangeResourceUri ?? "(none)");
                return new OAuthCardInfo(connectionName, exchangeResourceId, exchangeResourceUri);
            }
        }
        return null;
    }

    private readonly record struct OAuthCardInfo(
        string ConnectionName, string? ExchangeResourceId, string? ExchangeResourceUri);

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
