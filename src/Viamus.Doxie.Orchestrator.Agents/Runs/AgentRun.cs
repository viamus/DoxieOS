namespace Viamus.Doxie.Orchestrator.Agents;

public sealed class AgentRun
{
    private readonly List<AgentRunOutputLine> _output = new();
    private int? _outputLineCount;

    public AgentRun(string id, string agentId, string arguments, DateTimeOffset startedAt)
    {
        Id = id;
        AgentId = agentId;
        Arguments = arguments;
        StartedAt = startedAt;
        Status = AgentRunStatus.Queued;
    }

    public string Id { get; }

    public string AgentId { get; }

    public string Arguments { get; }

    public DateTimeOffset StartedAt { get; }

    public AgentRunStatus Status { get; internal set; }

    public DateTimeOffset? FinishedAt { get; internal set; }

    public int? ExitCode { get; internal set; }

    /// <summary>
    /// Absolute path of the per-run output folder this agent produced (its
    /// allocated sandbox under <c>./.sandbox/&lt;tag&gt;/</c> for standalone
    /// runs, or its node folder under <c>./.runs/&lt;runId&gt;/&lt;nodeId&gt;/</c>
    /// for workflow nodes). Captured at process exit by parsing the last
    /// JSON manifest line on stdout — agents whose contract returns a
    /// <c>report_dir</c> field expose their artefacts to the UI this way.
    /// Null for runs that didn't produce a manifest or aren't producer
    /// agents.
    /// </summary>
    public string? OutputDir { get; internal set; }

    /// <summary>
    /// Token + cost accounting captured from the provider's terminal
    /// "result" event. Set once near process exit (when the runner
    /// observes a usage-bearing event on stdout) and persisted via
    /// <see cref="IAgentRunStore.UpdateUsage"/>. Null while the run is
    /// in flight, and stays null for providers that don't surface
    /// usage (Codex today, plain-text test fixtures).
    /// </summary>
    public AgentRunUsage? Usage { get; internal set; }

    /// <summary>
    /// Stable id of the <see cref="IAgentProvider"/> that ran this
    /// agent (e.g. <c>"claude-code"</c>, <c>"codex"</c>). Snapshotted
    /// at <c>Start</c> from the resolver's active provider so a later
    /// Settings-page change to the default doesn't rewrite history.
    /// Null only for runs persisted before this field existed (legacy
    /// rows surviving the schema migration).
    /// </summary>
    public string? ProviderId { get; internal set; }

    public IReadOnlyList<AgentRunOutputLine> Output => _output;

    /// <summary>
    /// Total persisted output line count for this run. When a store hydrates
    /// only the visible tail, <see cref="Output"/> contains the loaded tail
    /// and this value still reflects the full log length.
    /// </summary>
    public int OutputLineCount => _outputLineCount ?? _output.Count;

    internal void Append(AgentRunOutputLine line)
    {
        _output.Add(line);
        if (_outputLineCount.HasValue && _outputLineCount.Value < _output.Count)
        {
            _outputLineCount = _output.Count;
        }
    }

    /// <summary>
    /// Reconstructs an <see cref="AgentRun"/> from persisted state.
    /// Used by stores when reading rows back from disk.
    /// </summary>
    public static AgentRun Hydrate(
        string id,
        string agentId,
        string arguments,
        DateTimeOffset startedAt,
        AgentRunStatus status,
        DateTimeOffset? finishedAt,
        int? exitCode,
        IEnumerable<AgentRunOutputLine> output,
        string? outputDir = null,
        AgentRunUsage? usage = null,
        string? providerId = null,
        int? outputLineCount = null)
    {
        var run = new AgentRun(id, agentId, arguments, startedAt)
        {
            Status = status,
            FinishedAt = finishedAt,
            ExitCode = exitCode,
            OutputDir = outputDir,
            Usage = usage,
            ProviderId = providerId,
            _outputLineCount = outputLineCount,
        };
        foreach (var line in output)
        {
            run.Append(line);
        }
        return run;
    }
}
