using Viamus.Doxie.Orchestrator.Agents;

namespace Viamus.Doxie.Orchestrator.Components.Pages;

public partial class Home
{
    // Spend panel — aggregated over agent runs whose Usage is set.
    // Window is user-selected (24h / 7d / all); recomputed every Reload().
    private enum CostWindow { Last24h, Last7d, AllTime }
    private CostWindow _costWindow = CostWindow.Last24h;
    private decimal _costTotalUsd;
    private long _costTotalTokens;
    private long _costInputTokens;
    private long _costOutputTokens;
    private long _costCacheReadTokens;
    private long _costCacheCreationTokens;
    private int _costRunCount;

    /// <summary>
    /// Per-provider slice of the Spend panel. <see cref="CostReported"/>
    /// is false when at least one run finished but none of them carried
    /// a cost figure — happens for Codex today. The dashboard shows "—"
    /// instead of "$0" in that case so it's clear this is missing data,
    /// not zero spend.
    /// </summary>
    private sealed record ProviderSpend(
        string Id,
        string DisplayName,
        decimal TotalUsd,
        long TotalTokens,
        int RunCount,
        bool CostReported);

    private static bool IsEstimatedCost(ProviderSpend spend) =>
        string.Equals(spend.Id, "codex", StringComparison.OrdinalIgnoreCase) && spend.CostReported;

    private IReadOnlyList<ProviderSpend> _providerSpend = Array.Empty<ProviderSpend>();

    private void SetCostWindow(CostWindow window)
    {
        if (_costWindow == window) return;
        _costWindow = window;
        RecomputeSpend();
    }

    private void RecomputeSpend()
    {
        _providerSpend = BuildProviderSpend(_agentRuns, out _costTotalUsd, out _costTotalTokens, out _costInputTokens,
            out _costOutputTokens, out _costCacheReadTokens, out _costCacheCreationTokens, out _costRunCount);
    }

    private IReadOnlyList<ProviderSpend> BuildProviderSpend(
        IReadOnlyList<AgentRun> agentRuns,
        out decimal costTotalUsd,
        out long costTotalTokens,
        out long costInputTokens,
        out long costOutputTokens,
        out long costCacheReadTokens,
        out long costCacheCreationTokens,
        out int costRunCount)
    {
        var cutoff = _costWindow switch
        {
            CostWindow.Last24h => DateTimeOffset.UtcNow.AddHours(-24),
            CostWindow.Last7d => DateTimeOffset.UtcNow.AddDays(-7),
            _ => DateTimeOffset.MinValue,
        };

        // Per-provider accumulator. Seed with every registered provider
        // so Codex stays visible at zero before it has any runs in the
        // window, and so the order matches the resolver (which mirrors
        // Settings).
        var providers = ProviderResolver.AllProviders;
        var perProvider = providers.ToDictionary(
            p => p.Id,
            p => new PerProviderAccumulator(p.Id, p.DisplayName),
            StringComparer.OrdinalIgnoreCase);

        decimal totalUsd = 0;
        long input = 0, output = 0, cacheRead = 0, cacheCreation = 0;
        var count = 0;
        foreach (var r in agentRuns)
        {
            if (r.StartedAt < cutoff) continue;

            // Bucket every in-window run by provider — even ones without
            // a Usage block, so Codex shows activity even while
            // token/cost details are unavailable.
            var providerKey = r.ProviderId ?? "unknown";
            if (!perProvider.TryGetValue(providerKey, out var bucket))
            {
                // Run came from a provider we no longer recognize (e.g.
                // a provider the user removed). Surface it so the cost
                // isn't silently dropped.
                bucket = new PerProviderAccumulator(providerKey, providerKey);
                perProvider[providerKey] = bucket;
            }
            bucket.RunCount++;

            if (r.Usage is not { } u) continue;
            totalUsd += u.TotalCostUsd ?? 0m;
            input += u.InputTokens;
            output += u.OutputTokens;
            cacheRead += u.CacheReadTokens;
            cacheCreation += u.CacheCreationTokens;
            count++;

            bucket.TotalTokens += u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheCreationTokens;
            if (u.TotalCostUsd is { } cost)
            {
                bucket.TotalUsd += cost;
                bucket.CostReported = true;
            }
        }

        costTotalUsd = totalUsd;
        costInputTokens = input;
        costOutputTokens = output;
        costCacheReadTokens = cacheRead;
        costCacheCreationTokens = cacheCreation;
        costTotalTokens = input + output + cacheRead + cacheCreation;
        costRunCount = count;

        // Preserve the resolver's enumeration order for known providers
        // and append unknowns alphabetically so the layout is stable.
        var ordered = new List<ProviderSpend>();
        foreach (var p in providers)
        {
            var b = perProvider[p.Id];
            ordered.Add(new ProviderSpend(b.Id, b.DisplayName, b.TotalUsd, b.TotalTokens, b.RunCount, b.CostReported));
            perProvider.Remove(p.Id);
        }
        foreach (var b in perProvider.Values.OrderBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            ordered.Add(new ProviderSpend(b.Id, b.DisplayName, b.TotalUsd, b.TotalTokens, b.RunCount, b.CostReported));
        }
        return ordered;
    }

    private sealed class PerProviderAccumulator
    {
        public PerProviderAccumulator(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public decimal TotalUsd { get; set; }
        public long TotalTokens { get; set; }
        public int RunCount { get; set; }
        public bool CostReported { get; set; }
    }

    private static string FormatTokens(long count)
    {
        if (count <= 0) return "0";
        if (count < 1_000) return count.ToString();
        if (count < 1_000_000) return $"{count / 1000.0:0.#}k";
        return $"{count / 1_000_000.0:0.##}M";
    }

    private static string FormatCost(decimal usd)
    {
        if (usd == 0) return "$0";
        if (usd < 0.01m) return $"${usd:0.0000}";
        if (usd < 1m) return $"${usd:0.000}";
        return $"${usd:0.00}";
    }
}