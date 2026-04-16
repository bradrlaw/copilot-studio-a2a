using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CopilotStudioA2A.Services;

/// <summary>
/// IChatClient implementation that proxies chat requests to a Copilot Studio
/// agent via the Bot Framework Direct Line API.
/// </summary>
public class CopilotStudioChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly CopilotStudioOptions _options;
    private readonly ILogger<CopilotStudioChatClient> _logger;

    public CopilotStudioChatClient(
        HttpClient httpClient,
        IOptions<CopilotStudioOptions> options,
        ILogger<CopilotStudioChatClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
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

        try
        {
            var token = await GetTokenAsync(cancellationToken);
            var (conversationId, _) = await StartConversationAsync(token, cancellationToken);
            _logger.LogInformation("Started Direct Line conversation {ConversationId}", conversationId);

            await SendMessageAsync(token, conversationId, messageText, cancellationToken);
            var botResponse = await PollForResponseAsync(token, conversationId, cancellationToken);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, botResponse));
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
        // Direct Line doesn't natively support streaming — return full response as single update
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

    #region Direct Line helpers

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_options.TokenEndpoint))
        {
            var tokenResponse = await _httpClient.GetAsync(_options.TokenEndpoint, cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();
            var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
            return tokenJson?["token"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Token endpoint did not return a token.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.DirectLineEndpoint}/tokens/generate");
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
        string token, string conversationId, string text, CancellationToken cancellationToken)
    {
        var activity = new
        {
            type = "message",
            from = new { id = "a2a-client" },
            text
        };

        var content = new StringContent(
            JsonSerializer.Serialize(activity),
            Encoding.UTF8,
            "application/json");

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

    private async Task<string> PollForResponseAsync(
        string token, string conversationId, CancellationToken cancellationToken)
    {
        string? watermark = null;
        var timeout = TimeSpan.FromSeconds(_options.ResponseTimeoutSeconds);
        var pollingInterval = TimeSpan.FromMilliseconds(_options.PollingIntervalMs);
        var deadline = DateTime.UtcNow + timeout;

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
                foreach (var activity in activities)
                {
                    var fromId = activity?["from"]?["id"]?.GetValue<string>();
                    var type = activity?["type"]?.GetValue<string>();
                    var text = activity?["text"]?.GetValue<string>();

                    if (type == "message" && fromId != "a2a-client" && !string.IsNullOrEmpty(text))
                    {
                        _logger.LogInformation("Received response from conversation {ConversationId}", conversationId);
                        return text;
                    }
                }
            }

            await Task.Delay(pollingInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"No response from Copilot Studio within {_options.ResponseTimeoutSeconds} seconds.");
    }

    #endregion
}
