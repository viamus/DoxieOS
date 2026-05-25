namespace Viamus.Doxie.Orchestrator.Workflows;

/// <summary>
/// Auto-positions workflow nodes for the canvas so the creator UI
/// doesn't have to manage coordinates. Strategy: BFS from every root
/// (node with no incoming edge); the longest path to a node defines
/// its column (depth), siblings inside the same column are stacked
/// vertically. Yields well-spaced left-to-right layouts that match
/// the n8n / dagster convention readers already expect.
///
/// <para>Pure function, idempotent — running it twice on the same
/// definition produces identical X/Y. Safe to invoke at save time
/// (creator) or at render time (legacy workflows imported by hand).</para>
/// </summary>
public static class WorkflowLayout
{
    public const int NodeWidth = 200;
    public const int NodeHeight = 76;
    public const int ColumnSpacing = 240;
    public const int RowSpacing = 130;
    public const int MarginX = 60;
    public const int MarginY = 40;

    public static IReadOnlyList<WorkflowNode> AutoPosition(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges)
    {
        if (nodes.Count == 0) return nodes;

        var depth = ComputeDepth(nodes, edges);

        // Group by depth to assign Y positions. Inside a column, sort
        // by node id for determinism so the same definition always
        // renders the same way.
        var columns = depth
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(kv => kv.Key)
                      .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                      .ToList());

        var positioned = nodes.Select(n =>
        {
            var d = depth[n.Id];
            var siblings = columns[d];
            var row = siblings.IndexOf(n.Id);
            var totalRows = siblings.Count;

            var x = MarginX + d * ColumnSpacing;
            // Center the column vertically around y = MarginY + (maxRows * RowSpacing) / 2,
            // but use a per-column centering so single-node columns sit middle.
            var columnHeight = totalRows * RowSpacing;
            var y = MarginY + row * RowSpacing + (MaxColumnHeight(columns) - columnHeight) / 2;

            return n with { X = x, Y = y };
        }).ToList();

        return positioned;
    }

    private static int MaxColumnHeight(Dictionary<int, List<string>> columns) =>
        columns.Values.Max(c => c.Count) * RowSpacing;

    /// <summary>
    /// Longest-path depth of every node. Cycles (which shouldn't exist
    /// in a workflow but might during editing) are treated as depth 0
    /// so the layout still produces something rather than infinite-looping.
    /// </summary>
    private static Dictionary<string, int> ComputeDepth(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges)
    {
        var dependsOn = nodes.ToDictionary(
            n => n.Id,
            n => edges
                .Where(e => string.Equals(e.ToNodeId, n.Id, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FromNodeId)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int Visit(string id)
        {
            if (depth.TryGetValue(id, out var d)) return d;
            if (!visiting.Add(id)) return 0; // cycle guard

            var deps = dependsOn[id];
            var max = deps.Count == 0 ? 0 : deps.Max(Visit) + 1;
            visiting.Remove(id);
            depth[id] = max;
            return max;
        }

        foreach (var n in nodes) Visit(n.Id);
        return depth;
    }
}
