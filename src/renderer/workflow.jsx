import { useEffect, useMemo, useRef, useState } from 'react';
import { Activity, ArrowDownToLine, ArrowRight, BookOpen, Bot, Box, Check, Clock3, GitBranch, Grip, Layers3, Link2, Move, Pencil, Play, Plus, RotateCcw, Save, ShieldCheck, Square, Terminal, Trash2, Workflow, Wrench, X } from 'lucide-react';
import { Button, Confirm, DateTime, Empty, ErrorNotice, ExecutionFields, Field, FieldInput, IconButton, ItemRow, Loading, Modal, PageHeading, Panel, Status, Tag, active, go, invoke, nameOf, part, selectModes, slug, split, useApp, useAsync } from './ui.jsx';
import { Guidance, LogOutput, recordRoute } from './automation.jsx';
import { FilesBrowser } from './workbench.jsx';

const nodeKinds = ['Trigger', 'Agent', 'Aggregate', 'Output', 'Loop', 'Decision', 'Tool'];
const triggerKinds = ['Manual', 'Cron', 'Event', 'Webhook'];
const nodeKind = (kind) => nodeKinds[Number(kind)] || nodeKinds.find((value) => value.toLowerCase() === String(kind).toLowerCase()) || kind || 'Agent';
const triggerKind = (kind) => triggerKinds[Number(kind)] || triggerKinds.find((value) => value.toLowerCase() === String(kind).toLowerCase()) || kind || 'Manual';
const legacyToolKinds = { Aggregate: 'aggregate', Output: 'write-to-workspace', Loop: 'loop', Decision: 'if-else' };
const connectorTypes = {
  aggregate: { name: 'Aggregate', icon: Layers3 },
  loop: { name: 'Loop', icon: RotateCcw },
  'approval-gate': { name: 'Approval gate', icon: ShieldCheck },
  'if-else': { name: 'If / else', icon: GitBranch },
  'write-to-workspace': { name: 'Write to workspace', icon: ArrowDownToLine },
  library: { name: 'Library', icon: BookOpen },
  sandbox: { name: 'Sandbox', icon: Box },
  prompt: { name: 'Prompt', icon: Bot, modelBacked: true },
};
const toolIdOf = (node) => node?.toolId || legacyToolKinds[nodeKind(node?.kind)] || (Object.hasOwn(connectorTypes, node?.agentId) ? node.agentId : null);
const nodeIcon = (node) => toolIdOf(node) ? connectorTypes[toolIdOf(node)]?.icon || Wrench : nodeKind(node.kind) === 'Trigger' ? Play : Bot;
const nodeType = (node) => toolIdOf(node) ? connectorTypes[toolIdOf(node)]?.name || toolIdOf(node) : nodeKind(node.kind);
const catalogAgent = (agents, id, catalogId) => agents.find((item) => item.id === id && item.catalogId === catalogId) || agents.find((item) => item.id === id);
const width = 186;
const height = 68;

function layout(nodes, edges, force = false) {
  const columns = new Map();
  const getDepth = (id, visiting = new Set()) => {
    if (visiting.has(id)) return 0;
    const parents = edges.filter((edge) => edge.toNodeId === id).map((edge) => edge.fromNodeId);
    const next = new Set([...visiting, id]);
    return parents.length ? Math.max(...parents.map((parent) => getDepth(parent, next))) + 1 : 0;
  };
  const levels = new Map();
  for (const node of nodes) {
    const level = getDepth(node.id);
    levels.set(node.id, level);
    columns.set(level, [...(columns.get(level) || []), node.id]);
  }
  const tallest = Math.max(1, ...[...columns.values()].map((items) => items.length));
  return nodes.map((node) => {
    if (!force && (node.x || node.y)) return node;
    const level = levels.get(node.id);
    const siblings = columns.get(level);
    return { ...node, x: 32 + level * 238, y: 32 + siblings.indexOf(node.id) * 112 + (tallest - siblings.length) * 56 };
  });
}

function Graph({ nodes, edges, selected, select, updatePosition, nodeRuns = {} }) {
  const positions = useMemo(() => layout(nodes, edges), [nodes, edges]);
  const graph = useRef(null);
  const drag = useRef(null);
  const [preview, setPreview] = useState(null);
  const placed = positions.map((node) => preview?.id === node.id ? { ...node, ...preview } : node);
  const byId = new Map(placed.map((node) => [node.id, node]));
  const dimensions = { width: Math.max(560, ...placed.map(({ x }) => x + width + 32)), height: Math.max(210, ...placed.map(({ y }) => y + height + 32)) };
  const moveStart = (event, node) => {
    if (!updatePosition || event.button !== 0) return;
    drag.current = { id: node.id, x: node.x, y: node.y, startX: event.clientX, startY: event.clientY };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const move = (event) => {
    if (!drag.current) return;
    const { id, x, y, startX, startY } = drag.current;
    if (Math.abs(event.clientX - startX) + Math.abs(event.clientY - startY) < 4 && !preview) return;
    setPreview({ id, x: Math.max(14, Math.round(x + event.clientX - startX)), y: Math.max(14, Math.round(y + event.clientY - startY)) });
  };
  const moveEnd = () => {
    if (preview) updatePosition(preview.id, preview.x, preview.y);
    drag.current = null;
    setPreview(null);
  };
  if (!placed.length) return <Empty title="No nodes" icon={Workflow} detail="Add a node to start composing the graph." />;
  return <div className="graph-scroll" ref={graph}><div className="graph" style={dimensions}>
    <svg className="graph-edges" width={dimensions.width} height={dimensions.height} aria-hidden="true">
      <defs><marker id="workflow-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="currentColor" /></marker></defs>
      {edges.map((edge, index) => {
        const source = byId.get(edge.fromNodeId);
        const target = byId.get(edge.toNodeId);
        if (!source || !target) return null;
        const x1 = source.x + width;
        const x2 = target.x;
        const y1 = source.y + height / 2;
        const y2 = target.y + height / 2;
        const bend = Math.max(28, Math.abs(x2 - x1) / 2);
        return <g key={`${edge.fromNodeId}-${edge.toNodeId}-${index}`}><path className={selected === source.id || selected === target.id ? 'highlight-edge' : ''} d={`M ${x1} ${y1} C ${x1 + bend} ${y1} ${x2 - bend} ${y2} ${x2} ${y2}`} markerEnd="url(#workflow-arrow)" />{edge.condition && <text x={(x1 + x2) / 2} y={(y1 + y2) / 2 - 7}>{String(edge.condition)}</text>}</g>;
      })}
    </svg>
    {placed.map((node) => {
      const Icon = nodeIcon(node);
      const run = nodeRuns[node.id];
      return <button key={node.id} type="button" className={`graph-node ${selected === node.id ? 'selected' : ''} ${updatePosition ? 'draggable' : ''}`} style={{ left: node.x, top: node.y, width, height }} aria-pressed={selected === node.id} onClick={() => select(node.id)} onPointerDown={(event) => moveStart(event, node)} onPointerMove={move} onPointerUp={moveEnd} onPointerCancel={() => { drag.current = null; setPreview(null); }}>
        <span className="node-icon"><Icon size={17} /></span><span className="node-text"><strong>{node.label || node.id}</strong><small>{run?.status || nodeType(node)}</small></span>{updatePosition && <Grip className="node-grip" size={15} />}
      </button>;
    })}
  </div></div>;
}

function EdgeEditor({ nodes, edges, set }) {
  const [source, setSource] = useState('');
  const [target, setTarget] = useState('');
  const [condition, setCondition] = useState('');
  const label = (id) => nodes.find((node) => node.id === id)?.label || id;
  const decision = toolIdOf(nodes.find((node) => node.id === source)) === 'if-else';
  return <Panel title="Connections" icon={Link2}><div className="panel-body stack">
    <div className="form-grid edge-form"><Field label="From"><select value={source} onChange={(event) => { setSource(event.target.value); setCondition(''); }}><option value="">Select source</option>{nodes.map((node) => <option key={node.id} value={node.id}>{node.label || node.id}</option>)}</select></Field>
      <Field label="To"><select value={target} onChange={(event) => setTarget(event.target.value)}><option value="">Select destination</option>{nodes.filter((node) => node.id !== source).map((node) => <option key={node.id} value={node.id}>{node.label || node.id}</option>)}</select></Field>
      {decision && <Field label="Branch"><select value={condition} onChange={(event) => setCondition(event.target.value)}><option value="">Any</option><option value="true">True</option><option value="false">False</option></select></Field>}
      <Button icon={Link2} disabled={!source || !target || source === target || edges.some((edge) => edge.fromNodeId === source && edge.toNodeId === target && (edge.condition || '') === condition)} onClick={() => { set([...edges, { fromNodeId: source, toNodeId: target, condition: condition || null }]); setTarget(''); }}>Connect</Button>
    </div>
    {!!edges.length && <div className="row-list">{edges.map((edge, index) => <ItemRow key={`${edge.fromNodeId}/${edge.toNodeId}/${index}`} title={<span className="edge-label">{label(edge.fromNodeId)} <ArrowRight size={14} /> {label(edge.toNodeId)}</span>}>{edge.condition && <Tag>{String(edge.condition)}</Tag>}<IconButton icon={Trash2} label="Remove connection" onClick={() => set(edges.filter((_, position) => position !== index))} /></ItemRow>)}</div>}
  </div></Panel>;
}

function KeyValues({ values, change, label = 'Inputs' }) {
  const [newKey, setNewKey] = useState('');
  return <div className="stack key-values"><span>{label}</span>{Object.entries(values || {}).map(([key, value]) => <div className="input-action" key={key}><code>{key}</code><input aria-label={key} value={value ?? ''} onChange={(event) => change({ ...values, [key]: event.target.value })} /><IconButton icon={Trash2} label={`Remove ${key}`} onClick={() => change(Object.fromEntries(Object.entries(values).filter(([item]) => item !== key)))} /></div>)}
    <div className="input-action"><input aria-label={`New ${label.toLowerCase()} key`} value={newKey} placeholder="New key" onChange={(event) => setNewKey(event.target.value)} /><Button icon={Plus} disabled={!newKey.trim() || Object.hasOwn(values || {}, newKey.trim())} onClick={() => { change({ ...values, [newKey.trim()]: '' }); setNewKey(''); }}>Add</Button></div>
  </div>;
}

function NodeEditor({ node, nodes, catalogId, change, remove }) {
  const { state } = useApp();
  const kind = nodeKind(node.kind);
  const toolId = toolIdOf(node);
  const activeTool = state.connectors.find((connector) => connector.id === toolId);
  const activeAgent = !toolId && catalogAgent(state.agents, node.agentId, catalogId);
  const modelBacked = activeTool?.modelBacked || connectorTypes[toolId]?.modelBacked;
  const modes = selectModes(activeTool || activeAgent);
  const mode = toolId ? node.toolMode || node.agentMode : node.agentMode;
  const fields = modes[mode || Object.keys(modes)[0]]?.fields || [];
  const extra = Object.fromEntries(Object.entries(node.inputs || {}).filter(([key]) => !fields.some((field) => field.id === key)));
  const set = (patch) => change({ ...node, ...patch });
  const candidates = [...new Set(state.agents.map(({ id }) => id))].map((id) => catalogAgent(state.agents, id, catalogId));
  const modeField = Object.keys(modes).length > 0 && <Field label="Mode"><select aria-label="Mode" value={mode || ''} onChange={(event) => set({ [toolId && kind === 'Tool' ? 'toolMode' : 'agentMode']: event.target.value })}><option value="">Default</option>{Object.keys(modes).map((item) => <option key={item} value={item}>{item}</option>)}</select></Field>;
  const workspaceField = <Field label="Execution workspace"><select aria-label="Execution workspace" value={node.workspaceId || ''} onChange={(event) => set({ workspaceId: event.target.value || null })}><option value="">Workflow workspace</option>{state.workspaces.map((workspace) => <option key={workspace.id} value={workspace.id}>{nameOf(workspace)}</option>)}</select></Field>;
  const inherited = activeTool?.execution || activeAgent?.execution;
  const executionFields = <ExecutionFields value={node.execution} inherited={inherited} inheritedLabel={inherited?.providerId ? toolId ? 'Connector default' : 'Agent default' : 'Application default'} requireProvider onChange={({ providerId, model }) => set({ execution: providerId ? { providerId, ...(model ? { model } : {}) } : null })} />;
  return <Panel title="Selected node" icon={Move} actions={kind !== 'Trigger' && <IconButton icon={Trash2} label="Remove node" onClick={remove} />}><div className="panel-body stack">
    <div className="form-grid"><Field label="Node ID"><input readOnly value={node.id} /></Field><Field label="Label"><input required value={node.label || ''} onChange={(event) => set({ label: event.target.value })} /></Field></div>
    {kind === 'Agent' && !toolId && <div className="form-grid">
      <Field label="Agent"><select required value={node.agentId || ''} onChange={(event) => { const agentId = event.target.value; set({ agentId, agentMode: Object.keys(selectModes(candidates.find((item) => item.id === agentId)))[0] || '' }); }}><option value="">Select agent</option>{node.agentId && !candidates.some(({ id }) => id === node.agentId) && <option value={node.agentId}>{node.agentId}</option>}{candidates.map((agent) => <option key={agent.id} value={agent.id}>{nameOf(agent)}</option>)}</select></Field>
      {modeField}{workspaceField}
      {executionFields}
    </div>}
    {toolId && <><div className="form-grid"><Field label="Connector"><input readOnly value={nameOf(activeTool) || nodeType(node)} /></Field>{modeField}{(modelBacked || ['library', 'sandbox'].includes(toolId)) && workspaceField}{modelBacked && executionFields}</div>{activeTool?.description && <p className="muted">{activeTool.description}</p>}</>}
    {toolId === 'write-to-workspace' && <div className="form-grid"><Field label="Output workspace"><select value={node.outputWorkspaceId || ''} onChange={(event) => set({ outputWorkspaceId: event.target.value || null })}><option value="">Workflow workspace</option>{state.workspaces.map((workspace) => <option key={workspace.id} value={workspace.id}>{nameOf(workspace)}</option>)}</select></Field><Field label="Output filename"><input value={node.outputFileName || ''} onChange={(event) => set({ outputFileName: event.target.value })} placeholder="report.md" /></Field></div>}
    {kind !== 'Trigger' && toolId !== 'loop' && <Field label="Loop membership"><select value={node.loopId || ''} onChange={(event) => set({ loopId: event.target.value || null })}><option value="">Outside a loop</option>{nodes.filter((item) => item.id !== node.id && toolIdOf(item) === 'loop').map((item) => <option key={item.id} value={item.id}>{item.label || item.id}</option>)}</select></Field>}
    {fields.map((field) => <FieldInput key={field.id} field={field} catalogId={catalogId} allowExpression value={node.inputs?.[field.id]} onChange={(value) => set({ inputs: { ...node.inputs, [field.id]: value } })} />)}
    {kind !== 'Trigger' && <KeyValues values={extra} change={(values) => set({ inputs: { ...Object.fromEntries(Object.entries(node.inputs || {}).filter(([key]) => fields.some((field) => field.id === key))), ...values } })} label="Additional inputs" />}
    {kind !== 'Trigger' && <p className="muted">Use <code>{'{{trigger.input-id}}'}</code> or <code>{'{{previous-node-id.output}}'}</code> to reference earlier values.</p>}
  </div></Panel>;
}

function TriggerEditor({ trigger, set }) {
  const kind = triggerKind(trigger.kind);
  const values = trigger.inputs || [];
  const change = (index, patch) => set({ ...trigger, inputs: values.map((field, position) => position === index ? { ...field, ...patch } : field) });
  const changeType = (index, value) => {
    const lookup = value.startsWith('lookup:') ? value.slice('lookup:'.length) : undefined;
    const type = lookup ? 'select' : value;
    change(index, { type, lookup, ...(value === 'select' ? {} : { options: undefined, optionsText: undefined }) });
  };
  return <Panel title="Trigger" icon={Clock3}><div className="panel-body stack">
    <div className="form-grid"><Field label="Trigger type"><select value={kind} onChange={(event) => set({ ...trigger, kind: event.target.value })}>{triggerKinds.map((item) => <option key={item}>{item}</option>)}</select></Field>
      {kind === 'Cron' && <Field label="Cron expression" hint="The schedule runs while DoxieOS is open."><input required value={trigger.cronExpression || ''} placeholder="0 9 * * 1-5" onChange={(event) => set({ ...trigger, cronExpression: event.target.value })} /></Field>}
      {kind === 'Event' && <Field label="Event name"><input value={trigger.eventName || ''} onChange={(event) => set({ ...trigger, eventName: event.target.value })} /></Field>}
      {kind === 'Webhook' && <Field label="Webhook path"><input value={trigger.webhookPath || ''} onChange={(event) => set({ ...trigger, webhookPath: event.target.value })} /></Field>}
    </div>
    {kind === 'Manual' && <><div className="actions start"><Button icon={Plus} onClick={() => set({ ...trigger, inputs: [...values, { id: '', label: '', required: false, type: 'text' }] })}>Add run input</Button></div>{values.map((field, index) => <div className="trigger-field" key={index}>
      <div className="form-grid">
        <Field label="Input ID"><input required pattern="[A-Za-z0-9_\-]+" value={field.id} onChange={(event) => change(index, { id: event.target.value })} /></Field>
        <Field label="Label"><input required value={field.label} onChange={(event) => change(index, { label: event.target.value })} /></Field>
        <Field label="Type"><select value={field.lookup ? `lookup:${field.lookup}` : field.type || 'text'} onChange={(event) => changeType(index, event.target.value)}>
          <option value="text">Text</option><option value="select">Select</option><option value="lookup:libraries">Library</option><option value="lookup:workspaces">Workspace</option><option value="lookup:agents">Agent in this catalog</option><option value="lookup:workflows">Workflow in this catalog</option>
        </select></Field>
        <Field label="Default value"><input value={field.defaultValue || ''} onChange={(event) => change(index, { defaultValue: event.target.value })} /></Field>
        <Field label="Placeholder"><input value={field.placeholder || ''} onChange={(event) => change(index, { placeholder: event.target.value })} /></Field>
        {field.type === 'select' && !field.lookup && <Field label="Options" hint="Separate values with commas."><input value={field.optionsText ?? (field.options || []).join(', ')} onChange={(event) => change(index, { optionsText: event.target.value, options: split(event.target.value) })} /></Field>}
      </div>
      <div className="actions start"><label className="check"><input type="checkbox" checked={field.required} onChange={(event) => change(index, { required: event.target.checked })} /> Required</label><IconButton icon={Trash2} label="Remove input field" onClick={() => set({ ...trigger, inputs: values.filter((_, position) => index !== position) })} /></div>
    </div>)}</>}
  </div></Panel>;
}

function hasCycle(nodes, edges) {
  const remaining = new Set(nodes.map(({ id }) => id));
  let ready;
  do {
    ready = [...remaining].filter((id) => !edges.some((edge) => edge.toNodeId === id && remaining.has(edge.fromNodeId)));
    ready.forEach((id) => remaining.delete(id));
  } while (ready.length);
  return remaining.size > 0;
}

function WorkflowForm({ existing }) {
  const { state, perform } = useApp();
  const [draft, setDraft] = useState(existing || { id: '', name: '', description: '', category: 'Other', trigger: { kind: 'Manual' }, nodes: [{ id: 'trigger', kind: 'Trigger', label: 'Trigger', x: 32, y: 72 }], edges: [], enabled: true, catalogId: 'default' });
  const [selected, setSelected] = useState(draft.nodes[0]?.id || '');
  const [add, setAdd] = useState('Agent');
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false);
  const set = (patch) => setDraft((current) => ({ ...current, ...patch }));
  const node = draft.nodes.find((item) => item.id === selected);
  const catalogs = state.catalogs || state.settings.catalogs || [];
  const connectors = state.connectors.length ? state.connectors : Object.entries(connectorTypes).map(([id, definition]) => ({ id, name: definition.name }));
  const setNode = (id, patch) => setDraft((current) => ({ ...current, nodes: current.nodes.map((item) => item.id === id ? { ...item, ...patch } : item) }));
  const addNode = () => {
    const tool = add === 'Agent' ? null : connectors.find(({ id }) => id === add);
    const rootId = tool?.id || slug(add);
    let number = 1;
    while (draft.nodes.some((item) => item.id === `${rootId}-${number}`)) number += 1;
    const id = `${rootId}-${number}`;
    const anchor = node || draft.nodes.at(-1);
    const entry = { id, kind: tool ? 'Tool' : 'Agent', label: nameOf(tool) || 'Agent', x: (anchor?.x || 30) + 238, y: anchor?.y || 50 };
    if (tool) { entry.toolId = tool.id; entry.toolMode = Object.keys(selectModes(tool))[0]; }
    set({ nodes: [...draft.nodes, entry] });
    setSelected(id);
  };
  const submit = async (event) => {
    event.preventDefault();
    setError('');
    const id = draft.id || slug(draft.name);
    if (!id) { setError('Enter a valid workflow ID.'); return; }
    if (hasCycle(draft.nodes, draft.edges)) { setError('The workflow contains a cycle. Remove a connection before saving.'); return; }
    const ids = (draft.trigger.inputs || []).map((field) => field.id);
    if (new Set(ids).size !== ids.length) { setError('Run input IDs must be unique.'); return; }
    setPending(true);
    const trigger = { ...draft.trigger, inputs: (draft.trigger.inputs || []).map(({ optionsText, ...field }) => field) };
    const payload = { ...draft, id, trigger, updatedAt: new Date().toISOString(), createdAt: draft.createdAt || new Date().toISOString(), enabled: !existing && triggerKind(trigger.kind) === 'Cron' ? false : draft.enabled };
    const result = await perform('catalog:save', ['workflows', payload], existing ? 'Workflow updated' : 'Workflow created');
    setPending(false);
    if (result) go(recordRoute('workflows', result.id ? result : payload));
  };
  return <form className="page" onSubmit={submit}><PageHeading title={existing ? `Edit ${nameOf(existing)}` : 'New workflow'} icon={Workflow} subtitle="Define when the workflow runs, connect its nodes and configure their inputs." back={{ to: existing ? recordRoute('workflows', existing) : '/workflows', label: 'Workflows' }} actions={<Button type="submit" variant="primary" icon={Save} disabled={pending}>Save workflow</Button>} />
    <ErrorNotice error={error} />
    <Panel title="Workflow details" icon={Workflow}><div className="panel-body stack">
      <div className="form-grid"><Field label="Name"><input required value={draft.name} onChange={(event) => set({ name: event.target.value })} /></Field><Field label="Workflow ID"><input disabled={!!existing} value={draft.id} pattern="[a-z0-9]+(-[a-z0-9]+)*" placeholder={slug(draft.name) || 'my-workflow'} onChange={(event) => set({ id: event.target.value })} /></Field><Field label="Category"><input value={draft.category || ''} onChange={(event) => set({ category: event.target.value })} /></Field>
        <Field label="Catalog"><select disabled={!!existing} value={draft.catalogId || 'default'} onChange={(event) => set({ catalogId: event.target.value })}>{!catalogs.some(({ id }) => id === 'default') && <option value="default">Default catalog</option>}{catalogs.map((catalog) => <option key={catalog.id} value={catalog.id}>{nameOf(catalog)}</option>)}</select></Field>
        <Field label="Workspace"><select value={draft.workspaceId || ''} onChange={(event) => set({ workspaceId: event.target.value || null })}><option value="">No workspace</option>{state.workspaces.map((workspace) => <option key={workspace.id} value={workspace.id}>{nameOf(workspace)}</option>)}</select></Field><Field label="Description"><input value={draft.description || ''} onChange={(event) => set({ description: event.target.value })} /></Field>
      </div>
      <div className="actions start"><label className="check"><input type="checkbox" checked={draft.enabled !== false} onChange={(event) => set({ enabled: event.target.checked })} /> Enabled</label><label className="check"><input type="checkbox" checked={!!draft.isPrivate} onChange={(event) => set({ isPrivate: event.target.checked })} /> Private</label></div>
    </div></Panel>
    <TriggerEditor trigger={draft.trigger || { kind: 'Manual' }} set={(trigger) => set({ trigger })} />
    <Panel title="Workflow graph" icon={GitBranch} hint="Drag a node to move it. Select a node to configure it." actions={<><select aria-label="Node type" value={add} onChange={(event) => setAdd(event.target.value)}><option value="Agent">Agent</option><optgroup label="Connectors">{connectors.map((connector) => <option key={connector.id} value={connector.id}>{nameOf(connector)}</option>)}</optgroup></select><Button icon={Plus} onClick={addNode}>Add node</Button><Button icon={Grip} onClick={() => set({ nodes: layout(draft.nodes, draft.edges, true) })}>Auto layout</Button></>}>
      <Graph nodes={draft.nodes} edges={draft.edges} selected={selected} select={setSelected} updatePosition={(id, x, y) => setNode(id, { x, y })} />
    </Panel>
    {node && <NodeEditor key={node.id} node={node} nodes={draft.nodes} catalogId={draft.catalogId} change={(next) => setNode(node.id, next)} remove={() => { set({ nodes: draft.nodes.filter((item) => item.id !== node.id).map((item) => item.loopId === node.id ? { ...item, loopId: null } : item), edges: draft.edges.filter((edge) => edge.fromNodeId !== node.id && edge.toNodeId !== node.id) }); setSelected(''); }} />}
    <EdgeEditor nodes={draft.nodes} edges={draft.edges} set={(edges) => set({ edges })} />
    <Panel title="Environment" icon={Terminal}><div className="panel-body"><KeyValues values={draft.env || {}} change={(env) => set({ env })} label="Environment variables" /></div></Panel>
    <div className="actions"><Button type="submit" variant="primary" icon={Save} disabled={pending}>Save workflow</Button></div>
  </form>;
}

export function WorkflowEditor({ id, catalogId }) {
  const { state } = useApp();
  const workflow = id && state.workflows.find((item) => item.id === id && (!catalogId || item.catalogId === catalogId));
  if (id && !workflow) return <div className="page"><PageHeading title="Workflow not found" icon={Workflow} back={{ to: '/workflows', label: 'Workflows' }} /></div>;
  return <WorkflowForm key={id || 'new'} existing={workflow} />;
}

function RunDialog({ workflow, close, submit }) {
  const fields = workflow.trigger?.inputs || [];
  const [values, setValues] = useState({});
  const [pending, setPending] = useState(false);
  return <Modal title={`Run ${nameOf(workflow)}`} icon={Play} close={close}><form onSubmit={async (event) => {
    event.preventDefault(); setPending(true);
    const result = await submit(Object.fromEntries(fields.map((field) => [field.id, values[field.id] ?? field.defaultValue ?? ''])));
    setPending(false); if (result) close();
  }}><div className="modal-body stack">{fields.map((field) => <FieldInput key={field.id} field={field} catalogId={workflow.catalogId} value={values[field.id]} onChange={(value) => setValues((old) => ({ ...old, [field.id]: value }))} />)}</div><footer className="modal-footer"><Button onClick={close}>Cancel</Button><Button type="submit" variant="primary" icon={Play} disabled={pending}>Run workflow</Button></footer></form></Modal>;
}

function DecisionDialog({ approval, node, close, submit }) {
  const [comment, setComment] = useState('');
  const [pending, setPending] = useState(false);
  const verb = approval ? 'Approve' : 'Reject';
  return <Modal title={`${verb} ${node.label || node.id}`} icon={ShieldCheck} close={close}><form onSubmit={async (event) => { event.preventDefault(); setPending(true); const result = await submit({ approve: approval, comment }); setPending(false); if (result !== false) close(); }}><div className="modal-body"><Field label="Decision comment"><textarea rows={3} value={comment} onChange={(event) => setComment(event.target.value)} placeholder="Optional context" /></Field></div><footer className="modal-footer"><Button onClick={close}>Cancel</Button><Button type="submit" variant={approval ? 'primary' : 'danger'} disabled={pending}>{verb}</Button></footer></form></Modal>;
}

export function WorkflowPage({ id, catalogId, runId }) {
  const { state, perform, lastEvent } = useApp();
  const workflow = state.workflows.find((item) => item.id === id && (!catalogId || item.catalogId === catalogId));
  const runs = state.workflowRuns.filter((run) => run.workflowId === id && (!catalogId || !run.catalogId || catalogId === run.catalogId)).sort((a, b) => (b.startedAt || '').localeCompare(a.startedAt || ''));
  const selectedRun = runId || runs[0]?.id;
  const event = lastEvent?.type === 'workflow:changed' && (lastEvent.run?.id || lastEvent.id) === selectedRun ? lastEvent : null;
  const result = useAsync(() => selectedRun && invoke('workflow:run', selectedRun), [selectedRun, event]);
  const run = result.value || event?.run || runs.find((item) => item.id === selectedRun);
  const nodes = workflow?.nodes || [];
  const edges = workflow?.edges || [];
  const [selectedNode, setSelectedNode] = useState(nodes[0]?.id || '');
  const [showRun, setShowRun] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [rerun, setRerun] = useState(false);
  const [guidance, setGuidance] = useState('');
  const [decision, setDecision] = useState(null);
  const node = nodes.find((item) => item.id === selectedNode) || nodes[0];
  const nodeRuns = Array.isArray(run?.nodeRuns) ? Object.fromEntries(run.nodeRuns.map((item) => [item.nodeId, item])) : run?.nodeRuns || {};
  const nodeRun = nodeRuns[node?.id];
  const showApproval = nodeRun?.status === 'AwaitingApproval';
  const start = async (input) => {
    const started = await perform('workflow:start', [id, { triggerInputs: input, catalogId: workflow.catalogId }]);
    if (started?.id) go(recordRoute('workflows', workflow, '', started.id));
    return started;
  };
  if (!workflow) return <div className="page"><PageHeading title="Workflow not found" icon={Workflow} back={{ to: '/workflows', label: 'Workflows' }} />{run && <Panel title={`Run ${run.id}`} icon={Terminal}>{Object.values(nodeRuns).map((entry) => <div key={entry.nodeId} className="panel-body"><h3>{entry.nodeId}</h3><LogOutput lines={entry.logs} /></div>)}</Panel>}</div>;
  return <div className="page"><PageHeading title={nameOf(workflow)} icon={Workflow} subtitle={workflow.description} back={{ to: '/workflows', label: 'Workflows' }} actions={<>
    <Button icon={ArrowDownToLine} onClick={() => perform('catalog:export', ['workflows', id, workflow.catalogId])}>Export</Button><Button to={recordRoute('workflows', workflow, 'edit')} icon={Pencil}>Edit</Button><Button to={`/workflows/new/chat?edit=${part(id)}&catalog=${part(workflow.catalogId || 'default')}`} icon={Pencil}>Edit with AI</Button><Button icon={Play} variant="primary" disabled={workflow.enabled === false} onClick={() => workflow.trigger?.inputs?.length ? setShowRun(true) : start({})}>Run workflow</Button>
  </>} />
  <div className="toolbar"><Status status={workflow.enabled === false ? 'Disabled' : 'Enabled'} /><Tag>{triggerKind(workflow.trigger?.kind)}</Tag>{workflow.trigger?.cronExpression && <Tag>{workflow.trigger.cronExpression}</Tag>}<Tag>{workflow.category || 'Other'}</Tag><label className="check"><input type="checkbox" checked={workflow.enabled !== false} onChange={(event) => perform('catalog:save', ['workflows', { ...workflow, enabled: event.target.checked }])} /> Enable workflow</label><IconButton icon={Trash2} label="Delete workflow" onClick={() => setConfirm(true)} /></div>
  <Panel title="Workflow graph" icon={GitBranch} actions={<Tag>{nodes.length} nodes</Tag>}><Graph nodes={nodes} edges={edges} selected={node?.id} select={setSelectedNode} nodeRuns={nodeRuns} /></Panel>
  <div className="dashboard-grid detail-grid"><Panel title="Run timeline" icon={Activity} actions={run && <><Status status={run.status} />{active(run.status) && <Button icon={Square} onClick={() => perform('workflow:stop', [run.id])}>Stop</Button>}</>}>
    {run ? <><div className="panel-body execution-meta"><DateTime value={run.startedAt} />{run.triggeredBy && <Tag>{run.triggeredBy}</Tag>}</div><div className="row-list">{nodes.map((item) => {
      const step = nodeRuns[item.id];
      const provider = state.providers.find(({ id }) => id === step?.providerId);
      const detail = [nodeType(item), nameOf(provider) || step?.providerId, step?.model].filter(Boolean).join(' · ');
      return <ItemRow key={item.id} title={item.label || item.id} detail={detail} icon={nodeIcon(item)} onClick={() => setSelectedNode(item.id)}><Status status={step?.status || 'Pending'} /></ItemRow>;
    })}</div>
      {active(run.status) && <div className="panel-body"><Guidance title="Guide the running workflow…" submit={(text) => perform('workflow:guide', [run.id, text], 'Guidance sent')} /></div>}
    </> : result.loading && selectedRun ? <Loading /> : <Empty title="No run selected" icon={Activity} />}
  </Panel><Panel title={node?.label || 'Node output'} icon={Terminal} actions={nodeRun && <>{nodeRun.hasTransientContext && <Tag>Transient handoff</Tag>}<Status status={nodeRun.status} /></>}>
    {nodeRun ? <><LogOutput lines={nodeRun.logs} />{nodeRun.outputSummary && <div className="panel-body"><pre>{typeof nodeRun.outputSummary === 'string' ? nodeRun.outputSummary : JSON.stringify(nodeRun.outputSummary, null, 2)}</pre></div>}
      {showApproval && <div className="panel-body stack"><strong>Awaiting your decision</strong><div className="actions start"><Button icon={Check} variant="primary" onClick={() => setDecision({ approval: true, node })}>Approve</Button><Button icon={X} variant="danger" onClick={() => setDecision({ approval: false, node })}>Reject</Button></div></div>}
      {!active(run.status) && <div className="panel-body"><Button icon={RotateCcw} onClick={() => setRerun(true)}>Rerun from this node</Button></div>}
    </> : <Empty compact title="No node output yet" icon={Terminal} />}
    <ErrorNotice error={result.error} />
  </Panel></div>
  {run && <Panel title="Files produced" icon={ArrowDownToLine} actions={<Button onClick={() => setShowFiles(!showFiles)}>{showFiles ? 'Hide files' : 'Browse files'}</Button>}>{showFiles && <FilesBrowser key={run.id} api="run-files" id={run.id} compact />}</Panel>}
  <Panel title="Recent runs" icon={Clock3}>{runs.length ? <div className="row-list">{runs.map((item) => <ItemRow key={item.id} title={<DateTime value={item.startedAt} />} detail={item.id} icon={Clock3} to={recordRoute('workflows', workflow, '', item.id)}><Status status={item.status} /></ItemRow>)}</div> : <Empty compact title="No workflow runs yet" icon={Clock3} />}</Panel>
  {showRun && <RunDialog workflow={workflow} close={() => setShowRun(false)} submit={start} />}
  {confirm && <Confirm title="Delete workflow" detail={`Remove ${nameOf(workflow)} from its catalog?`} close={() => setConfirm(false)} confirm={async () => { if (await perform('catalog:remove', ['workflows', id, workflow.catalogId], 'Workflow removed')) go('/workflows'); }} />}
  {decision && <DecisionDialog {...decision} close={() => setDecision(null)} submit={(value) => perform('workflow:approve', [run.id, decision.node.id, value])} />}
  {rerun && <Modal title={`Rerun from ${node.label || node.id}`} icon={RotateCcw} close={() => setRerun(false)}><form onSubmit={async (event) => { event.preventDefault(); const next = await perform('workflow:rerun', [run.id, node.id, guidance.trim()]); if (next?.id) { setRerun(false); setGuidance(''); go(recordRoute('workflows', workflow, '', next.id)); } }}><div className="modal-body"><Field label="Guidance"><textarea rows={3} value={guidance} onChange={(event) => setGuidance(event.target.value)} placeholder="Optional instruction for the new run" /></Field></div><footer className="modal-footer"><Button onClick={() => setRerun(false)}>Cancel</Button><Button variant="primary" type="submit" icon={RotateCcw}>Rerun</Button></footer></form></Modal>}
  </div>;
}
