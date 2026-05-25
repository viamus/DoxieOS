namespace Viamus.Doxie.Orchestrator.Workflows;

public interface IWorkflowStore
{
    IReadOnlyList<WorkflowDefinition> ListAll();

    WorkflowDefinition? GetById(string id);

    /// <summary>
    /// Persists a workflow definition. Creates the
    /// <c>.doxie/workflows/&lt;id&gt;/workflow.json</c> file on first save and
    /// overwrites it on subsequent saves. Throws if the id is empty or
    /// not kebab-case (validation happens at the API edge too — the
    /// store enforces it again as defence-in-depth).
    /// </summary>
    void Save(WorkflowDefinition definition);

    /// <summary>
    /// Recursively deletes a workflow folder. Throws if the workflow
    /// doesn't exist.
    /// </summary>
    void Delete(string id);
}
