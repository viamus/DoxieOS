import { useId, useState } from 'react';
import { Bot, Brain, Cpu, Pencil, Save, Sparkles } from 'lucide-react';
import { Button, ErrorNotice, ExecutionFields, Field, Loading, PageHeading, Panel, go, invoke, nameOf, useApp, useAsync } from './ui.jsx';
import { recordKey, recordRoute } from './automation.jsx';

function AgentEditorForm({ agent }) {
  const { refresh, notify } = useApp();
  const formId = useId();
  const [draft, setDraft] = useState(() => ({
    name: nameOf(agent), description: agent.description || '', body: agent.body || '',
    category: agent.category || 'Other', execution: { ...agent.execution },
  }));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const categorySealed = agent.categorySealed || agent.category === 'Doxie';
  const backTo = recordRoute('agents', agent);
  const buildQuery = new URLSearchParams({ edit: agent.id, catalog: agent.catalogId || 'default' });
  const change = (patch) => setDraft((current) => ({ ...current, ...patch }));
  const stayWhileSaving = (event) => { if (saving) event.preventDefault(); };

  async function save(event) {
    event.preventDefault();
    if (saving) return;
    setError('');
    if (!draft.name.trim() || !draft.body.trim()) {
      setError('Enter a name and instructions for the agent.');
      return;
    }
    const { providerId, model, effort } = draft.execution;
    const execution = providerId ? {
      providerId, ...(model?.trim() ? { model: model.trim() } : {}), ...(effort ? { effort } : {}),
    } : null;
    setSaving(true);
    try {
      const current = await invoke('catalog:get', 'agents', agent.id, agent.catalogId || 'default');
      if (!current) throw new Error('This agent is no longer available in its catalog. Return to Agents and reopen it.');
      const saved = await invoke('catalog:save', 'agents', {
        id: agent.id, catalogId: agent.catalogId || 'default', name: draft.name.trim(),
        description: draft.description.trim(), body: draft.body,
        ...(!categorySealed ? { category: draft.category.trim() || 'Other' } : {}), execution,
      });
      await refresh();
      notify('Agent saved');
      go(recordRoute('agents', saved || agent));
    } catch (problem) {
      setError(problem.message.replace(/^Error invoking remote method '[^']+': (?:Error: )?/, ''));
    } finally { setSaving(false); }
  }

  return <div className="page"><PageHeading title="Edit agent" icon={Pencil} subtitle={nameOf(agent)} back={{ to: backTo, label: 'Back to agent' }} actions={<>
    <Button to={`/agents/new?${buildQuery}`} icon={Sparkles} aria-disabled={saving || undefined} onClick={stayWhileSaving}>Edit with AI</Button>
    <Button to={backTo} aria-disabled={saving || undefined} onClick={stayWhileSaving}>Cancel</Button>
    <Button type="submit" form={formId} variant="primary" icon={Save} disabled={saving}>{saving ? 'Saving…' : 'Save agent'}</Button>
  </>} />
    <ErrorNotice error={error} />
    <form id={formId} className="stack" onSubmit={save} aria-busy={saving}>
      <fieldset className="agent-editor-fields stack" disabled={saving}>
        <Panel title="Agent details" icon={Bot}><div className="panel-body stack">
          <div className="form-grid"><Field label="Agent ID"><input readOnly value={agent.id} /></Field><Field label="Catalog"><input readOnly value={agent.catalogName || agent.catalogId || 'Default catalog'} /></Field></div>
          <div className="form-grid"><Field label="Name"><input required value={draft.name} onChange={(event) => change({ name: event.target.value })} /></Field><Field label="Category" hint={categorySealed ? 'This category is managed by the catalog.' : undefined}><input readOnly={categorySealed} value={draft.category} onChange={(event) => change({ category: event.target.value })} /></Field></div>
          <Field label="Description"><textarea aria-label="Description" rows={3} value={draft.description} onChange={(event) => change({ description: event.target.value })} /></Field>
          <Field label="Instructions"><textarea aria-label="Instructions" required rows={14} spellCheck={false} value={draft.body} onChange={(event) => change({ body: event.target.value })} /></Field>
        </div></Panel>
        <Panel title="Agent execution defaults" icon={Cpu}><div className="panel-body stack"><p className="muted">Choose the provider, model and reasoning effort used when this agent runs.</p><div className="form-grid"><ExecutionFields value={draft.execution} onChange={(execution) => change({ execution })} requireProvider /></div></div></Panel>
        <Panel title="Brain" icon={Brain}><div className="panel-body stack"><p>Every agent consults shared knowledge, explores its connections, and records reusable learnings through Brain.</p><div className="actions start"><span className="tag">Required connector · always connected</span><Button to="/libraries" variant="ghost">Explore Brain</Button></div></div></Panel>
      </fieldset>
    </form>
  </div>;
}

export function AgentEditor({ id, catalogId }) {
  const scope = JSON.stringify([id, catalogId || null]);
  const result = useAsync(async () => {
    try { return { scope, agent: await invoke('catalog:get', 'agents', id, catalogId) }; }
    catch (problem) { return { scope, error: problem.message.replace(/^Error invoking remote method '[^']+': (?:Error: )?/, '') }; }
  }, [id, catalogId]);
  const current = result.value?.scope === scope ? result.value : null;
  if (!current) return <div className="page"><PageHeading title="Edit agent" icon={Pencil} back={{ to: '/agents', label: 'Agents' }} /><Loading /></div>;
  if (current.error) return <div className="page"><PageHeading title="Could not load agent" icon={Bot} back={{ to: '/agents', label: 'Agents' }} /><ErrorNotice error={current.error} /></div>;
  const agent = current.agent;
  if (!agent) return <div className="page"><PageHeading title="Agent not found" icon={Bot} back={{ to: '/agents', label: 'Agents' }} /></div>;
  return <AgentEditorForm key={recordKey(agent)} agent={agent} />;
}
