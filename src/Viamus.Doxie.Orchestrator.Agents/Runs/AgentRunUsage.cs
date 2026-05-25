namespace Viamus.Doxie.Orchestrator.Agents;

/// <summary>
/// Token + cost accounting for a single agent run, captured from the
/// provider's terminal "result" event (Claude CLI stream-json
/// <c>{"type":"result", ...}</c>). Persisted alongside the run so the
/// dashboard can aggregate spend across runs and the per-run page can
/// show a breakdown.
///
/// Token counts are kept separate (not pre-summed) so the UI can show
/// the cache hit rate — cache reads are dramatically cheaper than fresh
/// input tokens, and surfacing the split is the whole reason to track
/// them. <see cref="TotalCostUsd"/> is what the provider itself bills,
/// already converted to USD; null when the provider doesn't surface a
/// cost figure (e.g. Codex today).
/// </summary>
public sealed record AgentRunUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal? TotalCostUsd)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}
