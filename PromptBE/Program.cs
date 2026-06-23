using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PromptBE.Data;
using PromptBE.Models;

var builder = WebApplication.CreateBuilder(args);

// Add CORS services
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

// Configure EF Core DbContext
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? builder.Configuration["ConnectionStrings:DefaultConnection"];
builder.Services.AddDbContext<PromptDbContext>(options =>
    options.UseSqlServer(connectionString));

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AllowAll");

// Apply database migrations on startup with a retry mechanism
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    var dbContext = services.GetRequiredService<PromptDbContext>();
    
    for (int i = 0; i < 6; i++)
    {
        try
        {
            logger.LogInformation("Applying migrations to database... (Attempt {Attempt})", i + 1);
            dbContext.Database.Migrate();
            logger.LogInformation("Database migrated successfully.");
            
            logger.LogInformation("Seeding database...");
            await SeedDataAsync(dbContext);
            logger.LogInformation("Seeding completed.");
            break;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while migrating or seeding the database.");
            if (i == 5) throw;
            logger.LogInformation("Waiting 5 seconds before retrying...");
            await Task.Delay(5000);
        }
    }
}

// API Endpoints
app.MapGet("/api/prompts", async (PromptDbContext db) =>
{
    var dbPrompts = await db.Prompts
        .Include(p => p.TagPrompts).ThenInclude(tp => tp.Tag)
        .Include(p => p.CategoryPrompts).ThenInclude(cp => cp.Category)
        .ToListAsync();

    var dtos = dbPrompts.Select(p => new PromptDto(
        p.Id,
        p.Name,
        p.Description,
        p.PromptText,
        p.CategoryPrompts.FirstOrDefault()?.Category?.Name ?? "General",
        p.TagPrompts.Select(tp => tp.Tag.Name).ToArray(),
        p.CreatedOn
    ));

    return Results.Ok(dtos);
})
.WithName("GetPrompts");

app.MapPost("/api/prompts", async ([FromBody] PromptDto newPrompt, PromptDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(newPrompt.Title) || string.IsNullOrWhiteSpace(newPrompt.PromptText))
    {
        return Results.BadRequest("Title and Prompt Text are required.");
    }

    var categoryName = string.IsNullOrWhiteSpace(newPrompt.Category) ? "General" : newPrompt.Category.Trim();
    var dbCategory = await db.Categories.FirstOrDefaultAsync(c => c.Name == categoryName);
    if (dbCategory == null)
    {
        dbCategory = new Category { Name = categoryName };
        db.Categories.Add(dbCategory);
    }

    var promptEntity = new Prompt
    {
        Id = Guid.NewGuid().ToString(),
        Name = newPrompt.Title.Trim(),
        Description = newPrompt.Description?.Trim() ?? "",
        PromptText = newPrompt.PromptText,
        CreatedOn = DateTime.UtcNow
    };

    db.Prompts.Add(promptEntity);

    // Link Category
    db.CategoryPrompts.Add(new CategoryPrompt
    {
        Prompt = promptEntity,
        Category = dbCategory
    });

    // Link Tags
    if (newPrompt.Tags != null)
    {
        foreach (var tagName in newPrompt.Tags)
        {
            var cleanTagName = tagName.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(cleanTagName)) continue;

            var dbTag = await db.Tags.FirstOrDefaultAsync(t => t.Name == cleanTagName);
            if (dbTag == null)
            {
                dbTag = new Tag { Name = cleanTagName };
                db.Tags.Add(dbTag);
            }

            db.TagPrompts.Add(new TagPrompt
            {
                Prompt = promptEntity,
                Tag = dbTag
            });
        }
    }

    await db.SaveChangesAsync();

    var responseDto = new PromptDto(
        promptEntity.Id,
        promptEntity.Name,
        promptEntity.Description,
        promptEntity.PromptText,
        categoryName,
        newPrompt.Tags ?? Array.Empty<string>(),
        promptEntity.CreatedOn
    );

    return Results.Created($"/api/prompts/{promptEntity.Id}", responseDto);
})
.WithName("CreatePrompt");

app.Run();

// Seeding Helper (must precede type declarations in top-level statements)
static async Task SeedDataAsync(PromptDbContext db)
{
    if (await db.Prompts.AnyAsync()) return;

    var codingCat = new Category { Name = "Coding" };
    var creativeCat = new Category { Name = "Creative" };
    var analysisCat = new Category { Name = "Analysis" };
    var marketingCat = new Category { Name = "Marketing" };

    db.Categories.AddRange(codingCat, creativeCat, analysisCat, marketingCat);

    var tags = new Dictionary<string, Tag>();
    var getOrCreateTag = (string name) =>
    {
        if (!tags.TryGetValue(name, out var tag))
        {
            tag = new Tag { Name = name };
            tags[name] = tag;
            db.Tags.Add(tag);
        }
        return tag;
    };

    // Prompt 1
    var p1 = new Prompt
    {
        Name = "Developer Pair Programmer",
        Description = "Acts as an expert software engineer guiding you through design, implementation, and code reviews.",
        PromptText = "You are an expert software engineer. We are pair programming to solve a task. Please analyze my code, point out any bugs, recommend performance optimizations, and suggest clean architectural designs. Keep explanations concise.",
        CreatedOn = DateTime.UtcNow
    };
    db.Prompts.Add(p1);
    db.CategoryPrompts.Add(new CategoryPrompt { Prompt = p1, Category = codingCat });
    foreach (var t in new[] { "dotnet", "architecture", "refactoring" })
    {
        db.TagPrompts.Add(new TagPrompt { Prompt = p1, Tag = getOrCreateTag(t) });
    }

    // Prompt 2
    var p2 = new Prompt
    {
        Name = "Creative Story Architect",
        Description = "Brainstorms plot structures, character arcs, and worldbuilding ideas.",
        PromptText = "You are a creative writer and story architect. Help me develop a compelling narrative arc, flesh out complex character motivations, and construct immersive fantasy or sci-fi world details. Use standard narrative structures like the Hero's Journey.",
        CreatedOn = DateTime.UtcNow
    };
    db.Prompts.Add(p2);
    db.CategoryPrompts.Add(new CategoryPrompt { Prompt = p2, Category = creativeCat });
    foreach (var t in new[] { "writing", "storytelling", "brainstorming" })
    {
        db.TagPrompts.Add(new TagPrompt { Prompt = p2, Tag = getOrCreateTag(t) });
    }

    // Prompt 3
    var p3 = new Prompt
    {
        Name = "Data Summarizer & Analyzer",
        Description = "Distills large bodies of text or logs into digestible, actionable summaries.",
        PromptText = "You are a senior data analyst. Please read the provided text or dataset. Extract key insights, identify notable trends or patterns, highlight potential areas of concern, and summarize the overall findings in a clean markdown table.",
        CreatedOn = DateTime.UtcNow
    };
    db.Prompts.Add(p3);
    db.CategoryPrompts.Add(new CategoryPrompt { Prompt = p3, Category = analysisCat });
    foreach (var t in new[] { "data", "summarization", "markdown" })
    {
        db.TagPrompts.Add(new TagPrompt { Prompt = p3, Tag = getOrCreateTag(t) });
    }

    // Prompt 4
    var p4 = new Prompt
    {
        Name = "Marketing Copy Generator",
        Description = "Drafts engaging social media, email campaigns, and ad headlines.",
        PromptText = "You are a conversion copywriter. Write a series of 3 engaging ad hooks and a follow-up email campaign for a new SaaS product launch. Focus on benefits over features, and keep the tone conversational and persuasive.",
        CreatedOn = DateTime.UtcNow
    };
    db.Prompts.Add(p4);
    db.CategoryPrompts.Add(new CategoryPrompt { Prompt = p4, Category = marketingCat });
    foreach (var t in new[] { "copywriting", "saas", "social-media" })
    {
        db.TagPrompts.Add(new TagPrompt { Prompt = p4, Tag = getOrCreateTag(t) });
    }

    await db.SaveChangesAsync();
}

// API DTO for compatibility with Blazor UI (must be at the very bottom)
public record PromptDto(
    string Id,
    string Title,
    string Description,
    string PromptText,
    string Category,
    string[] Tags,
    DateTime CreatedOn
);
