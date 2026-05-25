using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Viamus.Doxie.Orchestrator.Services;

internal static class OutputArtifactText
{
    public static string Clean(string text)
    {
        var primary = ExtractPrimaryBody(text);
        var lines = primary
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !LooksLikeProtocolEvent(line))
            .Where(line => !LooksLikeProcessTerminationNoise(line))
            .Where(line => !line.StartsWith("<!-- from:", StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', lines);
    }

    public static string ExtractMemoryHighlights(string path, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return string.Empty;
        }

        var lines = ExtractPrimaryBody(text)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !LooksLikeProtocolEvent(line))
            .Where(line => !LooksLikeProcessTerminationNoise(line))
            .Where(line => !line.StartsWith("<!-- from:", StringComparison.OrdinalIgnoreCase))
            .Where(line =>
                line.StartsWith("#", StringComparison.Ordinal)
                || line.StartsWith("-", StringComparison.Ordinal)
                || Regex.IsMatch(line, @"\b\d{1,2}:\d{2}\b")
                || Regex.IsMatch(line, @"\b\d{4}-\d{2}-\d{2}\b")
                || Regex.IsMatch(line, @"\b\d{1,2}/\d{1,2}/\d{4}\b"))
            .Take(80);

        return Compact(string.Join('\n', lines), maxChars);
    }

    public static int QualityScore(string excerpt)
    {
        var normalized = Normalize(excerpt);
        var score = 0;

        if (excerpt.Length >= 800) score += 8;
        if (excerpt.Length >= 1_800) score += 8;
        if (Regex.IsMatch(excerpt, @"\b\d{1,2}:\d{2}\b")) score += 12;
        if (Regex.IsMatch(excerpt, @"^\s*[-*]\s+", RegexOptions.Multiline)) score += 6;
        if (Regex.IsMatch(excerpt, @"^\s*#{1,3}\s+", RegexOptions.Multiline)) score += 4;

        if (LooksLikeNoData(normalized)) score -= 80;
        return score;
    }

    public static bool LooksLikeNoData(string text)
    {
        var normalized = Normalize(text);
        return normalized.Contains("no upstream files to merge", StringComparison.Ordinal)
               || normalized.Contains("did not produce an indexable artifact", StringComparison.Ordinal)
               || normalized.Contains("nao ha dados", StringComparison.Ordinal)
               || normalized.Contains("nÃ£o ha dados", StringComparison.Ordinal)
               || normalized.Contains("nao ha eventos", StringComparison.Ordinal)
               || normalized.Contains("eventos confirmados: nenhum", StringComparison.Ordinal)
               || normalized.Contains("sem eventos confirmados", StringComparison.Ordinal)
               || normalized.Contains("snapshot upstream", StringComparison.Ordinal)
               || normalized.Contains("dados estao ausentes", StringComparison.Ordinal)
               || normalized.Contains("dados estÃ£o ausentes", StringComparison.Ordinal);
    }

    private static string ExtractPrimaryBody(string text)
    {
        var preferredMarkers = new[]
        {
            "<!-- from: output.md -->",
            "<!-- from: report.md -->",
            "<!-- from: result.md -->",
            "<!-- from: summary.md -->",
            "<!-- from: merged.md -->",
        };

        foreach (var marker in preferredMarkers)
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return text[(index + marker.Length)..];
            }
        }

        var generic = Regex.Match(
            text,
            @"<!--\s*from:\s*[^>]*(output|report|result|summary)[^>]*-->",
            RegexOptions.IgnoreCase);
        return generic.Success ? text[(generic.Index + generic.Length)..] : text;
    }

    private static bool LooksLikeProtocolEvent(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("type", out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeProcessTerminationNoise(string line)
    {
        var normalized = Normalize(line);
        return (normalized.Contains("processo com pid", StringComparison.Ordinal)
                && normalized.Contains("foi finalizado", StringComparison.Ordinal))
               || (normalized.Contains("process with pid", StringComparison.Ordinal)
                   && normalized.Contains("terminated", StringComparison.Ordinal));
    }

    private static string Compact(string? value, int maxChars)
    {
        var clean = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= maxChars) return clean;
        return clean[..Math.Max(0, maxChars - 1)].TrimEnd() + "...";
    }

    private static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
