using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class WorkflowDetail
{
    private NodeDragState? _drag;
    private NodeContextMenu? _nodeMenu;

    private string NodeCursor(string nodeId) =>
        _drag?.NodeId == nodeId ? "grabbing" : "grab";

    private void BeginNodeDrag(WorkflowNode node, PointerEventArgs e)
    {
        if (e.Button != 0) return;

        CloseNodeMenu();
        SelectNode(node.Id);
        _drag = new NodeDragState(node.Id, e.ClientX, e.ClientY, node.X, node.Y, Moved: false);
    }

    private void DragNode(PointerEventArgs e)
    {
        if (_drag is null || _workflow is null) return;

        var x = Math.Max(20, _drag.StartX + (int)Math.Round(e.ClientX - _drag.StartClientX));
        var y = Math.Max(20, _drag.StartY + (int)Math.Round(e.ClientY - _drag.StartClientY));
        var current = _workflow.Nodes.FirstOrDefault(n => string.Equals(n.Id, _drag.NodeId, StringComparison.OrdinalIgnoreCase));
        if (current is null || (current.X == x && current.Y == y)) return;

        MoveWorkflowNode(_drag.NodeId, x, y);
        _drag = _drag with { Moved = true };
    }

    private void EndNodeDrag()
    {
        if (_drag?.Moved == true && _workflow is not null)
        {
            _workflow = _workflow with { UpdatedAt = DateTime.UtcNow };
            WorkflowStore.Save(_workflow);
        }

        _drag = null;
    }

    private void OpenNodeMenu(WorkflowNode node)
    {
        if (_activeRun is null && string.IsNullOrWhiteSpace(node.AgentId)) return;

        SelectNode(node.Id);
        _drag = null;
        _nodeMenu = new NodeContextMenu(
            node.Id,
            node.AgentId,
            Math.Min(node.X + 22, CanvasWidth - 198),
            Math.Min(node.Y + NodeHeight + 10, CanvasHeight - 122));
    }

    private void CloseNodeMenu()
    {
        _nodeMenu = null;
    }

    private void NavigateToAgent(string agentId)
    {
        _nodeMenu = null;
        Nav.NavigateTo($"/agents/{Uri.EscapeDataString(agentId)}");
    }

    private async Task RerunFromNode(string nodeId)
    {
        if (_workflow is null || _activeRun is null) return;
        var node = NodeOrNull(nodeId);
        if (node is null) return;

        _nodeMenu = null;
        var parameters = new DialogParameters
        {
            ["NodeLabel"] = node.Label,
        };
        var dialog = await Dialog.ShowAsync<Viamus.Doxie.Orchestrator.Components.Workflows.WorkflowRerunGuidanceDialog>(
            "Run from here",
            parameters,
            new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        if (result is null || result.Canceled) return;

        var guidance = result.Data as string;
        try
        {
            var run = WorkflowRunner.StartFrom(_workflow, _activeRun.Id, node.Id, guidance);
            _activeRun = run;
            Snackbar.Add("Rerun started from selected step", Severity.Info);
        }
        catch (InvalidOperationException ex)
        {
            Snackbar.Add($"Could not rerun from here: {ex.Message}", Severity.Error);
        }
    }

    private void MoveWorkflowNode(string nodeId, int x, int y)
    {
        if (_workflow is null) return;

        _workflow = _workflow with
        {
            Nodes = _workflow.Nodes
                .Select(n => string.Equals(n.Id, nodeId, StringComparison.OrdinalIgnoreCase)
                    ? n with { X = x, Y = y }
                    : n)
                .ToList(),
        };
    }

    private sealed record NodeDragState(
        string NodeId,
        double StartClientX,
        double StartClientY,
        int StartX,
        int StartY,
        bool Moved);

    private sealed record NodeContextMenu(
        string NodeId,
        string? AgentId,
        int X,
        int Y);
}
