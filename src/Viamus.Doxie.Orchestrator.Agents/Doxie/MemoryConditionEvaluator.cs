using System.Text.RegularExpressions;

namespace Viamus.Doxie.Orchestrator.Agents.Doxie;

/// <summary>
/// Tiny condition DSL used by per-skill memories to gate themselves
/// on the runtime context of a dispatch (mode, provider, workspace).
///
/// Grammar (alpha — single-line, AND-only, no parentheses):
/// <code>
///   expr     := atom ( "AND" atom )*
///   atom     := "always" | "never" | comparison
///   comparison := key ( "=" | "!=" ) string-literal
///   key      := "mode" | "provider" | "workspace_id"
///   string-literal := "..." | '...'   (double or single quotes)
/// </code>
///
/// A null/empty condition is treated as <c>always</c>. Anything that
/// doesn't parse evaluates to false (the memory is silently skipped)
/// and the failure is reported via the optional <see cref="OnInvalid"/>
/// callback so misconfigurations surface in logs without crashing the
/// dispatch.
/// </summary>
public static class MemoryConditionEvaluator
{
    /// <summary>
    /// Runtime facts a condition can reference. <see cref="WorkspaceId"/>
    /// is empty (not null) when there's no workspace bound to the
    /// dispatch — this lets a condition test for absence with
    /// <c>workspace_id = ""</c> instead of needing a separate "is null"
    /// keyword.
    /// </summary>
    public sealed record Context(string Mode, string Provider, string WorkspaceId);

    private static readonly Regex _comparison = new(
        @"^\s*(?<key>mode|provider|workspace_id)\s*(?<op>!=|=)\s*(?<quote>[""'])(?<value>.*?)\k<quote>\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Evaluates <paramref name="condition"/> against the supplied
    /// context. Returns true when the memory should be loaded for this
    /// dispatch. Null/empty/whitespace condition is treated as always.
    /// Invalid expressions return false and (when supplied) report the
    /// reason via <paramref name="onInvalid"/>.
    /// </summary>
    public static bool Eval(string? condition, Context ctx, Action<string>? onInvalid = null)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;

        // Top-level AND split — the only combinator we support in alpha.
        // Whitespace surrounding " AND " is liberally trimmed; case-
        // insensitive so users can write "and" or "And" comfortably.
        var parts = Regex.Split(condition, @"\s+AND\s+", RegexOptions.IgnoreCase);
        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                onInvalid?.Invoke($"Empty operand in condition '{condition}'.");
                return false;
            }

            if (string.Equals(part, "always", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(part, "never", StringComparison.OrdinalIgnoreCase)) return false;

            var match = _comparison.Match(part);
            if (!match.Success)
            {
                onInvalid?.Invoke(
                    $"Could not parse condition operand '{part}'. " +
                    "Supported forms: 'always', 'never', 'mode=\"X\"', 'provider=\"X\"', 'workspace_id=\"X\"' (or '!=').");
                return false;
            }

            var key = match.Groups["key"].Value.ToLowerInvariant();
            var op = match.Groups["op"].Value;
            var value = match.Groups["value"].Value;

            var actual = key switch
            {
                "mode" => ctx.Mode,
                "provider" => ctx.Provider,
                "workspace_id" => ctx.WorkspaceId,
                _ => string.Empty,
            };

            var equal = string.Equals(actual, value, StringComparison.Ordinal);
            var matched = op == "=" ? equal : !equal;
            if (!matched) return false;
        }

        return true;
    }

    /// <summary>
    /// Filters and orders a memory list for one dispatch — convenience
    /// over <see cref="Eval"/> + LINQ that callers would otherwise
    /// inline at every dispatch site. Sorted high â†’ medium â†’ low,
    /// then by FileName for stability.
    /// </summary>
    public static IReadOnlyList<DoxieSkillMemory> Filter(
        IReadOnlyList<DoxieSkillMemory> memories,
        Context ctx,
        Action<DoxieSkillMemory, string>? onInvalid = null)
    {
        if (memories.Count == 0) return memories;

        return memories
            .Where(m => Eval(m.Condition, ctx, reason => onInvalid?.Invoke(m, reason)))
            .OrderByDescending(m => m.Priority)
            .ThenBy(m => m.FileName, StringComparer.Ordinal)
            .ToList();
    }
}
