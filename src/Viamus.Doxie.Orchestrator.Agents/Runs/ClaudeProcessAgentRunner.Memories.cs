using Viamus.Doxie.Orchestrator.Agents.Doxie;
using Viamus.Doxie.Orchestrator.Context;

namespace Viamus.Doxie.Orchestrator.Agents;

public sealed partial class ClaudeProcessAgentRunner
{
    /// <summary>
    /// Loads memories for the given skill via the injected
    /// <see cref="DoxieSkillReader"/>, evaluates each one's
    /// <c>condition</c> against the current dispatch context, and
    /// returns the matched ones sorted high → medium → low. Returns
    /// null when no reader is wired (some test paths) so the provider
    /// emits the unmemoised prompt; returns empty list when the skill
    /// has a memories folder but nothing matches the condition (the
    /// provider then skips the memory delimiter entirely).
    /// </summary>
    private IReadOnlyList<DoxieSkillMemory>? LoadAndFilterMemories(
        string skillName,
        string? modeId,
        string providerId,
        string? workspaceId)
    {
        var matched = new List<DoxieSkillMemory>();
        var ctx = new MemoryConditionEvaluator.Context(
            Mode: modeId ?? string.Empty,
            Provider: providerId,
            WorkspaceId: workspaceId ?? string.Empty);

        if (_skillReader is not null)
        {
            var all = _skillReader.LoadMemories(skillName);
            if (all.Count > 0)
            {
                matched.AddRange(MemoryConditionEvaluator.Filter(all, ctx));
            }
        }

        matched.AddRange(LoadWorkspaceMemories(workspaceId));

        return matched.Count == 0 ? null : matched;
    }

    private IReadOnlyList<DoxieSkillMemory> LoadWorkspaceMemories(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || _workspaceStore is null)
        {
            return Array.Empty<DoxieSkillMemory>();
        }

        var workspace = _workspaceStore.GetById(workspaceId.Trim());
        if (workspace is null) return Array.Empty<DoxieSkillMemory>();

        var entries = new List<DoxieSkillMemory>();
        var remaining = MaxWorkspaceMemoryChars;

        AddWorkspaceFileMemory(entries, workspace, "WORKSPACE.md", DoxieSkillMemoryPriority.High, ref remaining);

        var memoryDir = Path.Combine(workspace.Path, "memory");
        if (Directory.Exists(memoryDir))
        {
            foreach (var file in Directory.EnumerateFiles(memoryDir, "*.md", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(memoryDir, path), StringComparer.OrdinalIgnoreCase))
            {
                AddWorkspaceFileMemory(entries, workspace, file, DoxieSkillMemoryPriority.Medium, ref remaining);
                if (remaining <= 0) break;
            }
        }

        if (_libraryStore is not null)
        {
            foreach (var libraryId in workspace.MountedLibraryIds)
            {
                if (remaining <= 0) break;

                var library = _libraryStore.GetById(libraryId);
                if (library is null) continue;

                foreach (var memory in library.Memories.OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    if (remaining <= 0) break;

                    AddMemoryEntry(
                        entries,
                        fileName: $"workspace-{workspace.Id}-library-{library.Id}-{memory.FileName}",
                        name: $"Workspace library: {library.Name} / {memory.Name}",
                        priority: DoxieSkillMemoryPriority.Medium,
                        body: $"""
                            Source: workspace `{workspace.Id}` mounted library `{library.Id}` memory `{memory.FileName}`.

                            {memory.Content.Trim()}
                            """,
                        ref remaining);
                }
            }
        }

        return entries;
    }

    private static void AddWorkspaceFileMemory(
        List<DoxieSkillMemory> entries,
        Workspace workspace,
        string relativeOrAbsolutePath,
        DoxieSkillMemoryPriority priority,
        ref int remaining)
    {
        if (remaining <= 0) return;

        var path = Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(workspace.Path, relativeOrAbsolutePath);
        if (!File.Exists(path)) return;

        try
        {
            var rel = Path.GetRelativePath(workspace.Path, path).Replace('\\', '/');
            AddMemoryEntry(
                entries,
                fileName: $"workspace-{workspace.Id}-{rel.Replace('/', '-')}",
                name: $"Workspace: {rel}",
                priority: priority,
                body: $"""
                    Source: workspace `{workspace.Id}` file `{rel}`.

                    {File.ReadAllText(path).Trim()}
                    """,
                ref remaining);
        }
        catch (IOException) { /* best-effort: skip unreadable workspace memory */ }
        catch (UnauthorizedAccessException) { /* best-effort: skip unreadable workspace memory */ }
    }

    private static void AddMemoryEntry(
        List<DoxieSkillMemory> entries,
        string fileName,
        string name,
        DoxieSkillMemoryPriority priority,
        string body,
        ref int remaining)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0 || remaining <= 0) return;

        var effectiveBody = trimmed;
        if (effectiveBody.Length > remaining)
        {
            effectiveBody = effectiveBody[..remaining].TrimEnd()
                + "\n\n[Workspace memory truncated by DoxieOS to keep the CLI invocation under the Windows command-line limit.]";
            remaining = 0;
        }
        else
        {
            remaining -= effectiveBody.Length;
        }

        entries.Add(new DoxieSkillMemory(fileName, name, priority, Condition: null, Body: effectiveBody));
    }
}