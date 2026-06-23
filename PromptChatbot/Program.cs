using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configure CORS
var allowedOrigins = builder.Configuration["AllowedOrigins"]?.Split(',') 
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        if (allowedOrigins.Length > 0 && !string.IsNullOrWhiteSpace(allowedOrigins[0]))
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

// Configure OpenTelemetry Logging and Tracing
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeScopes = true;
    logging.AddConsoleExporter();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("PromptChatbot"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

app.UseCors("AllowAll");

var logger = app.Services.GetRequiredService<ILogger<Program>>();

app.MapPost("/api/chat", async (
    [FromBody] ChatRequest request,
    HttpContext httpContext,
    IConfiguration configuration,
    ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("PromptChatbot");
    
    if (request == null || string.IsNullOrWhiteSpace(request.Message))
    {
        httpContext.Response.StatusCode = 400;
        await httpContext.Response.WriteAsync("Message is required.");
        return;
    }

    log.LogInformation("Received chat query: {Query}", request.Message);

    // Set response headers for streaming
    httpContext.Response.ContentType = "text/plain";
    
    // 1. Load grounding prompt file
    string groundingPrompt = "";
    try
    {
        var path = Path.Combine(app.Environment.ContentRootPath, "grounding.txt");
        if (File.Exists(path))
        {
            groundingPrompt = await File.ReadAllTextAsync(path);
        }
        else
        {
            groundingPrompt = "You are a prompt engineering chatbot. Answer the query using the context: {context}";
        }
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Failed to read grounding.txt. Using default template.");
        groundingPrompt = "You are a prompt engineering chatbot. Answer the query using the context: {context}";
    }

    // 2. Query Azure AI Search for relevant context
    var searchEndpoint = configuration["SearchService:Endpoint"];
    var searchApiKey = configuration["SearchService:ApiKey"];
    var indexName = configuration["SearchService:IndexName"] ?? "prompts-index";

    var retrievedChunks = new List<string>();

    if (!string.IsNullOrEmpty(searchEndpoint) && searchEndpoint != "http://localhost:5000")
    {
        try
        {
            log.LogInformation("Connecting to Azure AI Search to fetch relevant chunks...");
            var searchClient = new SearchClient(new Uri(searchEndpoint), indexName, new AzureKeyCredential(searchApiKey ?? ""));

            // Generate mock embedding aligned with the search index mock generator
            var queryVector = GenerateMockEmbedding(request.Message);
            var searchOptions = new SearchOptions();
            
            // Perform vectorized search query
            var vectorQuery = new VectorizedQuery(queryVector)
            {
                KNearestNeighborsCount = 3
            };
            vectorQuery.Fields.Add("vector");
            
            searchOptions.VectorSearch = new VectorSearchOptions
            {
                Queries = { vectorQuery }
            };
            
            searchOptions.Select.Add("title");
            searchOptions.Select.Add("content");
            searchOptions.Select.Add("category");
            
            var searchResult = await searchClient.SearchAsync<SearchDocument>("*", searchOptions);
            await foreach (var result in searchResult.Value.GetResultsAsync())
            {
                if (result.Document.TryGetValue("content", out var contentVal) && contentVal != null)
                {
                    var title = result.Document.TryGetValue("title", out var titleVal) ? titleVal.ToString() : "Untitled";
                    retrievedChunks.Add($"[{title}]: {contentVal}");
                }
            }

            log.LogInformation("Retrieved {Count} relevant chunks from Search.", retrievedChunks.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to retrieve context chunks from Azure AI Search.");
        }
    }
    else
    {
        log.LogWarning("Search service not configured. Bypassing Search Service context retrieval.");
    }

    // Embed retrieved context into grounding prompt
    var contextText = retrievedChunks.Count > 0 
        ? string.Join("\n\n", retrievedChunks)
        : "No matching prompts found in the library database.";
    
    groundingPrompt = groundingPrompt.Replace("{context}", contextText);

    // 3. Connect to Azure OpenAI and stream response
    var openAiEndpoint = configuration["AzureOpenAI:Endpoint"];
    var openAiApiKey = configuration["AzureOpenAI:ApiKey"];
    var deploymentName = configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4o-mini";

    if (!string.IsNullOrEmpty(openAiEndpoint) && !string.IsNullOrEmpty(openAiApiKey))
    {
        try
        {
            log.LogInformation("Connecting to LLM Endpoint: {Endpoint}...", openAiEndpoint);
            var openAiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(openAiApiKey), new OpenAIClientOptions { Endpoint = new Uri(openAiEndpoint) });
            var chatClient = openAiClient.GetChatClient(deploymentName);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(groundingPrompt)
            };

            // Add history
            if (request.History != null)
            {
                foreach (var hist in request.History)
                {
                    if (hist.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Add(new UserChatMessage(hist.Content));
                    }
                    else if (hist.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Add(new AssistantChatMessage(hist.Content));
                    }
                }
            }

            // Add user message
            messages.Add(new UserChatMessage(request.Message));

            // Stream response
            var responseStream = chatClient.CompleteChatStreamingAsync(messages);
            await foreach (var update in responseStream)
            {
                if (update.ContentUpdate != null)
                {
                    foreach (var part in update.ContentUpdate)
                    {
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            await httpContext.Response.WriteAsync(part.Text);
                            await httpContext.Response.Body.FlushAsync();
                        }
                    }
                }
            }
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Azure OpenAI execution failed. Bypassing to simulated streaming response...");
        }
    }

    // 4. Simulated streaming response fallback
    log.LogInformation("Generating simulated chatbot streaming response...");
    var simulatedText = GenerateSimulatedResponse(request.Message, retrievedChunks);
    
    // Split into small pieces to simulate a typing effect
    int index = 0;
    while (index < simulatedText.Length)
    {
        int size = Math.Min(new Random().Next(4, 12), simulatedText.Length - index);
        var chunk = simulatedText.Substring(index, size);
        await httpContext.Response.WriteAsync(chunk);
        await httpContext.Response.Body.FlushAsync();
        index += size;
        await Task.Delay(new Random().Next(20, 50));
    }
});

app.Run();

static float[] GenerateMockEmbedding(string text)
{
    float[] vector = new float[1536];
    int seed = text.GetHashCode();
    var rand = new Random(seed);
    double sumOfSquares = 0;
    for (int i = 0; i < 1536; i++)
    {
        vector[i] = (float)(rand.NextDouble() * 2.0 - 1.0);
        sumOfSquares += vector[i] * vector[i];
    }
    double length = Math.Sqrt(sumOfSquares);
    if (length > 0)
    {
        for (int i = 0; i < 1536; i++)
        {
            vector[i] = (float)(vector[i] / length);
        }
    }
    return vector;
}

static string GenerateSimulatedResponse(string query, List<string> contextChunks)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("### 🤖 GPT-4o-mini (Simulated Grounded Response)");
    sb.AppendLine();
    sb.AppendLine($"*Analyzed search index context for: \"{query}\"*");
    sb.AppendLine();
    
    if (contextChunks.Count > 0)
    {
        sb.AppendLine("I found some relevant prompt context in your **Azure AI Search** library index:");
        sb.AppendLine();
        foreach (var chunk in contextChunks)
        {
            sb.AppendLine($"> 📄 **{chunk}**");
            sb.AppendLine();
        }
        sb.AppendLine("To leverage these prompts for your task, I suggest utilizing the formatting patterns above and refining details such as target tone or system instructions.");
    }
    else
    {
        sb.AppendLine("No matching prompt templates were found in your library index. Here are general prompt engineering guidelines instead:");
        sb.AppendLine();
        sb.AppendLine("1. **Specify Role**: Start by assigning a persona, e.g. *\"You are a Senior .NET Architect...\"*");
        sb.AppendLine("2. **State Output Format**: Instruct the model on formatting, e.g. *\"Format response as a markdown table...\"*");
        sb.AppendLine("3. **Delimiters**: Use clear boundaries like triple backticks to separate instruction from input data.");
    }
    
    return sb.ToString();
}

public class ChatRequest
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("history")]
    public List<ChatMessageDto>? History { get; set; }
}

public class ChatMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
