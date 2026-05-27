using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Viamus.Doxie.Orchestrator.Workflows;

public sealed partial class OrchestratedWorkflowRunner
{
    private async Task SimulateDecisionNodeAsync(
        WorkflowNode node,
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowNodeRun nr,
        ConcurrentDictionary<string, string> branchDecisions,
        CancellationToken cancellation)
    {
        var nodeDir = WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, node.Id);
        Directory.CreateDirectory(nodeDir);

        var upstreamIds = definition.Edges
            .Where(e => string.Equals(e.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FromNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var upstreamDirs = upstreamIds
            .Select(id => WorkflowRunPaths.NodeDir(_runsDirectory, run.Id, id))
            .Where(Directory.Exists)
            .ToList();

        await SimulateDecisionNodeInScopeAsync(
            node,
            run,
            definition,
            nr,
            outputDir: nodeDir,
            upstreamDirs: upstreamDirs,
            extraEnv: null,
            branchDecisions: branchDecisions,
            cancellation: cancellation).ConfigureAwait(false);
    }

    private async Task SimulateDecisionNodeInScopeAsync(
        WorkflowNode node,
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowNodeRun nr,
        string outputDir,
        IReadOnlyList<string> upstreamDirs,
        IReadOnlyDictionary<string, string>? extraEnv,
        ConcurrentDictionary<string, string> branchDecisions,
        CancellationToken cancellation)
    {
        Directory.CreateDirectory(outputDir);

        var inputs = ApplyInputSubstitutions(node.Inputs, run.TriggerInputs, extraEnv)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var source = NullIfEmpty(inputs.GetValueOrDefault("source"))
            ?? NullIfEmpty(inputs.GetValueOrDefault("source_file"))
            ?? NullIfEmpty(inputs.GetValueOrDefault("source-file"))
            ?? "result.json";
        var path = NullIfEmpty(inputs.GetValueOrDefault("path"))
            ?? NullIfEmpty(inputs.GetValueOrDefault("json_path"))
            ?? NullIfEmpty(inputs.GetValueOrDefault("json-path"));
        var op = NormalizeDecisionOperator(
            NullIfEmpty(inputs.GetValueOrDefault("operator"))
            ?? NullIfEmpty(inputs.GetValueOrDefault("op"))
            ?? "truthy");
        var expected = inputs.GetValueOrDefault("value")
            ?? inputs.GetValueOrDefault("expected")
            ?? string.Empty;

        nr.AppendLog($"[if-else] config: source={source}, path={path ?? "(root)"}, operator={op}, value={Truncate(expected, 120)}");
        if (upstreamDirs.Count > 0)
        {
            nr.AppendLog($"env: {WorkflowRunPaths.InputDirsEnvVar}={string.Join(';', upstreamDirs)}");
        }

        var sourcePath = upstreamDirs
            .Select(dir => Path.Combine(dir, source))
            .FirstOrDefault(File.Exists);

        var evaluation = sourcePath is null
            ? EvaluateMissingDecisionSource(source, path, op, expected)
            : await EvaluateDecisionSourceAsync(sourcePath, path, op, expected, cancellation).ConfigureAwait(false);

        var selectedBranch = evaluation.Matched ? "true" : "false";
        branchDecisions[node.Id] = selectedBranch;

        var copied = await ForwardUpstreamThroughDecisionAsync(upstreamDirs, outputDir, cancellation).ConfigureAwait(false);

        var payload = new
        {
            selectedBranch,
            matched = evaluation.Matched,
            source,
            sourcePath,
            path,
            @operator = op,
            expected,
            actual = evaluation.Actual,
            actualKind = evaluation.ActualKind,
            reason = evaluation.Reason,
            evaluatedAt = DateTimeOffset.UtcNow,
        };

        await using (var stream = File.Create(Path.Combine(outputDir, "decision.json")))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                payload,
                new JsonSerializerOptions { WriteIndented = true },
                cancellation).ConfigureAwait(false);
        }

        await WorkflowOutputArtifacts.EnsureManifestAsync(
            outputDir,
            producerId: IfElseAgentId,
            producerName: "If/Else",
            kind: "workflow-decision-node",
            title: $"{definition.Name} decision",
            tags: new[] { "workflow", "decision", definition.Id, node.Id, selectedBranch },
            cancellation).ConfigureAwait(false);

        nr.AppendLog($"[if-else] selected branch '{selectedBranch}' ({evaluation.Reason}); forwarded {copied} upstream file(s)");
        nr.OutputSummary = $"if/else: {selectedBranch}";
    }

    private static DecisionEvaluation EvaluateMissingDecisionSource(
        string source,
        string? path,
        string op,
        string expected)
    {
        var matched = op switch
        {
            "not-exists" => true,
            "falsy" => true,
            "empty" => true,
            "not-equals" => !string.IsNullOrEmpty(expected),
            _ => false,
        };
        return new DecisionEvaluation(
            matched,
            Actual: null,
            ActualKind: null,
            Reason: $"source '{source}' was not found{(string.IsNullOrWhiteSpace(path) ? "" : $" for path '{path}'")}");
    }

    private static async Task<DecisionEvaluation> EvaluateDecisionSourceAsync(
        string sourcePath,
        string? path,
        string op,
        string expected,
        CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(sourcePath);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
        var exists = TryResolveDecisionPath(doc.RootElement, path, out var actualElement);

        var matched = op switch
        {
            "exists" => exists,
            "not-exists" => !exists,
            "truthy" => exists && IsTruthy(actualElement),
            "falsy" => !exists || !IsTruthy(actualElement),
            "empty" => !exists || IsEmpty(actualElement),
            "not-empty" => exists && !IsEmpty(actualElement),
            "equals" => exists && DecisionEquals(actualElement, expected),
            "not-equals" => !exists || !DecisionEquals(actualElement, expected),
            "contains" => exists && DecisionContains(actualElement, expected),
            "greater-than" => exists && CompareNumber(actualElement, expected, static (left, right) => left > right),
            "less-than" => exists && CompareNumber(actualElement, expected, static (left, right) => left < right),
            "greater-or-equal" => exists && CompareNumber(actualElement, expected, static (left, right) => left >= right),
            "less-or-equal" => exists && CompareNumber(actualElement, expected, static (left, right) => left <= right),
            _ => throw new InvalidOperationException(
                $"Unsupported if-else operator '{op}'. Use exists, not-exists, truthy, falsy, empty, not-empty, equals, not-equals, contains, greater-than, less-than, greater-or-equal, or less-or-equal."),
        };

        if (!exists)
        {
            return new DecisionEvaluation(matched, null, null, $"path '{path}' was not found");
        }

        return new DecisionEvaluation(
            matched,
            JsonElementToString(actualElement),
            actualElement.ValueKind.ToString(),
            matched ? "condition matched" : "condition did not match");
    }

    private static async Task<int> ForwardUpstreamThroughDecisionAsync(
        IReadOnlyList<string> upstreamDirs,
        string outputDir,
        CancellationToken cancellation)
    {
        var copied = 0;
        foreach (var upstreamDir in upstreamDirs)
        {
            if (!Directory.Exists(upstreamDir)) continue;

            foreach (var sourceFile in Directory.EnumerateFiles(upstreamDir, "*", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(upstreamDir, sourceFile);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    continue;
                }
                if (string.Equals(Path.GetFileName(relative), "decision.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (string.Equals(Path.GetFileName(relative), ".doxie-artifact.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(outputDir, relative));
                var outputRoot = Path.GetFullPath(outputDir);
                if (!destination.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(destination, outputRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                await using var input = File.OpenRead(sourceFile);
                await using var output = File.Create(destination);
                await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
                copied++;
            }
        }

        return copied;
    }

    private static string NormalizeDecisionOperator(string op)
    {
        var normalized = op.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "==" => "equals",
            "=" => "equals",
            "eq" => "equals",
            "!=" => "not-equals",
            "<>" => "not-equals",
            "ne" => "not-equals",
            ">" => "greater-than",
            "gt" => "greater-than",
            "<" => "less-than",
            "lt" => "less-than",
            ">=" => "greater-or-equal",
            "gte" => "greater-or-equal",
            "<=" => "less-or-equal",
            "lte" => "less-or-equal",
            "non-empty" => "not-empty",
            _ => normalized,
        };
    }

    private static bool TryResolveDecisionPath(JsonElement root, string? path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path)) return true;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!TryGetPropertyCaseInsensitive(value, segment, out value)) return false;
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index < 0 || index >= value.GetArrayLength()) return false;
                value = value.EnumerateArray().ElementAt(index);
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsTruthy(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetDouble(out var d) && Math.Abs(d) > double.Epsilon,
            JsonValueKind.String => IsTruthyString(element.GetString()),
            JsonValueKind.Array => element.GetArrayLength() > 0,
            JsonValueKind.Object => element.EnumerateObject().Any(),
            _ => false,
        };

    private static bool IsTruthyString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim().ToLowerInvariant() switch
        {
            "false" => false,
            "0" => false,
            "no" => false,
            "nao" => false,
            "não" => false,
            "null" => false,
            "undefined" => false,
            _ => true,
        };
    }

    private static bool IsEmpty(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Array => element.GetArrayLength() == 0,
            JsonValueKind.Object => !element.EnumerateObject().Any(),
            _ => false,
        };

    private static bool DecisionEquals(JsonElement actual, string expected)
    {
        if (actual.ValueKind == JsonValueKind.Number
            && double.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedNumber)
            && actual.TryGetDouble(out var actualNumber))
        {
            return Math.Abs(actualNumber - expectedNumber) < double.Epsilon;
        }

        return string.Equals(JsonElementToString(actual), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DecisionContains(JsonElement actual, string expected) =>
        actual.ValueKind switch
        {
            JsonValueKind.Array => actual.EnumerateArray().Any(e => DecisionEquals(e, expected)),
            JsonValueKind.Object => actual.EnumerateObject().Any(p =>
                p.Name.Contains(expected, StringComparison.OrdinalIgnoreCase)
                || DecisionContains(p.Value, expected)),
            _ => JsonElementToString(actual).Contains(expected, StringComparison.OrdinalIgnoreCase),
        };

    private static bool CompareNumber(JsonElement actual, string expected, Func<double, double, bool> compare)
    {
        if (!actual.TryGetDouble(out var left)) return false;
        if (!double.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var right)) return false;
        return compare(left, right);
    }

    private sealed record DecisionEvaluation(
        bool Matched,
        string? Actual,
        string? ActualKind,
        string Reason);
}
