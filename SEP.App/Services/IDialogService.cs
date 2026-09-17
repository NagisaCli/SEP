using System.Collections.Generic;
using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>What a prompt dialog asks for; unused parts stay hidden.</summary>
public sealed class PromptRequest
{
    public string Title { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string OkLabel { get; init; } = string.Empty;
    /// <summary>Label + placeholder for a single-line text field; null hides it.</summary>
    public string? TextLabel { get; init; }
    public string? TextPlaceholder { get; init; }
    public string? TextInitial { get; init; }
    public int TextMaxLength { get; init; } = 120;
    public bool TextUpperCase { get; init; }
    /// <summary>Label for a password + confirmation pair; null hides them.</summary>
    public string? PasswordLabel { get; init; }
    /// <summary>Label + options for a choice list; null hides it.</summary>
    public string? ChoiceLabel { get; init; }
    public IReadOnlyList<string>? Choices { get; init; }
    public string? ChoiceInitial { get; init; }
    public bool Danger { get; init; }
}

public sealed record PromptResult(string Text, string Password, string? Choice);

/// <summary>Draft of a new project user collected by the user dialog.</summary>
public sealed record UserDraft(string Name, string Security, string? Password, string? Description, string? Team);

public interface IDialogService
{
    /// <summary>Yes/no question; <paramref name="danger"/> styles the confirm button as destructive.</summary>
    Task<bool> ConfirmAsync(string title, string message, string okLabel, bool danger = false);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Text / password / choice prompt; null when cancelled.</summary>
    Task<PromptResult?> PromptAsync(PromptRequest request);

    /// <summary>The "new project user" form; null when cancelled.</summary>
    Task<UserDraft?> NewUserAsync(IReadOnlyList<string> teams, string projectCode);

    /// <summary>Project details editor (name, category, status, owner, tags, description, notes); true when saved.</summary>
    Task<bool> EditProjectAsync(ProjectItem project);
    /// <summary>Batch editor for several projects; true when applied.</summary>
    Task<bool> EditProjectsAsync(IReadOnlyList<ProjectItem> projects);
    Task ManageCategoriesAsync();
    Task CreateProjectAsync();
    Task DecommissionProjectAsync(ProjectItem project);
    /// <summary>Folder picker; null when cancelled.</summary>
    string? PickFolder(string title, string? initial = null);
    /// <summary>File picker; null when cancelled.</summary>
    string? PickFile(string title, string filter, string? initial = null);
    string? PickSaveFile(string title, string filter, string suggestedName);
}
