using A2A.AspNetCore;
using CopilotStudioA2A.Services;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

// Bind Copilot Studio configuration
builder.Services.Configure<CopilotStudioOptions>(
    builder.Configuration.GetSection("CopilotStudio"));

// Required for chat clients to access the authenticated user
builder.Services.AddHttpContextAccessor();

// Determine connection mode
var connectionMode = builder.Configuration.GetValue<ConnectionMode>("CopilotStudio:ConnectionMode");
var authEnabled = builder.Configuration.GetValue<bool>("CopilotStudio:EnableAuthPassthrough");

// SDK mode always requires authentication
var requireAuth = authEnabled || connectionMode == ConnectionMode.CopilotStudioSdk;

// Configure Entra ID bearer token auth when required
if (requireAuth)
{
    builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration, "CopilotStudio:AzureAd");
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("A2AAuth", policy => policy.RequireAuthenticatedUser());
}

// Register the appropriate chat client based on connection mode
if (connectionMode == ConnectionMode.CopilotStudioSdk)
{
    // Register named HttpClient for the Copilot Studio SDK
    builder.Services.AddHttpClient("copilot-studio-sdk");

    builder.Services.AddSingleton<CopilotStudioSdkChatClient>();
    builder.Services.AddSingleton<IChatClient>(sp =>
        sp.GetRequiredService<CopilotStudioSdkChatClient>());
}
else
{
    // Register the Direct Line chat client that proxies to Copilot Studio
    builder.Services.AddHttpClient<CopilotStudioChatClient>();
    builder.Services.AddSingleton<IChatClient>(sp =>
        sp.GetRequiredService<CopilotStudioChatClient>());
}

// Register the A2A agent backed by Copilot Studio
var copilotAgent = builder.AddAIAgent("copilot-studio",
    instructions: "You are a proxy to a Copilot Studio agent. Forward all user messages and return responses.");

var app = builder.Build();

app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();

if (requireAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();

    // Enforce auth on A2A POST requests (JSON-RPC handler).
    // The agent card (GET) stays public so clients can discover auth requirements.
    // We use middleware because MapA2A's return value doesn't reliably apply
    // RequireAuthorization to the internal JSON-RPC POST handler.
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/a2a/copilot-studio")
            && context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        await next();
    });
}

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", mode = connectionMode.ToString() }));

// Expose the Copilot Studio agent via A2A protocol
app.MapA2A(copilotAgent, path: "/a2a/copilot-studio", agentCard: new()
{
    Name = builder.Configuration["A2A:AgentName"] ?? "Copilot Studio Agent",
    Description = builder.Configuration["A2A:AgentDescription"]
        ?? "An A2A-compatible agent backed by Microsoft Copilot Studio.",
    Version = "1.0",
    Url = builder.Configuration["A2A:AgentUrl"] ?? "http://localhost:5173/a2a/copilot-studio",
    Capabilities = new A2A.AgentCapabilities
    {
        Streaming = false,
        PushNotifications = false,
        StateTransitionHistory = false,
        Extensions = new List<A2A.AgentExtension>()
    }
});

app.Run();
