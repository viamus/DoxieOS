import { useState } from 'react';
import { Cpu, Plug } from 'lucide-react';
import { AgentPage, recordRoute } from './automation.jsx';
import { Button, Empty, ItemRow, PageHeading, Panel, SearchBox, Tag, nameOf, selectModes, useApp, useText } from './ui.jsx';

export function ConnectorsPage() {
  const { state } = useApp();
  const t = useText();
  const [search, setSearch] = useState('');
  const visible = state.connectors.filter((item) => `${nameOf(item)} ${item.id} ${item.description || ''}`.toLowerCase().includes(search.trim().toLowerCase()));
  return <div className="page">
    <PageHeading title={t('Nav.Connectors', 'Connectors')} icon={Plug} subtitle={t('Connectors.Subtitle', 'Reusable steps for workflows, including native operations and model-backed Prompt.')} />
    <div className="toolbar"><SearchBox value={search} setValue={setSearch} placeholder={t('Connectors.Search', 'Search connectors…')} /></div>
    {visible.length ? <div className="cards catalog-cards">{visible.map((item) => <Panel key={item.id} title={nameOf(item)} icon={item.modelBacked ? Cpu : Plug} hint={item.id} actions={<Tag>{item.id === 'library' ? 'Required · every agent' : item.modelBacked ? 'Model-backed' : 'Native'}</Tag>}>
      <div className="panel-body catalog-card-body"><p>{item.description}</p><div className="actions start"><Button to={recordRoute('connectors', item)}>{item.modelBacked ? 'Configure run' : 'Inspect connector'}</Button></div></div>
    </Panel>)}</div> : <Panel><Empty title={search ? 'No matching connectors' : 'No connectors available'} icon={Plug} /></Panel>}
  </div>;
}

export function ConnectorPage({ id, runId }) {
  const { state } = useApp();
  const t = useText();
  const connector = state.connectors.find((item) => item.id === id);
  if (connector?.modelBacked) return <AgentPage id={id} runId={runId} connector />;
  const modes = Object.entries(selectModes(connector));
  return <div className="page">
    <PageHeading title={nameOf(connector) || 'Connector not found'} subtitle={connector?.description} icon={Plug} back={{ to: '/connectors', label: t('Nav.Connectors', 'Connectors') }} actions={connector && <Tag>{connector.id === 'library' ? 'Required · every agent' : 'Native connector'}</Tag>} />
    {connector && <Panel title="Workflow inputs" icon={Plug} hint="Choose this connector from the workflow editor’s node palette.">
      <div className="panel-body stack">{modes.length ? modes.map(([modeId, mode]) => <div key={modeId} className="stack">
        <h3>{mode.label || mode.name || modeId}</h3>{mode.description && <p className="muted">{mode.description}</p>}
        {(mode.fields || []).length ? <div className="row-list">{mode.fields.map((field) => <ItemRow key={field.id} title={field.label || field.id} detail={field.description || field.placeholder || field.id}>
          {field.lookup && <Tag>{field.lookup}</Tag>}{field.required && <Tag>Required</Tag>}
        </ItemRow>)}</div> : <p className="muted">This mode uses workflow context.</p>}
      </div>) : <p className="muted">This connector uses workflow context.</p>}</div>
    </Panel>}
  </div>;
}
