using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Dapr;
using OpenAI;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configure OpenTelemetry Logging and Tracing
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeScopes = true;
    logging.AddConsoleExporter();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("PromptVectorIngestion"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

builder.Services.AddDaprClient();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Initialize Azure AI Search Index on startup
try
{
    var endpointStr = app.Configuration["SearchService:Endpoint"];
    var apiKey = app.Configuration["SearchService:ApiKey"];
    var indexName = app.Configuration["SearchService:IndexName"] ?? "prompts-index";

    if (!string.IsNullOrEmpty(endpointStr) && endpointStr != "http://localhost:5000")
    {
        logger.LogInformation("Initializing Azure AI Search Index client for endpoint: {Endpoint}", endpointStr);
        var endpoint = new Uri(endpointStr);
        var credential = new AzureKeyCredential(apiKey ?? "");
        var indexClient = new SearchIndexClient(endpoint, credential);
        
        bool indexExists = false;
        try
        {
            var existingIndex = await indexClient.GetIndexAsync(indexName);
            if (existingIndex != null)
            {
                indexExists = true;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            indexExists = false;
        }

        if (!indexExists)
        {
            logger.LogInformation("Creating search index: {IndexName}...", indexName);
            var searchIndex = new SearchIndex(indexName)
            {
                Fields =
                {
                    new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
                    new SimpleField("promptId", SearchFieldDataType.String) { IsFilterable = true },
                    new SearchableField("title"),
                    new SearchableField("content"),
                    new SearchableField("category") { IsFilterable = true, IsFacetable = true },
                    new SearchField("tags", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsSearchable = true, IsFilterable = true, IsFacetable = true },
                    new SearchField("vector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                    {
                        VectorSearchDimensions = 1536,
                        VectorSearchProfileName = "my-vector-profile"
                    }
                },
                VectorSearch = new VectorSearch
                {
                    Algorithms =
                    {
                        new HnswAlgorithmConfiguration("my-hnsw-config")
                        {
                            Parameters = new HnswParameters
                            {
                                Metric = VectorSearchAlgorithmMetric.Cosine
                            }
                        }
                    },
                    Profiles =
                    {
                        new VectorSearchProfile("my-vector-profile", "my-hnsw-config")
                    }
                }
            };

            await indexClient.CreateIndexAsync(searchIndex);
            logger.LogInformation("Successfully created Search Index: {IndexName}", indexName);
        }
        else
        {
            logger.LogInformation("Search Index {IndexName} already exists.", indexName);
        }
    }
    else
    {
        logger.LogWarning("SearchService:Endpoint is empty or placeholder. Skipping search index initialization.");
    }
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to initialize Azure AI Search index. Startup will proceed.");
}

app.UseCloudEvents();
app.MapSubscribeHandler();

app.MapPost("/api/ingest-prompt", [Topic("pubsub", "prompts")] async (
    [FromBody] PromptEventPayload payload,
    IConfiguration configuration,
    ILoggerFactory loggerFactory) =>
{
    var endpointStr = configuration["SearchService:Endpoint"];
    var apiKey = configuration["SearchService:ApiKey"];
    var indexName = configuration["SearchService:IndexName"] ?? "prompts-index";
    
    var log = loggerFactory.CreateLogger("PromptVectorIngestion");

    if (payload == null)
    {
        log.LogWarning("Received invalid prompt event payload.");
        return Results.BadRequest("Invalid payload");
    }

    log.LogInformation("Received ingestion event for Prompt ID: {Id}, Name: {Title}", payload.Id, payload.Title);

    // Weight important fields more heavily by repeating them with labels
    var titlePart = $"Title: {payload.Title}";
    var categoryPart = !string.IsNullOrWhiteSpace(payload.Category) ? $"Category: {payload.Category}" : "";
    var tagsPart = payload.Tags != null && payload.Tags.Length > 0 ? $"Tags: {string.Join(", ", payload.Tags)}" : "";
    var weightedTitle = string.Join(" ", Enumerable.Repeat(titlePart, 3));
    var weightedCategory = string.Join(" ", Enumerable.Repeat(categoryPart, 2));
    var weightedTags = string.Join(" ", Enumerable.Repeat(tagsPart, 2));
    var textToChunk = $"{weightedTitle}\n{weightedCategory}\n{weightedTags}\n{payload.Description}\n{payload.PromptText}";
    var chunks = ChunkText(textToChunk, 500);

    var docs = new List<PromptSearchDocument>();
    int chunkIdx = 0;
    foreach (var chunk in chunks)
    {
        var embedding = await GenerateEmbeddingAsync(chunk, configuration, log);
        docs.Add(new PromptSearchDocument
        {
            Id = $"{payload.Id}_{chunkIdx}",
            PromptId = payload.Id,
            Title = payload.Title,
            Content = chunk,
            Category = payload.Category,
            Tags = payload.Tags != null ? new List<string>(payload.Tags) : new List<string>(),
            Vector = embedding
        });
        chunkIdx++;
    }

    if (!string.IsNullOrEmpty(endpointStr) && endpointStr != "http://localhost:5000")
    {
        try
        {
            var searchClient = new SearchClient(new Uri(endpointStr), indexName, new AzureKeyCredential(apiKey ?? ""));

            // Delete existing chunks for this prompt to prevent orphans
            try
            {
                var searchOptions = new SearchOptions();
                searchOptions.Select.Add("id");
                searchOptions.Filter = $"promptId eq '{payload.Id}'";
                
                var existingDocs = await searchClient.SearchAsync<SearchDocument>("*", searchOptions);
                var idsToDelete = new List<string>();
                await foreach (var doc in existingDocs.Value.GetResultsAsync())
                {
                    if (doc.Document.TryGetValue("id", out var idVal) && idVal != null)
                    {
                        var idStr = idVal.ToString();
                        if (!string.IsNullOrEmpty(idStr))
                        {
                            idsToDelete.Add(idStr);
                        }
                    }
                }
                if (idsToDelete.Count > 0)
                {
                    log.LogInformation("Deleting {Count} old chunks for Prompt ID: {Id} to prevent orphans", idsToDelete.Count, payload.Id);
                    await searchClient.DeleteDocumentsAsync("id", idsToDelete);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to check or delete old chunks before ingestion. Proceeding with upload.");
            }

            log.LogInformation("Uploading {Count} chunks to Azure AI Search for Prompt: {Id}", docs.Count, payload.Id);
            await searchClient.UploadDocumentsAsync(docs);
            log.LogInformation("Successfully completed ingestion for Prompt ID: {Id}", payload.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error uploading documents to search service.");
        }
    }
    else
    {
        log.LogWarning("Azure AI Search not configured. Simulated ingestion of {Count} chunks.", docs.Count);
    }

    return Results.Ok();
});

app.MapPost("/api/reset-index", async (IConfiguration configuration, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("PromptVectorIngestion");
    var endpointStr = configuration["SearchService:Endpoint"];
    var apiKey = configuration["SearchService:ApiKey"];
    var indexName = configuration["SearchService:IndexName"] ?? "prompts-index";

    log.LogInformation("Resetting search index: {IndexName}", indexName);

    if (string.IsNullOrEmpty(endpointStr) || endpointStr == "http://localhost:5000")
    {
        log.LogWarning("Search service not configured. Reset simulated.");
        return Results.Ok(new { Message = "Simulated index reset." });
    }

    try
    {
        var endpoint = new Uri(endpointStr);
        var credential = new AzureKeyCredential(apiKey ?? "");
        var indexClient = new SearchIndexClient(endpoint, credential);

        // Delete if exists
        try
        {
            await indexClient.DeleteIndexAsync(indexName);
            log.LogInformation("Deleted index: {IndexName}", indexName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Ignore
        }

        // Recreate index
        var searchIndex = new SearchIndex(indexName)
        {
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
                new SimpleField("promptId", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("title"),
                new SearchableField("content"),
                new SearchableField("category") { IsFilterable = true, IsFacetable = true },
                new SearchField("tags", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsSearchable = true, IsFilterable = true, IsFacetable = true },
                new SearchField("vector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    VectorSearchDimensions = 1536,
                    VectorSearchProfileName = "my-vector-profile"
                }
            },
            VectorSearch = new VectorSearch
            {
                Algorithms =
                {
                    new HnswAlgorithmConfiguration("my-hnsw-config")
                    {
                        Parameters = new HnswParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.Cosine
                        }
                    }
                },
                Profiles =
                {
                    new VectorSearchProfile("my-vector-profile", "my-hnsw-config")
                }
            }
        };

        await indexClient.CreateIndexAsync(searchIndex);
        log.LogInformation("Recreated index: {IndexName}", indexName);
        return Results.Ok(new { Message = "Index reset and recreated successfully." });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to reset search index.");
        return Results.Problem("Failed to reset search index: " + ex.Message);
    }
});

app.MapPost("/api/bulk-ingest", async (
    [FromBody] List<PromptEventPayload> payloads,
    IConfiguration configuration,
    ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("PromptVectorIngestion");
    var endpointStr = configuration["SearchService:Endpoint"];
    var apiKey = configuration["SearchService:ApiKey"];
    var indexName = configuration["SearchService:IndexName"] ?? "prompts-index";

    log.LogInformation("Starting bulk ingestion of {Count} prompts...", payloads.Count);

    var docs = new List<PromptSearchDocument>();
    foreach (var payload in payloads)
    {
        var titlePart = $"Title: {payload.Title}";
        var categoryPart = !string.IsNullOrWhiteSpace(payload.Category) ? $"Category: {payload.Category}" : "";
        var tagsPart = payload.Tags != null && payload.Tags.Length > 0 ? $"Tags: {string.Join(", ", payload.Tags)}" : "";
        var weightedTitle = string.Join(" ", Enumerable.Repeat(titlePart, 3));
        var weightedCategory = string.Join(" ", Enumerable.Repeat(categoryPart, 2));
        var weightedTags = string.Join(" ", Enumerable.Repeat(tagsPart, 2));
        var textToChunk = $"{weightedTitle}\n{weightedCategory}\n{weightedTags}\n{payload.Description}\n{payload.PromptText}";
        var chunks = ChunkText(textToChunk, 500);

        int chunkIdx = 0;
        foreach (var chunk in chunks)
        {
            var embedding = await GenerateEmbeddingAsync(chunk, configuration, log);
            docs.Add(new PromptSearchDocument
            {
                Id = $"{payload.Id}_{chunkIdx}",
                PromptId = payload.Id,
                Title = payload.Title,
                Content = chunk,
                Category = payload.Category,
                Tags = payload.Tags != null ? new List<string>(payload.Tags) : new List<string>(),
                Vector = embedding
            });
            chunkIdx++;
        }
    }

    if (string.IsNullOrEmpty(endpointStr) || endpointStr == "http://localhost:5000")
    {
        log.LogWarning("Search service not configured. Simulated bulk ingestion of {Count} chunks.", docs.Count);
        return Results.Ok(new { Message = $"Simulated bulk ingestion of {docs.Count} chunks." });
    }

    try
    {
        var searchClient = new SearchClient(new Uri(endpointStr), indexName, new AzureKeyCredential(apiKey ?? ""));
        
        // Upload documents in batches of 1000
        const int batchSize = 1000;
        for (int i = 0; i < docs.Count; i += batchSize)
        {
            var batch = docs.Skip(i).Take(batchSize).ToList();
            log.LogInformation("Uploading batch of {Count} chunks (progress {Index}/{Total})...", batch.Count, i, docs.Count);
            await searchClient.UploadDocumentsAsync(batch);
        }

        log.LogInformation("Successfully completed bulk ingestion of {Count} prompts ({ChunkCount} chunks).", payloads.Count, docs.Count);
        return Results.Ok(new { Message = $"Successfully indexed {docs.Count} chunks." });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to upload bulk documents to search service.");
        return Results.Problem("Failed to upload bulk documents: " + ex.Message);
    }
});

app.Run();

static List<string> ChunkText(string text, int maxChunkLength)
{
    if (string.IsNullOrEmpty(text)) return new List<string>();
    var chunks = new List<string>();
    for (int i = 0; i < text.Length; i += maxChunkLength)
    {
        chunks.Add(text.Substring(i, Math.Min(maxChunkLength, text.Length - i)));
    }
    return chunks;
}

static async Task<float[]> GenerateEmbeddingAsync(string text, IConfiguration config, ILogger log)
{
    var endpoint = config["AzureOpenAI:Endpoint"];
    var apiKey = config["AzureOpenAI:ApiKey"];
    var model = config["AzureOpenAI:EmbeddingDeploymentName"] ?? "text-embedding-3-small";

    if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey))
    {
        try
        {
            var openAiClient = new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
            var embeddingClient = openAiClient.GetEmbeddingClient(model);
            var result = await embeddingClient.GenerateEmbeddingAsync(text);
            return result.Value.ToFloats().ToArray();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Real embedding failed (model={Model}). Using stable deterministic fallback.", model);
        }
    }
    else
    {
        log.LogDebug("AzureOpenAI not configured — using stable deterministic embedding fallback.");
    }

    return GenerateStableMockEmbedding(text);
}

// SHA-256-seeded deterministic fallback: consistent across all processes (unlike GetHashCode)
static float[] GenerateStableMockEmbedding(string text)
{
    var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
    int seed = BitConverter.ToInt32(hashBytes, 0);
    var rand = new Random(seed);
    float[] vector = new float[1536];
    double sumOfSquares = 0;
    for (int i = 0; i < 1536; i++)
    {
        vector[i] = (float)(rand.NextDouble() * 2.0 - 1.0);
        sumOfSquares += vector[i] * vector[i];
    }
    double length = Math.Sqrt(sumOfSquares);
    if (length > 0)
        for (int i = 0; i < 1536; i++)
            vector[i] = (float)(vector[i] / length);
    return vector;
}

public class PromptEventPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("promptText")]
    public string PromptText { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = Array.Empty<string>();
}

public class PromptSearchDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("promptId")]
    public string PromptId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("vector")]
    public float[] Vector { get; set; } = Array.Empty<float>();
}
