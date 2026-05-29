using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Components.Shared;
using Viamus.Doxie.Orchestrator.Theme;


namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class AgentDetail
{
    private static string CategoryIcon(string category)
    {
        if (Enum.TryParse<AgentCategory>(category, ignoreCase: true, out var cat))
        {
            return cat switch
            {
                AgentCategory.Doxie => Icons.Material.Filled.Pets,
                AgentCategory.Developer => Icons.Material.Filled.Code,
                AgentCategory.Fixer => Icons.Material.Filled.Build,
                AgentCategory.Builder => Icons.Material.Filled.School,
                AgentCategory.Inspector => Icons.Material.Filled.RuleFolder,
                AgentCategory.Connector => Icons.Material.Filled.Cable,
                _ => Icons.Material.Filled.SmartToy,
            };
        }
        return Icons.Material.Filled.BookmarkBorder;
    }

    private static string AgentIcon(AgentDescriptor agent) =>
        AgentIconPalette.Resolve(agent.Icon) ?? CategoryIcon(agent.DisplayCategory);

    // The built-in categories the user can pick from when re-categorising
    // an agent. Excludes Doxie and Connector â€” server-side enforces the
    // same rule (sealed categories), so the UI just hides the bad options
    // instead of letting the user click and get a 409.
    private static readonly string[] EditableCategories =
    [
        nameof(AgentCategory.Developer),
            nameof(AgentCategory.Inspector),
            nameof(AgentCategory.Builder),
            nameof(AgentCategory.Fixer),
            nameof(AgentCategory.Other),
        ];

    private bool _categoryUpdateInFlight;
    private bool _categoryMenuOpen;
    private bool _customCategoryEditorOpen;
    private string _customCategoryDraft = string.Empty;

    /// <summary>
    /// Toggles the category-edit menu open/closed. Wired on the chip's
    /// OnClick because MudBlazor 9.4's ActivatorContent doesn't reliably
    /// propagate clicks from a child MudChip â€” the chip's own pointer
    /// handling intercepts. Controlled-open via @bind-Open works
    /// independently.
    /// </summary>
    private void ToggleCategoryMenu()
    {
        if (_categoryUpdateInFlight) return;
        _categoryMenuOpen = !_categoryMenuOpen;
    }

    private bool CustomCategoryDraftValid =>
        !string.IsNullOrWhiteSpace(_customCategoryDraft)
        && AgentCategoryPalette.CustomLabelPattern.IsMatch(_customCategoryDraft.Trim());

    private void OpenCustomCategoryEditor()
    {
        _customCategoryDraft = string.Empty;
        _customCategoryEditorOpen = true;
        // Close the menu â€” with controlled @bind-Open MudMenu doesn't
        // auto-close on item click.
        _categoryMenuOpen = false;
    }

    private void CancelCustomCategoryEditor()
    {
        _customCategoryEditorOpen = false;
        _customCategoryDraft = string.Empty;
    }

    private async Task ApplyCustomCategory()
    {
        if (!CustomCategoryDraftValid) return;
        var label = _customCategoryDraft.Trim();
        _customCategoryEditorOpen = false;
        await UpdateCategoryAsync(label);
    }

    private async Task OpenIconPickerAsync()
    {
        if (_agent is null || _categoryUpdateInFlight) return;
        var dialog = await DialogService.ShowAsync<AgentIconPickerDialog>(
            "Choose agent icon",
            new DialogParameters
            {
                [nameof(AgentIconPickerDialog.Title)] = $"Choose icon for {_agent.Name}",
                [nameof(AgentIconPickerDialog.CurrentIcon)] = _agent.Icon,
            },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseButton = true });
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await UpdateIconAsync(result.Data as string);
        }
    }

    private async Task UpdateIconAsync(string? icon)
    {
        if (_agent is null || _categoryUpdateInFlight) return;
        if (!string.IsNullOrWhiteSpace(icon) && !AgentIconPalette.Contains(icon)) return;
        _categoryUpdateInFlight = true;
        try
        {
            var result = await Js.InvokeAsync<CategoryUpdateResult?>(
                "doxieOs.updateAgentCategory", _agent.Id, _agent.DisplayCategory, icon);
            if (result is { Ok: true })
            {
                Snackbar.AddDoxieToast($"Updated icon for \"{_agent.Name}\"", Severity.Success);
                var refreshed = Catalog.FindById(_agent.Id);
                if (refreshed is not null) _agent = refreshed;
                await InvokeAsync(StateHasChanged);
            }
            else
            {
                Snackbar.AddDoxieToast($"Icon update failed: {result?.Message ?? "unknown error"}", Severity.Error);
            }
        }
        catch (JSException ex)
        {
            Snackbar.AddDoxieToast($"Icon update failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _categoryUpdateInFlight = false;
        }
    }

    private async Task OnCustomCategoryKeyDown(KeyboardEventArgs e)
    {
        if (string.Equals(e.Key, "Enter", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyCustomCategory();
        }
        else if (string.Equals(e.Key, "Escape", StringComparison.OrdinalIgnoreCase))
        {
            CancelCustomCategoryEditor();
        }
    }

    /// <summary>
    /// Sends the new category label to the server. Accepts either a
    /// built-in name (case-insensitive) or a well-formed custom label â€”
    /// the server applies the same parse-or-customise rule the descriptor
    /// uses on read, so no client-side branching is needed beyond
    /// short-circuiting a no-op (clicking the current category).
    /// </summary>
    private async Task UpdateCategoryAsync(string newCategory)
    {
        if (_agent is null || _categoryUpdateInFlight) return;
        if (string.Equals(newCategory, _agent.DisplayCategory, StringComparison.OrdinalIgnoreCase)) return;
        _categoryUpdateInFlight = true;
        // Close the menu immediately so the user gets visual feedback
        // that the click was received â€” controlled @bind-Open doesn't
        // auto-dismiss on item click.
        _categoryMenuOpen = false;
        try
        {
            var result = await Js.InvokeAsync<CategoryUpdateResult?>(
                "doxieOs.updateAgentCategory", _agent.Id, newCategory, _agent.Icon);
            if (result is { Ok: true })
            {
                Snackbar.AddDoxieToast($"Moved \"{_agent.Name}\" to {newCategory}", Severity.Success);
                // The catalog refresh on the server is already done; pull
                // the updated descriptor to refresh the UI.
                var refreshed = Catalog.FindById(_agent.Id);
                if (refreshed is not null) _agent = refreshed;
            }
            else
            {
                Snackbar.AddDoxieToast($"Could not move agent: {result?.Message ?? "unknown error"}", Severity.Error);
            }
        }
        catch (JSException ex)
        {
            Snackbar.AddDoxieToast($"Category update failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _categoryUpdateInFlight = false;
        }
    }

    private sealed record CategoryUpdateResult(bool Ok, int Status, string? Message);
}
