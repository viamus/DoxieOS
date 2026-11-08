import { createContext, useContext, useEffect, useId, useRef, useState } from 'react';
import { AlertCircle, ArrowLeft, ChevronRight, CircleHelp, Inbox, LoaderCircle, Mic, MicOff, RotateCw, Search, X } from 'lucide-react';
import { messages } from './messages.js';
import { dictationSeconds, startDictationCapture } from './dictation-audio.js';

export const AppContext = createContext(null);
export const useApp = () => useContext(AppContext);
export const invoke = (action, ...args) => window.doxie.invoke(action, ...args);
export const href = (route) => `#${route}`;
export const go = (route) => { window.location.hash = route; };
export const part = (value) => encodeURIComponent(value);
export const active = (status) => ['queued', 'running', 'awaitingapproval', 'awaitinginput'].includes(status?.toLowerCase());
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
  'ollama-loopback': 'Local Ollama server', 'lmstudio-loopback': 'Local LM Studio server',
};
const customModelOption = '@custom-model-id';

const effortLevels = { none: 'None', minimal: 'Minimal', low: 'Low', medium: 'Medium', high: 'High', xhigh: 'Extra high', max: 'Max', ultra: 'Ultra' };
const effortScale = Object.keys(effortLevels);

export function useEffortLabel() {
  const t = useText();
  return (level) => t(`Effort.${level}`, effortLevels[level] || level);
}

function ReasoningEffortField({ levels, value, inherited, onChange }) {
  const t = useText();
  const effortLabel = useEffortLabel();
  const id = useId();
  const label = t('Effort.Label', 'Reasoning effort');
  const choices = effortScale.filter(level => levels.includes(level));
  if (!choices.length && !value) return <small className="effort-note">{t('Effort.Managed', 'Reasoning effort follows the provider settings.')}</small>;
  const selected = choices.indexOf(value || inherited);
  const unavailable = value && !choices.includes(value);
  return <fieldset className="effort-field" aria-describedby={unavailable || (inherited && !value) ? `${id}-hint` : undefined}>
    <legend>{label}</legend>
    <div className="effort-options" style={{ '--effort-columns': choices.length + 1 }}>
      {['', ...choices].map((level, index) => <label className="effort-option" key={level}>
        <input type="radio" name={id} value={level} checked={(value || '') === level} required={Boolean(unavailable)} onChange={() => onChange(level || null)} />
        <span aria-hidden="true" className={`effort-bar${level && index - 1 <= selected ? ' filled' : ''}`} style={{ '--effort-height': `${5 + 2.5 * effortScale.indexOf(level)}px` }} />
        <span>{level ? effortLabel(level) : t('Effort.Default', 'Default')}</span>
      </label>)}
    </div>
    {unavailable ? <small id={`${id}-hint`} role="status">{t('Effort.Unavailable', 'Choose an available effort level or restore the default.')}</small>
      : inherited && !value && <small id={`${id}-hint`}>{t('Effort.Inherited', 'Inherited effort')} · {effortLabel(inherited)}</small>}
  </fieldset>;
}

export function ModelField({ providerId, value, onChange, placeholder, defaultModel, disabled, required, hint, label = 'Model', effort, select = false }) {
  const t = useText();
  const modelsId = useId();
  const statusId = useId();
  const force = useRef(null);
  const [revision, setRevision] = useState(0);
  const [useCustomModel, setUseCustomModel] = useState(false);
  const canDiscover = Boolean(providerId && (!disabled || effort));
  const inventory = useAsync(() => {
    if (!canDiscover) return null;
    const refresh = force.current === providerId;
    if (refresh) force.current = null;
    return invoke('models:list', providerId, { refresh }).then((result) => ({ ...result, providerId }));
  }, [providerId, canDiscover, revision]);
  const current = inventory.value?.providerId === providerId ? inventory.value : null;
  const models = new Map(current?.models.map((model) => [model.id, model]));
  const source = modelSources[current?.source] || current?.source;
  const status = current?.status === 'ready' ? `${models.size} ${models.size === 1 ? 'model' : 'models'}` : current?.status === 'empty' ? 'No models reported' : 'Inventory unavailable';
  const summary = inventory.loading ? 'Checking model inventory…' : [status, source].filter(Boolean).join(' · ');
  const availableEfforts = (model) => models.get(model.trim() || defaultModel || (providerId === 'claude-code' ? 'default' : undefined))?.reasoningEfforts ?? effort?.levels ?? [];
  const updateModel = (model) => onChange(model, availableEfforts(model).includes(effort?.value) ? effort.value : null);
  const fieldProps = { 'aria-label': label, 'aria-describedby': canDiscover ? statusId : undefined, disabled, required };
  const customLabel = t('Models.CustomId', 'Custom model ID');
  const selectedModel = value || '';
  return <><div className="model-field">
    <Field label={label} hint={hint}>
      {select ? <select {...fieldProps} value={useCustomModel ? customModelOption : selectedModel} onChange={(event) => {
        const model = event.target.value;
        setUseCustomModel(model === customModelOption);
        if (model !== customModelOption) updateModel(model);
      }}>
        <option value="">{t('Models.CliDefault', 'CLI default')}</option>
        {selectedModel && !models.has(selectedModel) && <option value={selectedModel}>{selectedModel} ({t('Models.NotInInventory', 'not in inventory')})</option>}
        {[...models.values()].map((model) => <option key={model.id} value={model.id}>{model.label || model.id}</option>)}
        <option value={customModelOption}>{customLabel}…</option>
      </select> : <>
        <input {...fieldProps} list={modelsId} value={selectedModel} maxLength={200} onChange={(event) => updateModel(event.target.value)} placeholder={placeholder || 'Provider default'} />
        <datalist id={modelsId}>{[...models.values()].map((model) => <option key={model.id} value={model.id} label={[model.label !== model.id && model.label, modelSources[model.source]].filter(Boolean).join(' · ') || undefined} />)}</datalist>
      </>}
    </Field>
    {canDiscover && <div className="model-inventory">
      <small id={statusId} role="status" title={current?.message || inventory.error || undefined}>{summary}</small>
      <IconButton type="button" icon={RotateCw} label="Refresh models" disabled={inventory.loading} onClick={() => { force.current = providerId; setRevision((current) => current + 1); }} />
    </div>}
  </div>
  {select && useCustomModel && <Field label={customLabel}>
    <input aria-label={customLabel} value={selectedModel} disabled={disabled} required maxLength={200} onChange={(event) => updateModel(event.target.value)} />
  </Field>}
  {effort && <ReasoningEffortField {...effort} levels={availableEfforts(selectedModel)} />}</>;
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
  const selectedModel = value?.model?.trim() || defaultModel;
  const inheritedEffort = (effectiveId === defaultId && (!inherited?.model || inherited.model === selectedModel) && inherited?.effort)
    || ((!provider?.model || provider.model === selectedModel) && provider?.effort);
  const modelDisabled = requireProvider && !providerId;
  const modelRequired = !modelDisabled && !defaultModel && Boolean(provider?.requiresModel || provider?.localBaseUrl);
  const modelHint = modelDisabled ? 'Select a provider to set a model override.' : modelRequired ? 'Select or enter a model for this local provider.' : undefined;
  return <>
    <Field label="Provider"><select aria-label="Provider" value={providerId} onChange={(event) => onChange({ providerId: event.target.value, model: '', effort: null })}>
      <option value="">{inheritedLabel}{defaultProvider ? ` · ${nameOf(defaultProvider)}` : ''}</option>
      {providerId && !state.providers.some(({ id }) => id === providerId) && <option value={providerId}>{providerId}</option>}
      {state.providers.map((item) => <option key={item.id} value={item.id}>{nameOf(item)}</option>)}
    </select></Field>
    <ModelField providerId={effectiveId} value={value?.model} disabled={modelDisabled} required={modelRequired} placeholder={defaultModel} defaultModel={defaultModel} hint={modelHint}
      onChange={(model, effort) => onChange({ providerId, model, effort })}
      effort={{ levels: provider?.reasoningEfforts, value: value?.effort, inherited: inheritedEffort,
        onChange: (effort) => onChange({ providerId: requireProvider && effort ? effectiveId : providerId, model: value?.model, effort }) }} />
  </>;
}

export function Status({ status = 'Unknown' }) {
  const key = String(status).toLowerCase();
  const tone = ['succeeded', 'success', 'available', 'enabled', 'completed', 'connected'].includes(key) ? 'green'
    : ['running', 'live', 'active'].includes(key) ? 'blue'
    : ['queued', 'awaitingapproval', 'awaitinginput', 'warning'].includes(key) ? 'yellow'
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
  const { state, lastEvent } = useApp();
  const t = useText();
  const recording = useRef(null);
  const controller = useRef(null);
  const request = useRef(0);
  const timer = useRef(null);
  const phaseRef = useRef('idle');
  const appendRef = useRef(append);
  const [phase, setPhase] = useState('idle');
  const [message, setMessage] = useState('');
  const [model, setModel] = useState(null);
  const statusId = useId();
  const requestedLanguage = navigator.language || state.settings.language || 'en-US';
  const language = requestedLanguage.toLowerCase().split('-')[0];
  const dictationLanguage = ['pt', 'es', 'en'].includes(language) ? language : 'en';
  const listening = phase === 'listening';
  const busy = ['checking', 'downloading', 'starting', 'transcribing'].includes(phase);
  appendRef.current = append;

  function show(next, text) {
    phaseRef.current = next;
    setPhase(next);
    setMessage(text);
  }

  function showModel(status) {
    setModel(status);
    if (status.state === 'ready') show('idle', t('Voice.Ready', 'Voice model is ready. Click the microphone to dictate.'));
    else if (status.state === 'downloading') {
      const percent = status.totalBytes ? Math.min(100, Math.round((status.receivedBytes || 0) * 100 / status.totalBytes)) : 0;
      show('downloading', `${t('Voice.ModelDownloading', 'Downloading voice model…')} ${percent}%`);
    } else {
      const size = status.totalBytes > 0 ? t('Voice.ModelSize', 'The download is about {size} MB.').replace('{size}', Math.ceil(status.totalBytes / 1024 / 1024)) : '';
      show('missing', [t('Voice.ModelMissing', 'Download the voice model once to transcribe on this device.'), size].filter(Boolean).join(' '));
    }
  }

  function showError(error) {
    const errors = {
      NotAllowedError: t('Voice.Permission', 'Allow microphone access in your system settings, then try dictation again.'),
      NotFoundError: t('Voice.Microphone', 'Connect a microphone and check that the system can hear it.'),
      NotReadableError: t('Voice.MicrophoneBusy', 'The microphone is in use or unavailable. Check it and try again.'),
      TimeoutError: t('Voice.StartTimeout', 'The microphone took too long to start. Try dictation again.'),
    };
    const detail = error?.message?.replace(/^Error invoking remote method '[^']+': (?:Error: )?/, '');
    show('error', errors[error?.name] || detail || t('Voice.Failed', 'Local dictation could not finish. Try again.'));
  }

  useEffect(() => {
    if (lastEvent?.type !== 'speech:changed' || !lastEvent.status) return;
    setModel(lastEvent.status);
    if (['checking', 'missing', 'downloading'].includes(phaseRef.current)) showModel(lastEvent.status);
  }, [lastEvent]);

  useEffect(() => () => {
    request.current++;
    clearInterval(timer.current);
    controller.current?.abort();
    const capture = recording.current;
    recording.current = null;
    void capture?.cancel();
  }, []);

  async function install() {
    if (['checking', 'downloading', 'starting', 'listening', 'transcribing'].includes(phaseRef.current)) return;
    const currentRequest = ++request.current;
    show('downloading', t('Voice.ModelDownloading', 'Downloading voice model…'));
    try {
      const status = await invoke('speech:install');
      if (currentRequest === request.current) showModel(status);
    } catch (error) {
      if (currentRequest === request.current) {
        setModel(current => ({ ...current, state: 'missing' }));
        showError(error);
      }
    }
  }

  async function finish() {
    const capture = recording.current;
    if (!capture) return;
    recording.current = null;
    clearInterval(timer.current);
    const currentRequest = request.current;
    show('transcribing', t('Voice.Transcribing', 'Transcribing locally…'));
    try {
      const samples = await capture.stop();
      if (currentRequest !== request.current) return;
      if (samples.length < 1600) {
        show('idle', t('Voice.NoSpeech', 'No speech was detected. Try dictation again.'));
        return;
      }
      const result = await invoke('speech:transcribe', { samples, language: dictationLanguage });
      if (currentRequest !== request.current) return;
      const text = result.text?.trim();
      if (text) appendRef.current(text);
      show('idle', text ? t('Voice.Added', 'Dictation added to your message.') : t('Voice.NoSpeech', 'No speech was detected. Try dictation again.'));
    } catch (error) {
      if (currentRequest === request.current) showError(error);
    }
  }

  async function toggle() {
    if (recording.current) { void finish(); return; }
    if (['checking', 'downloading', 'starting', 'transcribing'].includes(phaseRef.current)) return;
    const currentRequest = ++request.current;
    show('checking', t('Voice.Checking', 'Checking local dictation…'));
    try {
      const status = await invoke('speech:status');
      if (currentRequest !== request.current) return;
      setModel(status);
      if (status.state !== 'ready') { showModel(status); return; }
      if (status.busy) { show('idle', t('Voice.Busy', 'Another transcription is running. Try dictation again when it finishes.')); return; }
      show('starting', t('Voice.Starting', 'Starting the microphone…'));
      const abort = new AbortController();
      controller.current = abort;
      const capture = await startDictationCapture(() => { void finish(); }, abort.signal);
      if (currentRequest !== request.current) { await capture.cancel(); return; }
      recording.current = capture;
      const started = performance.now();
      const listeningMessage = () => `${t('Voice.Listening', 'Listening… Click the microphone again to finish.')} ${Math.min(dictationSeconds, Math.floor((performance.now() - started) / 1000))}/${dictationSeconds}s`;
      show('listening', listeningMessage());
      timer.current = setInterval(() => setMessage(listeningMessage()), 1000);
    } catch (error) {
      if (currentRequest === request.current && error?.name !== 'AbortError') showError(error);
    }
  }

  return <span className="voice-input" data-phase={phase}>
    <IconButton icon={listening ? MicOff : busy ? LoaderCircle : Mic} label={listening ? 'Stop dictation' : 'Start local dictation'} aria-pressed={listening} aria-describedby={message ? statusId : undefined} disabled={busy} onClick={toggle} />
    {message && <span className="voice-feedback" id={statusId} role="status">{message}
      {phase === 'downloading' && model?.totalBytes > 0 && <progress aria-label={t('Voice.ModelProgress', 'Voice model download')} value={model.receivedBytes || 0} max={model.totalBytes} />}
      {model?.state === 'missing' && !busy && <Button onClick={install}>{t('Voice.DownloadModel', 'Download voice model')}</Button>}
    </span>}
  </span>;
}
