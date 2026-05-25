using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Viamus.Doxie.Orchestrator.Agents;
using Viamus.Doxie.Orchestrator.Workflows;

namespace Viamus.Doxie.Orchestrator.Components.Shared;

public sealed record AgentRecommendation(AgentDescriptor Agent, int Score, string Reason);
public sealed record WorkflowRecommendation(WorkflowDefinition Workflow, int Score, string Reason);

public static class DoxieRecommendationAdvisor
{
    public static IReadOnlyList<AgentRecommendation> RecommendAgents(
        string? context,
        IEnumerable<AgentDescriptor> agents,
        int take = 6)
    {
        var terms = Tokenize(context);
        if (terms.Count == 0) return Array.Empty<AgentRecommendation>();

        return agents
            .Select(agent => ScoreAgent(agent, terms))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Agent.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }

    public static IReadOnlyList<WorkflowRecommendation> RecommendWorkflows(
        string? context,
        IEnumerable<WorkflowDefinition> workflows,
        int take = 6)
    {
        var terms = Tokenize(context);
        if (terms.Count == 0) return Array.Empty<WorkflowRecommendation>();

        return workflows
            .Select(workflow => ScoreWorkflow(workflow, terms))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Workflow.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }

    private static AgentRecommendation ScoreAgent(AgentDescriptor agent, IReadOnlyList<string> terms)
    {
        var score = 0;
        var hits = new List<string>();

        score += ScoreField(agent.Name, terms, 7, hits, "name");
        score += ScoreField(agent.Id, terms, 6, hits, "id");
        score += ScoreField(agent.SkillName, terms, 5, hits, "skill");
        score += ScoreField(agent.DisplayCategory, terms, 4, hits, "category");
        score += ScoreField(agent.Description, terms, 2, hits, "description");

        if (agent.Category is AgentCategory.Developer && HasAny(terms, "change", "pull", "code", "fix", "build"))
        {
            score += 5;
            hits.Add("developer flow");
        }

        return new AgentRecommendation(agent, score, Reason(hits));
    }

    private static WorkflowRecommendation ScoreWorkflow(WorkflowDefinition workflow, IReadOnlyList<string> terms)
    {
        var score = 0;
        var hits = new List<string>();

        score += ScoreField(workflow.Name, terms, 7, hits, "name");
        score += ScoreField(workflow.Id, terms, 6, hits, "id");
        score += ScoreField(workflow.Description, terms, 3, hits, "description");
        score += ScoreField(workflow.Trigger.Kind.ToString(), terms, 2, hits, "trigger");
        foreach (var node in workflow.Nodes)
        {
            score += ScoreField(node.Label, terms, 2, hits, "node");
            score += ScoreField(node.AgentId, terms, 3, hits, "agent");
            score += ScoreField(node.AgentMode, terms, 2, hits, "mode");
        }

        return new WorkflowRecommendation(workflow, score, Reason(hits));
    }

    private static int ScoreField(string? value, IReadOnlyList<string> terms, int weight, List<string> hits, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var haystack = NormalizeText(value);
        var matches = terms.Count(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (matches == 0) return 0;
        hits.Add(label);
        return matches * weight;
    }

    private static bool HasAny(IReadOnlyList<string> terms, params string[] candidates) =>
        candidates.Any(candidate => terms.Any(term => candidate.Contains(term, StringComparison.OrdinalIgnoreCase)
            || term.Contains(candidate, StringComparison.OrdinalIgnoreCase)));

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static int AddHit(List<string> hits, int score, string hit)
    {
        hits.Add(hit);
        return score;
    }

    private static string Reason(IReadOnlyList<string> hits) =>
        hits.Count == 0 ? "context match" : string.Join(", ", hits
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<string> Tokenize(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return Array.Empty<string>();

        var terms = Regex.Matches(NormalizeText(context), "[a-z0-9][a-z0-9-]{1,}")
            .Select(m => m.Value.Trim('-'))
            .Where(t => t.Length > 1)
            .Where(t => !StopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var term in terms.ToArray())
        {
            if (!Synonyms.TryGetValue(term, out var synonyms)) continue;
            foreach (var synonym in synonyms)
            {
                if (!StopWords.Contains(synonym))
                {
                    terms.Add(synonym);
                }
            }
        }

        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizeText(string value)
    {
        var normalized = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "uma", "uns", "para", "por",
        "com", "sem", "dos", "das", "que", "como", "quando", "onde", "sobre", "isso",
        "esse", "essa", "este", "esta", "tipo", "cara", "agent", "agents", "workflow",
        "workflows", "doxie", "quero", "preciso", "fazer", "ter", "vai",
        "vou", "poder", "novo", "nova", "dentro", "agora", "baseado", "objetivo"
    };

    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["incidente"] = ["incident", "observability", "observability"],
        ["incidentes"] = ["incident", "observability", "observability"],
        ["producao"] = ["production", "incident", "observability"],
        ["falha"] = ["failure", "bug", "incident"],
        ["falhas"] = ["failure", "bug", "incident"],
        ["erro"] = ["error", "bug", "logs"],
        ["erros"] = ["error", "bug", "logs"],
        ["monitoramento"] = ["monitor", "observability", "observability"],
        ["observabilidade"] = ["observability", "observability", "slo"],
        ["qualidade"] = ["quality", "quality-tool", "test"],
        ["cobertura"] = ["coverage", "quality-tool", "test"],
        ["seguranca"] = ["security", "vulnerability", "hotspot"],
        ["vulnerabilidade"] = ["vulnerability", "security", "quality-tool"],
        ["divida"] = ["debt", "quality", "maintainability"],
        ["tecnica"] = ["technical", "architecture"],
        ["produto"] = ["product", "scope", "roadmap"],
        ["escopo"] = ["scope", "product", "prioritization"],
        ["priorizacao"] = ["prioritization", "product", "backlog"],
        ["projeto"] = ["project", "program", "delivery"],
        ["scrum"] = ["scrum", "sprint", "delivery"],
        ["sprint"] = ["sprint", "scrum", "example"],
        ["backlog"] = ["backlog", "example", "product"],
        ["pull"] = ["pull", "change", "review"],
        ["change"] = ["pull", "request", "review"],
        ["build"] = ["pipeline", "ci", "example"],
        ["pipeline"] = ["pipeline", "ci", "example"],
        ["deploy"] = ["deployment", "release", "pipeline"],
        ["release"] = ["release", "deployment", "readiness"],
        ["arquitetura"] = ["architecture", "platform", "design"],
        ["integracao"] = ["integration", "api", "architecture"],
        ["banco"] = ["database", "schema", "sqlite"],
        ["dados"] = ["data", "database", "governance"],
        ["legado"] = ["legacy", "modernization", "architecture"],
    };
}
