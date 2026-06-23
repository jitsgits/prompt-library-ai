using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using PromptUI.Models;

namespace PromptUI.Services;

public class ExplorerStateService
{
    private readonly HttpClient _http;

    public List<Prompt> Prompts { get; set; } = new();
    public bool IsLoading { get; set; } = true;
    public string? ErrorMessage { get; set; }

    public string SearchQuery { get; set; } = "";
    public string SelectedCategory { get; set; } = "All";
    public HashSet<string> SelectedTags { get; set; } = new();
    public HashSet<string> ExpandedCategories { get; set; } = new();
    public Prompt? SelectedPrompt { get; set; }

    public string? CopiedPromptId { get; set; }
    public bool ShowCreateModal { get; set; } = false;

    public event Action? OnStateChanged;

    public void OpenCreateModal()
    {
        ShowCreateModal = true;
        NotifyStateChanged();
    }

    public void CloseCreateModal()
    {
        ShowCreateModal = false;
        NotifyStateChanged();
    }

    public ExplorerStateService(HttpClient http)
    {
        _http = http;
    }

    public void NotifyStateChanged() => OnStateChanged?.Invoke();

    public async Task LoadPrompts()
    {
        IsLoading = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            var response = await _http.GetFromJsonAsync<List<Prompt>>("api/prompts");
            if (response != null)
            {
                Prompts = response;
                // Expand all categories by default on first load
                foreach (var cat in Categories)
                {
                    ExpandedCategories.Add(cat);
                }

                // Select first item by default if no selection exists
                if (SelectedPrompt == null && Prompts.Any())
                {
                    SelectedPrompt = Prompts.First();
                    SelectedCategory = SelectedPrompt.Category;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    public IEnumerable<string> Categories => Prompts
        .Select(p => p.Category)
        .Distinct()
        .OrderBy(c => c);

    public IEnumerable<string> AllTags => Prompts
        .SelectMany(p => p.Tags)
        .Distinct()
        .OrderBy(t => t);

    public bool HasActiveFilters => 
        !string.IsNullOrWhiteSpace(SearchQuery) || 
        SelectedCategory != "All" || 
        SelectedTags.Any() || 
        SelectedPrompt != null;

    public void ClearAllFilters()
    {
        SearchQuery = "";
        SelectedCategory = "All";
        SelectedTags.Clear();
        SelectedPrompt = null;
        NotifyStateChanged();
    }

    public void ToggleCategoryExpand(string category)
    {
        if (ExpandedCategories.Contains(category))
        {
            ExpandedCategories.Remove(category);
        }
        else
        {
            ExpandedCategories.Add(category);
        }
        
        SelectedCategory = category;
        SelectedPrompt = null;
        NotifyStateChanged();
    }

    public void SelectPrompt(Prompt prompt)
    {
        SelectedPrompt = prompt;
        SelectedCategory = prompt.Category;
        NotifyStateChanged();
    }

    public void DeselectPrompt()
    {
        SelectedPrompt = null;
        NotifyStateChanged();
    }

    public void ToggleTag(string tag)
    {
        if (SelectedTags.Contains(tag))
        {
            SelectedTags.Remove(tag);
        }
        else
        {
            SelectedTags.Add(tag);
        }
        NotifyStateChanged();
    }

    public void SetCategory(string category)
    {
        SelectedCategory = category;
        SelectedPrompt = null;
        NotifyStateChanged();
    }

    public IEnumerable<Prompt> GetPromptsForCategory(string category)
    {
        return Prompts.Where(p => 
        {
            if (p.Category != category) return false;
            if (SelectedTags.Any() && !SelectedTags.All(t => p.Tags.Contains(t))) return false;
            
            if (string.IsNullOrWhiteSpace(SearchQuery)) return true;
            var term = SearchQuery.ToLowerInvariant();
            return p.Title.ToLowerInvariant().Contains(term) ||
                   p.Description.ToLowerInvariant().Contains(term) ||
                   p.PromptText.ToLowerInvariant().Contains(term) ||
                   p.Tags.Any(t => t.ToLowerInvariant().Contains(term));
        });
    }

    public IEnumerable<Prompt> FilteredPrompts => Prompts.Where(p =>
    {
        var categoryMatches = SelectedCategory == "All" || p.Category == SelectedCategory;
        if (!categoryMatches) return false;

        if (SelectedTags.Any() && !SelectedTags.All(t => p.Tags.Contains(t))) return false;

        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;

        var term = SearchQuery.ToLowerInvariant();
        return p.Title.ToLowerInvariant().Contains(term) ||
               p.Description.ToLowerInvariant().Contains(term) ||
               p.PromptText.ToLowerInvariant().Contains(term) ||
               p.Tags.Any(t => t.ToLowerInvariant().Contains(term));
    });
}
