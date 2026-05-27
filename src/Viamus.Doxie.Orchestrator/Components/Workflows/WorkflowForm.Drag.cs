using Microsoft.AspNetCore.Components.Web;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Workflows;

public partial class WorkflowForm
{
    private NodeContextMenu? _nodeMenu;

    private string PreviewNodeCursor(string nodeId) =>
        _drag?.NodeId == nodeId ? "grabbing" : "grab";

    private string LoopBodyCursor(string loopId) =>
        _loopBodyDrag?.LoopId == loopId ? "grabbing" : "grab";

    private void BeginPreviewNodeDrag(WorkflowNode node, PointerEventArgs e)
    {
        if (e.Button != 0) return;

        CloseNodeMenu();
        _loopBodyDrag = null;
        _drag = new NodeDragState(node.Id, e.ClientX, e.ClientY, node.X, node.Y);
        SetManualNodePosition(node.Id, node.X, node.Y);
    }

    private void BeginLoopBodyDrag(
        string loopId,
        IReadOnlyList<WorkflowNode> bodyNodes,
        PointerEventArgs e)
    {
        if (e.Button != 0 || bodyNodes.Count == 0) return;

        CloseNodeMenu();
        _drag = null;

        var startPositions = bodyNodes.ToDictionary(
            n => n.Id,
            n => new NodePosition(n.X, n.Y),
            StringComparer.OrdinalIgnoreCase);

        foreach (var node in bodyNodes)
        {
            SetManualNodePosition(node.Id, node.X, node.Y);
        }

        _loopBodyDrag = new LoopBodyDragState(loopId, e.ClientX, e.ClientY, startPositions);
    }

    private void DragPreview(PointerEventArgs e)
    {
        if (_drag is not null)
        {
            var x = Math.Max(20, _drag.StartX + (int)Math.Round(e.ClientX - _drag.StartClientX));
            var y = Math.Max(20, _drag.StartY + (int)Math.Round(e.ClientY - _drag.StartClientY));
            SetManualNodePosition(_drag.NodeId, x, y);
            return;
        }

        if (_loopBodyDrag is null) return;

        var deltaX = (int)Math.Round(e.ClientX - _loopBodyDrag.StartClientX);
        var deltaY = (int)Math.Round(e.ClientY - _loopBodyDrag.StartClientY);
        var minX = _loopBodyDrag.StartPositions.Min(p => p.Value.X);
        var minY = _loopBodyDrag.StartPositions.Min(p => p.Value.Y);

        deltaX = Math.Max(deltaX, 20 - minX);
        deltaY = Math.Max(deltaY, 20 - minY);

        foreach (var (nodeId, start) in _loopBodyDrag.StartPositions)
        {
            SetManualNodePosition(nodeId, start.X + deltaX, start.Y + deltaY);
        }
    }

    private void EndPreviewDrag()
    {
        _drag = null;
        _loopBodyDrag = null;
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

    private sealed record LoopBodyDragState(
        string LoopId,
        double StartClientX,
        double StartClientY,
        IReadOnlyDictionary<string, NodePosition> StartPositions);

    private readonly record struct NodePosition(int X, int Y);
}
