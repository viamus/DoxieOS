const providerIds = new Set(['claude-code', 'codex']);

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

function normalizeExecution(value) {
  if (value == null) return null;
  if (typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid execution preference.');
  const providerId = normalizeProvider(value.providerId);
  const model = normalizeModel(value.model);
  if (model && !providerId) throw new Error('Select a provider when saving a model.');
  return providerId ? { providerId, model } : null;
}

function resolveExecution(settings, preference, overrides = {}) {
  const saved = normalizeExecution(preference);
  const providerId = normalizeProvider(overrides.providerId) || saved?.providerId || settings.defaultProvider;
  const agentModel = saved?.providerId === providerId ? saved.model : null;
  const model = normalizeModel(overrides.model) || agentModel || normalizeModel(settings.providers[providerId]?.model);
  return { providerId, model };
}

module.exports = { normalizeProvider, normalizeModel, normalizeExecution, resolveExecution };
