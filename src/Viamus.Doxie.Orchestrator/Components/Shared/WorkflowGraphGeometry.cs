using System.Globalization;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Shared;

internal static class WorkflowGraphGeometry
{
    private const int LoopPaddingX = 24;
    private const int LoopPaddingTop = 54;
    private const int LoopPaddingBottom = 18;

    public static IReadOnlyList<WorkflowLoopRegion> LoopRegions(
        IReadOnlyList<WorkflowNode> nodes,
        int nodeWidth,
        int nodeHeight)
    {
        return nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.LoopId))
            .GroupBy(n => n.LoopId!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var bodyNodes = group.ToList();
                var owner = nodes.FirstOrDefault(n =>
                    string.Equals(n.Id, group.Key, StringComparison.OrdinalIgnoreCase));

                var x = bodyNodes.Min(n => n.X) - LoopPaddingX;
                var y = bodyNodes.Min(n => n.Y) - LoopPaddingTop;
                var right = bodyNodes.Max(n => n.X) + nodeWidth + LoopPaddingX;
                var bottom = bodyNodes.Max(n => n.Y) + nodeHeight + LoopPaddingBottom;

                return new WorkflowLoopRegion(
                    group.Key,
                    owner?.Label ?? group.Key,
                    LoopSummary(owner),
                    x,
                    y,
                    right - x,
                    bottom - y);
            })
            .OrderBy(r => r.X)
            .ThenBy(r => r.Y)
            .ToList();
    }

    public static bool IsCollapsedLoopNode(WorkflowNode node, IReadOnlyList<WorkflowNode> nodes) =>
        IsLoopNode(node) && nodes.Any(n => string.Equals(n.LoopId, node.Id, StringComparison.OrdinalIgnoreCase));

    public static int CanvasWidth(
        IReadOnlyList<WorkflowNode> nodes,
        int nodeWidth,
        int nodeHeight)
    {
        if (nodes.Count == 0) return 800;

        var nodeRight = nodes.Max(n => n.X + nodeWidth + 80);
        var regionRight = LoopRegions(nodes, nodeWidth, nodeHeight)
            .Select(r => r.Right + 80)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(800, Math.Max(nodeRight, regionRight));
    }

    public static int CanvasHeight(
        IReadOnlyList<WorkflowNode> nodes,
        int nodeWidth,
        int nodeHeight)
    {
        if (nodes.Count == 0) return 440;

        var nodeBottom = nodes.Max(n => n.Y + nodeHeight + 80);
        var regionBottom = LoopRegions(nodes, nodeWidth, nodeHeight)
            .Select(r => r.Bottom + 80)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(440, Math.Max(nodeBottom, regionBottom));
    }

    public static WorkflowEdgeRoute? RouteEdge(
        WorkflowEdge edge,
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges,
        int nodeWidth,
        int nodeHeight)
    {
        var from = nodes.FirstOrDefault(n => string.Equals(n.Id, edge.FromNodeId, StringComparison.OrdinalIgnoreCase));
        var to = nodes.FirstOrDefault(n => string.Equals(n.Id, edge.ToNodeId, StringComparison.OrdinalIgnoreCase));
        if (from is null || to is null) return null;

        var regions = LoopRegions(nodes, nodeWidth, nodeHeight)
            .ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

        var start = StartPoint(edge, from, to, regions, nodeWidth, nodeHeight);
        var end = EndPoint(edge, to, regions, nodeHeight);
        if (start is null || end is null) return null;

        if (regions.TryGetValue(edge.FromNodeId, out var fromRegion)
            && string.Equals(to.LoopId, fromRegion.Id, StringComparison.OrdinalIgnoreCase))
        {
            var path = $"M {F(start.Value.X)} {F(start.Value.Y)} L {F(end.Value.X)} {F(end.Value.Y)}";
            var label = LabelPoint(edge, start.Value, end.Value, (start.Value.X + end.Value.X) / 2, end.Value.Y, 0);
            return new WorkflowEdgeRoute(path, label.X, label.Y);
        }

        var offset = LaneOffset(edge, edges);
        var deltaX = end.Value.X - start.Value.X;
        if (deltaX >= 72)
        {
            var midX = start.Value.X + (deltaX / 2) + offset;
            var minMidX = start.Value.X + 34;
            var maxMidX = end.Value.X - 34;
            if (maxMidX > minMidX)
            {
                midX = Math.Clamp(midX, minMidX, maxMidX);
            }

            var path = $"M {F(start.Value.X)} {F(start.Value.Y)} " +
                       $"L {F(midX)} {F(start.Value.Y)} " +
                       $"L {F(midX)} {F(end.Value.Y)} " +
                       $"L {F(end.Value.X)} {F(end.Value.Y)}";
            var label = LabelPoint(edge, start.Value, end.Value, midX, (start.Value.Y + end.Value.Y) / 2, offset);
            return new WorkflowEdgeRoute(path, label.X, label.Y);
        }

        var outerX = Math.Max(start.Value.X, end.Value.X) + 70 + Math.Abs(offset);
        var fallbackPath = $"M {F(start.Value.X)} {F(start.Value.Y)} " +
                           $"L {F(outerX)} {F(start.Value.Y)} " +
                           $"L {F(outerX)} {F(end.Value.Y)} " +
                           $"L {F(end.Value.X)} {F(end.Value.Y)}";
        var fallbackLabel = LabelPoint(edge, start.Value, end.Value, outerX, (start.Value.Y + end.Value.Y) / 2, offset);
        return new WorkflowEdgeRoute(fallbackPath, fallbackLabel.X, fallbackLabel.Y);
    }

    private static WorkflowPoint LabelPoint(
        WorkflowEdge edge,
        WorkflowPoint start,
        WorkflowPoint end,
        double defaultX,
        double defaultY,
        double laneOffset)
    {
        if (string.IsNullOrWhiteSpace(edge.Condition))
        {
            return new WorkflowPoint(defaultX, defaultY);
        }

        var condition = edge.Condition.Trim();
        var verticalOffset = condition.Equals("true", StringComparison.OrdinalIgnoreCase) ? -20d : 20d;
        var horizontalRoom = Math.Abs(end.X - start.X);
        var labelX = horizontalRoom >= 120
            ? start.X + 66 + Math.Clamp(laneOffset, -12d, 12d)
            : Math.Max(start.X, end.X) + 54;
        var labelY = Math.Max(24d, start.Y + verticalOffset);

        return new WorkflowPoint(labelX, labelY);
    }

    private static WorkflowPoint? StartPoint(
        WorkflowEdge edge,
        WorkflowNode from,
        WorkflowNode to,
        IReadOnlyDictionary<string, WorkflowLoopRegion> regions,
        int nodeWidth,
        int nodeHeight)
    {
        if (regions.TryGetValue(edge.FromNodeId, out var region))
        {
            if (string.Equals(to.LoopId, region.Id, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkflowPoint(region.X + 8, to.Y + nodeHeight / 2d);
            }

            return new WorkflowPoint(region.Right, region.HeaderCenterY);
        }

        return new WorkflowPoint(from.X + nodeWidth, from.Y + nodeHeight / 2d);
    }

    private static WorkflowPoint? EndPoint(
        WorkflowEdge edge,
        WorkflowNode to,
        IReadOnlyDictionary<string, WorkflowLoopRegion> regions,
        int nodeHeight)
    {
        if (regions.TryGetValue(edge.ToNodeId, out var region))
        {
            return new WorkflowPoint(region.X, region.HeaderCenterY);
        }

        return new WorkflowPoint(to.X, to.Y + nodeHeight / 2d);
    }

    private static double LaneOffset(WorkflowEdge edge, IReadOnlyList<WorkflowEdge> edges)
    {
        var sameSource = edges
            .Where(e => string.Equals(e.FromNodeId, edge.FromNodeId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.ToNodeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Condition ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sameSource.Count <= 1) return 0;

        var index = sameSource.FindIndex(e =>
            string.Equals(e.ToNodeId, edge.ToNodeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Condition ?? string.Empty, edge.Condition ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return 0;

        return (index - ((sameSource.Count - 1) / 2d)) * 18d;
    }

    private static bool IsLoopNode(WorkflowNode node) =>
        node.Kind == WorkflowNodeKind.Loop
        || string.Equals(node.AgentId, "loop", StringComparison.OrdinalIgnoreCase);

    private static string LoopSummary(WorkflowNode? owner)
    {
        var source = Input(owner, "array_source")
            ?? Input(owner, "items")
            ?? Input(owner, "collection")
            ?? "items";
        var path = Input(owner, "array_path");
        var target = string.IsNullOrWhiteSpace(path) ? source : $"{source}:{path}";
        var concurrency = Input(owner, "concurrency") ?? "4";
        var failure = Input(owner, "on_failure") ?? "fail-fast";

        return $"iterate {target} | concurrency {concurrency} | {failure}";
    }

    private static string? Input(WorkflowNode? node, string key) =>
        node?.Inputs is { } inputs && inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string F(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private readonly record struct WorkflowPoint(double X, double Y);
}

internal sealed record WorkflowLoopRegion(
    string Id,
    string Label,
    string Summary,
    int X,
    int Y,
    int Width,
    int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public double HeaderCenterY => Y + 27d;
}

internal sealed record WorkflowEdgeRoute(string Path, double LabelX, double LabelY);
