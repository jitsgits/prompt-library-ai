using System;

namespace PromptUI.Models;

public record Prompt(
    string Id,
    string Title,
    string Description,
    string PromptText,
    string Category,
    string[] Tags,
    DateTime CreatedOn
);
