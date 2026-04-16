using A2A.AspNetCore;
using CopilotStudioA2A.Services;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

// Bind Copilot Studio configuration
builder.Services.Configure<CopilotStudioOptions>(
    builder.Configuration.GetSection("CopilotStudio"));

// Register the Direct Line chat client that proxies to Copilot Studio
builder.Services.AddHttpClient<CopilotStudioChatClient>();
builder.Services.AddSingleton<IChatClient>(sp =>
    sp.GetRequiredService<CopilotStudioChatClient>());

// Register the A2A agent backed by Copilot Studio
var copilotAgent = builder.AddAIAgent("copilot-studio",
    instructions: "You are a proxy to a Copilot Studio agent. Forward all user messages and return responses.");

var app = builder.Build();

app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

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
