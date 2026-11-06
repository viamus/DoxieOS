import { createContext, useContext, useEffect, useId, useRef, useState } from 'react';
import { AlertCircle, ArrowLeft, ChevronRight, CircleHelp, Inbox, LoaderCircle, Mic, MicOff, RotateCw, Search, X } from 'lucide-react';
import { messages } from './messages.js';

export const AppContext = createContext(null);
export const useApp = () => useContext(AppContext);
export const invoke = (action, ...args) => window.doxie.invoke(action, ...args);
export const href = (route) => `#${route}`;
export const go = (route) => { window.location.hash = route; };
export const part = (value) => encodeURIComponent(value);
export const active = (status) => ['queued', 'running', 'awaitingapproval'].includes(status?.toLowerCase());
export const slug = (text) => text.normalize('NFKD').toLowerCase().replace(/[\u0300-\u036f]/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
export const nameOf = (item) => item?.displayName || item?.name || item?.label || item?.id || '';
export const split = (value) => value.split(/[\n,]/).map((part) => part.trim()).filter(Boolean);

export function useText() {
  const language = useApp()?.state.settings?.language || 'en';
  return (key, fallback = key) => messages[language]?.[key] ?? fallback;
}

export function useAsync(load, keys) {
  const [result, setResult] = useState({ loading: true });
  useEffect(() => {
    let subscribed = true;
    setResult({ loading: true });
    Promise.resolve().then(load).then(
      (value) => { if (subscribed) setResult({ value }); },
      (error) => { if (subscribed) setResult({ error: error.message }); },
    );
    return () => { subscribed = false; };
  }, keys);
  return result;
}

export function Button({ icon: Icon, children, variant = 'secondary', to, className = '', ...props }) {
  const content = <>{Icon && <Icon size={15} strokeWidth={1.8} />}{children}</>;
  const styles = `button ${variant} ${className}`;
  return to ? <a className={styles} href={href(to)} {...props}>{content}</a> : <button type="button" className={styles} {...props}>{content}</button>;
}

export function IconButton({ icon, label, ...props }) {
  return <Button className="icon-button" variant="ghost" icon={icon} title={label} aria-label={label} {...props} />;
}

export function PageHeading({ icon: Icon, title, subtitle, eyebrow, actions, back }) {
  return <header className="page-heading">
    <div className="page-heading-copy">
      {back && <a className="back-link" href={href(back.to)}><ArrowLeft size={14} /> {back.label}</a>}
      {eyebrow && <div className="eyebrow">{eyebrow}</div>}
      <h1>{Icon && <span className="title-icon"><Icon size={20} strokeWidth={1.65} /></span>}{title}</h1>
      {subtitle && <p>{subtitle}</p>}
    </div>
    {actions && <div className="actions">{actions}</div>}
  </header>;
}

export function Panel({ title, hint, icon: Icon, actions, children, className = '' }) {
  return <section className={`panel ${className}`}>
    {(title || actions) && <header className="panel-header"><div className="panel-heading"><h2>{Icon && <Icon size={17} strokeWidth={1.65} />}{title}</h2>{hint && <p>{hint}</p>}</div>{actions && <div className="actions">{actions}</div>}</header>}
    {children}
  </section>;
}

export function Empty({ title, detail, icon: Icon = Inbox, action, compact = false }) {
  return <div className={`empty ${compact ? 'compact' : ''}`}><span className="empty-icon"><Icon size={19} strokeWidth={1.65} /></span><strong>{title}</strong>{detail && <p>{detail}</p>}{action}</div>;
}

export function ErrorNotice({ error }) {
  return error && <div className="error-notice" role="alert"><AlertCircle size={17} /> <span>{error}</span></div>;
}

export function Loading() {
  const t = useText();
  return <div className="loading" role="status"><LoaderCircle size={18} className="spinner" />{t('Common.Loading', 'Loading…')}</div>;
}

export function SearchBox({ value, setValue, placeholder }) {
  const t = useText();
  return <label className="search"><Search size={16} /><input type="search" aria-label={placeholder || t('Common.Search', 'Search')} placeholder={placeholder || t('Common.Search', 'Search')} value={value} onChange={(event) => setValue(event.target.value)} /></label>;
}

export function Field({ label, hint, children }) {
  return <label className="field"><span>{label}</span>{children}{hint && <small>{hint}</small>}</label>;
}

const modelSources = {
  'claude-cli-advertised': 'Claude Code CLI', 'claude-profile': 'Claude Code profile',
  'claude-host-settings': 'Installed Claude settings', 'claude-host-cache': 'Installed Claude cache',
  'claude-workbench-settings': 'DoxieOS Claude settings', 'claude-workbench-cache': 'DoxieOS Claude cache',
  'codex-host-catalog': 'Installed Codex catalog', 'codex-host-cache': 'Installed Codex cache', 'codex-host-settings': 'Installed Codex settings',
  'codex-workbench-catalog': 'DoxieOS Codex catalog', 'codex-workbench-cache': 'DoxieOS Codex cache', 'codex-workbench-settings': 'DoxieOS Codex settings',
  'ollama-loopback': 'Local Ollama server', 'lmstudio-loopback': 'Local LM Studio server', configured: 'DoxieOS settings',
};

export function ModelField({ providerId, value, onChange, suggestions, placeholder, disabled, required, hint, label = 'Model' }) {
  const { state } = useApp();
  const modelsId = useId();
  const statusId = useId();
  const force = useRef(null);
  const [revision, setRevision] = useState(0);
  const canDiscover = Boolean(providerId && !disabled);
  const inventory = useAsync(() => {
    if (!canDiscover) return null;
    const refresh = force.current === providerId;
    if (refresh) force.current = null;
    return invoke('models:list', providerId, { refresh }).then((result) => ({ ...result, providerId }));
  }, [providerId, canDiscover, revision]);
  const current = inventory.value?.providerId === providerId ? inventory.value : null;
  const configured = suggestions || state.settings.providers?.[providerId]?.models || [];
  const detected = current?.models || [];
  const models = new Map();
  for (const model of [...detected, ...(Array.isArray(configured) ? configured : [])]) {
    const id = typeof model === 'string' ? model : model.id;
    if (id && !models.has(id)) models.set(id, typeof model === 'string' ? { id } : model);
  }
  const source = modelSources[current?.source] || current?.source;
  const status = current?.status === 'ready' ? `${detected.length} ${detected.length === 1 ? 'model' : 'models'}` : current?.status === 'empty' ? 'No models reported' : 'Inventory unavailable';
  const summary = inventory.loading ? 'Checking model inventory…' : [status, source, current?.status !== 'ready' && models.size ? `${models.size} configured` : null].filter(Boolean).join(' · ');
  return <div className="model-field">
    <Field label={label} hint={hint}>
      <input aria-label={label} aria-describedby={canDiscover ? statusId : undefined} list={modelsId} value={value || ''} disabled={disabled} required={required} maxLength={200} onChange={(event) => onChange(event.target.value)} placeholder={placeholder || 'Provider default'} />
      <datalist id={modelsId}>{[...models.values()].map((model) => <option key={model.id} value={model.id} label={[model.label !== model.id && model.label, modelSources[model.source]].filter(Boolean).join(' · ') || undefined} />)}</datalist>
    </Field>
    {canDiscover && <div className="model-inventory">
      <small id={statusId} role="status" title={current?.message || inventory.error || undefined}>{summary}</small>
      <IconButton icon={RotateCw} label="Refresh models" disabled={inventory.loading} onClick={() => { force.current = providerId; setRevision((current) => current + 1); }} />
    </div>}
  </div>;
}

export function ExecutionFields({ value, onChange, inherited, inheritedLabel = 'Application default', requireProvider = false }) {
  const { state } = useApp();
  const normalize = (id) => id === 'claude' ? 'claude-code' : id;
  const providerId = normalize(value?.providerId) || '';
  const defaultId = normalize(inherited?.providerId || state.settings.defaultProvider);
  const effectiveId = providerId || defaultId;
  const provider = state.settings.providers?.[effectiveId] || state.providers.find(({ id }) => id === effectiveId);
  const defaultProvider = state.providers.find(({ id }) => id === defaultId);
  const defaultModel = (effectiveId === defaultId && inherited?.model) || provider?.model;
  const modelDisabled = requireProvider && !providerId;
  const modelRequired = !modelDisabled && !defaultModel && Boolean(provider?.requiresModel || provider?.localBaseUrl);
  const modelHint = modelDisabled ? 'Select a provider to set a model override.' : modelRequired ? 'Select or enter a model for this local provider.' : undefined;
  return <>
    <Field label="Provider"><select aria-label="Provider" value={providerId} onChange={(event) => onChange({ providerId: event.target.value, model: '' })}>
      <option value="">{inheritedLabel}{defaultProvider ? ` · ${nameOf(defaultProvider)}` : ''}</option>
      {providerId && !state.providers.some(({ id }) => id === providerId) && <option value={providerId}>{providerId}</option>}
      {state.providers.map((item) => <option key={item.id} value={item.id}>{nameOf(item)}</option>)}
    </select></Field>
    <ModelField providerId={effectiveId} value={value?.model} suggestions={provider?.models} disabled={modelDisabled} required={modelRequired} placeholder={defaultModel} hint={modelHint} onChange={(model) => onChange({ providerId, model })} />
  </>;
}

export function Status({ status = 'Unknown' }) {
  const key = String(status).toLowerCase();
  const tone = ['succeeded', 'success', 'available', 'enabled', 'completed', 'connected'].includes(key) ? 'green'
    : ['running', 'live', 'active'].includes(key) ? 'blue'
    : ['queued', 'awaitingapproval', 'warning'].includes(key) ? 'yellow'
    : ['failed', 'error', 'rejected'].includes(key) ? 'red' : 'neutral';
  return <span className={`status ${tone}`}><i />{String(status).replace(/([a-z])([A-Z])/g, '$1 $2')}</span>;
}

export function Tag({ children }) { return <span className="tag">{children}</span>; }

export function DateTime({ value, compact = false }) {
  const language = useApp()?.state.settings?.language || 'en';
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return <time dateTime={date.toISOString()} title={date.toLocaleString(language)}>{date.toLocaleString(language, compact ? { hour: '2-digit', minute: '2-digit' } : { dateStyle: 'medium', timeStyle: 'short' })}</time>;
}

export function ItemRow({ title, detail, icon: Icon, to, children, onClick }) {
  const content = <>{Icon && <span className="row-icon"><Icon size={17} strokeWidth={1.7} /></span>}<span className="item-copy"><strong>{title}</strong>{detail && <small>{detail}</small>}</span>{children}{(to || onClick) && <ChevronRight className="row-caret" size={15} />}</>;
  if (to) return <a className="item-row interactive" href={href(to)}>{content}</a>;
  if (onClick) return <button className="item-row interactive" type="button" onClick={onClick}>{content}</button>;
  return <div className="item-row">{content}</div>;
}

export function Modal({ title, icon: Icon = CircleHelp, close, children, wide = false }) {
  const [dialog, setDialog] = useState(null);
  useEffect(() => { if (dialog && !dialog.open) dialog.showModal(); }, [dialog]);
  return <dialog className={`modal ${wide ? 'wide' : ''}`} ref={setDialog} onCancel={(event) => { event.preventDefault(); close(); }}>
    <header><h2><Icon size={19} />{title}</h2><IconButton icon={X} label="Close" onClick={close} /></header>
    {children}
  </dialog>;
}

export function Confirm({ title, detail, close, confirm, label = 'Delete' }) {
  return <Modal title={title} close={close}><div className="modal-body"><p>{detail}</p></div><footer className="modal-footer"><Button onClick={close}>Cancel</Button><Button variant="danger" onClick={confirm}>{label}</Button></footer></Modal>;
}

const lookupKinds = new Set(['libraries', 'workspaces', 'agents', 'workflows']);

export function FieldInput({ field, value, onChange, catalogId = 'default', allowExpression = false }) {
  const { state } = useApp();
  const [preferExpression, setPreferExpression] = useState(null);
  const lookup = lookupKinds.has(field.lookup) && field.lookup;
  const catalogScoped = lookup === 'agents' || lookup === 'workflows';
  const records = lookup ? state[lookup].filter((record) => !catalogScoped || (record.catalogId || 'default') === catalogId) : [];
  const options = lookup ? records.map((record) => ({ value: record.id, label: nameOf(record) })) : Array.isArray(field.options) ? field.options : [];
  const props = { value: value ?? field.defaultValue ?? '', required: field.required, onChange: (event) => onChange(event.target.value) };
  const entries = options.map((option) => <option key={option.value ?? option} value={option.value ?? option}>{option.label ?? option}</option>);
  const unavailable = lookup && props.value && !records.some(({ id }) => id === props.value);
  const label = field.label || field.id;
  const expressionToggle = lookup && allowExpression;
  const expression = expressionToggle && (preferExpression ?? String(props.value).includes('{{'));
  const input = <Field label={`${label}${field.required ? ' *' : ''}`} hint={field.description || (lookup && !records.length ? `No ${lookup} available${catalogScoped ? ' in this catalog' : ''}.` : undefined)}>
    {expression ? <input {...props} aria-label={label} placeholder="{{trigger.input-id}}" />
      : lookup || options.length ? <select {...props} aria-label={label}><option value="">{field.placeholder || 'Select…'}</option>{unavailable && <option value={props.value}>{props.value} (unavailable)</option>}{entries}</select>
      : field.type === 'textarea' || field.multiline ? <textarea {...props} placeholder={field.placeholder} rows={3} />
        : <input {...props} placeholder={field.placeholder} />}
  </Field>;
  return expressionToggle ? <div className="lookup-field">{input}<Button variant="ghost" onClick={() => setPreferExpression(!expression)}>{expression ? 'Choose an item' : 'Use expression'}</Button></div> : input;
}

export function selectModes(agent) {
  const modes = agent?.modes;
  if (Array.isArray(modes)) return Object.fromEntries(modes.filter((mode) => !mode.hidden).map((mode) => [mode.id || mode.name, mode]));
  return Object.fromEntries(Object.entries(modes ?? {}).filter(([, mode]) => !mode.hidden));
}

export function VoiceInput({ append }) {
  const { state, notify } = useApp();
  const Engine = window.SpeechRecognition || window.webkitSpeechRecognition;
  const recognition = useRef(null);
  const [listening, setListening] = useState(false);
  useEffect(() => () => recognition.current?.abort(), []);
  if (!Engine || !('processLocally' in Engine.prototype)) return null;
  function toggle() {
    if (recognition.current) { recognition.current.stop(); return; }
    const speech = new Engine();
    recognition.current = speech;
    speech.lang = state.settings.language === 'en' || !state.settings.language ? 'en-US' : state.settings.language;
    speech.processLocally = true;
    speech.continuous = true;
    speech.onresult = (event) => {
      const text = Array.from(event.results).slice(event.resultIndex).filter((result) => result.isFinal).map((result) => result[0].transcript).join(' ');
      if (text) append(text);
    };
    speech.onerror = (event) => { if (event.error !== 'aborted') notify(`Local dictation: ${event.error}`, 'error'); };
    speech.onend = () => { recognition.current = null; setListening(false); };
    try { speech.start(); setListening(true); } catch (error) { recognition.current = null; notify(error.message, 'error'); }
  }
  return <IconButton icon={listening ? MicOff : Mic} label={listening ? 'Stop dictation' : 'Start local dictation'} onClick={toggle} />;
}
