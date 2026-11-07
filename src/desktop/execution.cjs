const providerIds = new Set(['claude-code', 'codex', 'ollama', 'lmstudio']);
const reasoningEfforts = {
  'claude-code': ['low', 'medium', 'high', 'xhigh', 'max'],
  codex: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'],
};

function normalizeProvider(value) {
  if (value == null || value === '') return null;
  if (typeof value !== 'string') throw new Error('Invalid provider.');
  const id = value.trim().toLowerCase();
  if (!id) return null;
  if (id === 'claude') return 'claude-code';
  if (!providerIds.has(id)) throw new Error('Unknown provider.');
  return id;
}

function normalizeModel(value) {
  if (value == null || value === '') return null;
  if (typeof value !== 'string') throw new Error('Invalid provider model.');
  const model = value.trim();
  if (!model) return null;
  if (!/^[a-z0-9][a-z0-9._:/-]{0,199}$/i.test(model)) throw new Error('Invalid provider model.');
  return model;
}

function normalizeEffort(value, providerId) {
  if (value == null || value === '') return null;
  if (typeof value !== 'string') throw new Error('Invalid reasoning effort.');
  const effort = value.trim().toLowerCase();
  if (!effort) return null;
  const codexLegacy = providerId === 'codex' && ['none', 'minimal'].includes(effort);
  if (!codexLegacy && !reasoningEfforts[providerId]?.includes(effort)) throw new Error(`Unsupported reasoning effort for ${providerId}.`);
  return effort;
}

function normalizeExecution(value) {
  if (value == null) return null;
  if (typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid execution preference.');
  const providerId = normalizeProvider(value.providerId);
  const model = normalizeModel(value.model);
  if (model && !providerId) throw new Error('Select a provider when saving a model.');
  if (value.effort && !providerId) throw new Error('Select a provider when saving a reasoning effort.');
  const effort = normalizeEffort(value.effort, providerId);
  return providerId ? { providerId, model, ...(effort ? { effort } : {}) } : null;
}

function resolveExecution(settings, preference, overrides = {}) {
  const saved = normalizeExecution(preference);
  const providerId = normalizeProvider(overrides.providerId) || saved?.providerId || settings.defaultProvider;
  const preferenceForProvider = saved?.providerId === providerId ? saved : null;
  const defaults = settings.providers[providerId];
  const defaultModel = normalizeModel(defaults?.model);
  const model = normalizeModel(overrides.model) || preferenceForProvider?.model || defaultModel;
  const savedEffort = !preferenceForProvider?.model || preferenceForProvider.model === model ? preferenceForProvider?.effort : null;
  const defaultEffort = !defaultModel || defaultModel === model ? defaults?.effort : null;
  const effort = normalizeEffort(overrides.effort, providerId) || savedEffort || normalizeEffort(defaultEffort, providerId);
  return { providerId, model, ...(effort ? { effort } : {}) };
}

module.exports = { reasoningEfforts, normalizeProvider, normalizeModel, normalizeEffort, normalizeExecution, resolveExecution };
