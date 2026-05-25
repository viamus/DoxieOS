using Microsoft.AspNetCore.Components.Web;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Workflows;

public partial class WorkflowForm
{
    private NodeContextMenu? _nodeMenu;

    private string PreviewNodeCursor(string nodeId) =>
        _drag?.NodeId == nodeId ? "grabbing" : "grab";

    private void BeginPreviewNodeDrag(WorkflowNode node, PointerEventArgs e)
    {
        if (e.Button != 0) return;

        CloseNodeMenu();
        _drag = new NodeDragState(node.Id, e.ClientX, e.ClientY, node.X, node.Y);
        SetManualNodePosition(node.Id, node.X, node.Y);
    }

    private void DragPreviewNode(PointerEventArgs e)
    {
        if (_drag is null) return;

        var x = Math.Max(20, _drag.StartX + (int)Math.Round(e.ClientX - _drag.StartClientX));
        var y = Math.Max(20, _drag.StartY + (int)Math.Round(e.ClientY - _drag.StartClientY));
        SetManualNodePosition(_drag.NodeId, x, y);
    }

    private void EndPreviewNodeDrag()
    {
        _drag = null;
    }

    private void OpenNodeMenu(WorkflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.AgentId)) return;

        _drag = null;
        _nodeMenu = new NodeContextMenu(
            node.Id,
            node.AgentId,
            Math.Min(node.X + 22, CanvasWidth(BuildPreview().Nodes) - 198),
            Math.Min(node.Y + WorkflowLayout.NodeHeight + 10, CanvasHeight(BuildPreview().Nodes) - 88));
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

    private void SetManualNodePosition(string nodeId, int x, int y)
    {
        if (string.Equals(nodeId, "trigger", StringComparison.OrdinalIgnoreCase))
        {
            _triggerX = x;
            _triggerY = y;
            return;
        }

        var step = _steps.FirstOrDefault(s => string.Equals(s.Id, nodeId, StringComparison.OrdinalIgnoreCase));
        if (step is null) return;

        step.X = x;
        step.Y = y;
    }

    private sealed record NodeContextMenu(
        string NodeId,
        string AgentId,
        int X,
        int Y);
}
