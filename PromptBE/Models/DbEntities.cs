using System;
using System.Collections.Generic;

namespace PromptBE.Models;

public class Prompt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty; // Maps to "Prompt" column in DB
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    // Relationships
    public ICollection<TagPrompt> TagPrompts { get; set; } = new List<TagPrompt>();
    public ICollection<CategoryPrompt> CategoryPrompts { get; set; } = new List<CategoryPrompt>();
}

public class Tag
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;

    // Relationships
    public ICollection<TagPrompt> TagPrompts { get; set; } = new List<TagPrompt>();
}

public class TagPrompt
{
    public string PromptId { get; set; } = string.Empty;
    public Prompt Prompt { get; set; } = null!;

    public string TagId { get; set; } = string.Empty;
    public Tag Tag { get; set; } = null!;
}

public class Category
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;

    // Relationships
    public ICollection<CategoryPrompt> CategoryPrompts { get; set; } = new List<CategoryPrompt>();
}

public class CategoryPrompt
{
    public string PromptId { get; set; } = string.Empty;
    public Prompt Prompt { get; set; } = null!;

    public string CategoryId { get; set; } = string.Empty;
    public Category Category { get; set; } = null!;
}
