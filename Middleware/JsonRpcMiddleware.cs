using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotStudioA2A.Middleware;

/// <summary>
/// Middleware that wraps/unwraps JSON-RPC 2.0 envelopes for A2A protocol compatibility.
/// </summary>
public class JsonRpcMiddleware
{
    private readonly RequestDelegate _next;

    public JsonRpcMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == HttpMethods.Post &&
            context.Request.ContentType?.Contains("application/json") == true)
        {
            context.Request.EnableBuffering();

            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            JsonNode? root = null;
            try { root = JsonNode.Parse(body); }
            catch { }

            if (root is JsonObject obj &&
                obj.ContainsKey("jsonrpc") &&
                obj["jsonrpc"]?.GetValue<string>() == "2.0" &&
                obj.ContainsKey("method") &&
                obj.ContainsKey("params"))
            {
                var id = obj["id"];
                var paramsNode = obj["params"];

                var newBodyBytes = JsonSerializer.SerializeToUtf8Bytes(paramsNode);
                var newBodyStream = new MemoryStream(newBodyBytes);
                context.Request.Body = newBodyStream;
                context.Request.ContentLength = newBodyBytes.Length;

                var originalBodyStream = context.Response.Body;
                using var responseBodyStream = new MemoryStream();
                context.Response.Body = responseBodyStream;

                try
                {
                    await _next(context);

                    responseBodyStream.Position = 0;
                    var responseContent = await new StreamReader(responseBodyStream).ReadToEndAsync();

                    JsonNode? resultNode = null;

                    // Handle SSE format (data: ...)
                    if (responseContent.TrimStart().StartsWith("data:"))
                    {
                        using var stringReader = new StringReader(responseContent);
                        string? line;
                        while ((line = await stringReader.ReadLineAsync()) != null)
                        {
                            if (line.StartsWith("data:"))
                            {
                                var jsonPart = line.Substring(5).Trim();
                                try
                                {
                                    resultNode = JsonNode.Parse(jsonPart);
                                    if (resultNode != null) break;
                                }
                                catch { }
                            }
                        }
                    }

                    if (resultNode == null && !string.IsNullOrWhiteSpace(responseContent))
                    {
                        try { resultNode = JsonNode.Parse(responseContent); }
                        catch { }
                    }

                    var rpcResponse = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id?.DeepClone(),
                    };

                    if (context.Response.StatusCode >= 200 && context.Response.StatusCode < 300)
                    {
                        rpcResponse["result"] = resultNode ?? responseContent;
                    }
                    else
                    {
                        rpcResponse["error"] = new JsonObject
                        {
                            ["code"] = context.Response.StatusCode,
                            ["message"] = "Error processing request",
                            ["data"] = resultNode ?? responseContent
                        };
                        context.Response.StatusCode = 200;
                    }

                    var rpcResponseBytes = JsonSerializer.SerializeToUtf8Bytes(rpcResponse);

                    context.Response.Body = originalBodyStream;
                    context.Response.ContentLength = rpcResponseBytes.Length;
                    context.Response.ContentType = "application/json";
                    await context.Response.Body.WriteAsync(rpcResponseBytes);
                    return;
                }
                catch (Exception ex)
                {
                    context.Response.Body = originalBodyStream;
                    var errorResponse = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id?.DeepClone(),
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32603,
                            ["message"] = "Internal error",
                            ["data"] = ex.Message
                        }
                    };
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(errorResponse);
                    return;
                }
            }
        }

        await _next(context);
    }
}
