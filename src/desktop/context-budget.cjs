const { createHash } = require('node:crypto');

const estimateMethod = 'utf8-bytes/4';
const defaultContextBytes = 24 * 1024;

// This is a planning estimate, not provider usage or a tokenizer count.
function measureText(text) {
  const bytes = Buffer.byteLength(String(text ?? ''), 'utf8');
  return { bytes, estimatedTokens: Math.ceil(bytes / 4), estimateMethod };
}

function comparisonMetrics(original, selected, extra = {}) {
  const before = measureText(original);
  const after = measureText(selected);
  return {
    originalBytes: before.bytes, selectedBytes: after.bytes,
    savedBytes: Math.max(0, before.bytes - after.bytes),
    estimatedOriginalTokens: before.estimatedTokens, estimatedSelectedTokens: after.estimatedTokens,
    estimatedSavedTokens: Math.max(0, before.estimatedTokens - after.estimatedTokens), estimateMethod,
    ...extra,
  };
}

function byteBudget(value, fallback) {
  return Number.isSafeInteger(value) && value >= 0 ? value : fallback;
}

function fingerprint(text) {
  return createHash('sha256').update(text.replace(/\r\n/g, '\n').trim()).digest('hex');
}

function selectContext(entries, { maxBytes = defaultContextBytes } = {}) {
  const budgetBytes = byteBudget(maxBytes, defaultContextBytes);
  const candidates = entries.filter(item => typeof item?.text === 'string' && item.text.trim())
    .map((item, index) => ({ item, index }));
  const selectedIndices = new Set();
  const duplicates = new Set();
  const seen = new Set();
  const weights = { high: 3, medium: 2, low: 1 };
  let bytes = 0;
  const priority = value => Number.isFinite(value) ? value : weights[String(value).toLowerCase()] || 0;
  const ranked = [...candidates].sort((a, b) => Number(Boolean(b.item.required)) - Number(Boolean(a.item.required))
    || priority(b.item.priority) - priority(a.item.priority) || a.index - b.index);
  for (const { item, index } of ranked) {
    const key = fingerprint(item.text);
    if (seen.has(key)) { duplicates.add(index); continue; }
    const cost = Buffer.byteLength(item.text) + (selectedIndices.size ? 2 : 0);
    if (!item.required && bytes + cost > budgetBytes) continue;
    seen.add(key);
    selectedIndices.add(index);
    bytes += cost;
  }
  const selected = candidates.filter(entry => selectedIndices.has(entry.index)).map(entry => entry.item);
  const omitted = candidates.filter(entry => !selectedIndices.has(entry.index) && !duplicates.has(entry.index)).map(entry => entry.item);
  const text = selected.map(item => item.text).join('\n\n');
  return {
    text, selected, omitted,
    metrics: comparisonMetrics(candidates.map(entry => entry.item.text).join('\n\n'), text, {
      selectedItems: selected.length, omittedItems: omitted.length, duplicateItems: duplicates.size,
      budgetBytes, overBudget: bytes > budgetBytes,
    }),
  };
}

function selectHistory(messages, { maxBytes = defaultContextBytes, maxTurns = 8 } = {}) {
  const budgetBytes = byteBudget(maxBytes, defaultContextBytes);
  const turnLimit = byteBudget(maxTurns, 8);
  const anchor = messages.findIndex(turn => turn.role === 'user');
  const selectedIndices = new Set(anchor < 0 ? [] : [anchor]);
  const groups = [];
  for (let index = 0; index < messages.length; index++) {
    if (!groups.length || messages[index].role === 'user') groups.push([]);
    groups.at(-1).push(index);
  }
  const serialized = indices => JSON.stringify([...indices].sort((a, b) => a - b).map(index => messages[index]));
  for (const group of groups.reverse()) {
    const next = new Set([...selectedIndices, ...group]);
    // Keep a contiguous suffix of whole exchanges. A large exchange can always
    // be recovered from the archived transcript; never send a sliced message.
    if (next.size > turnLimit || Buffer.byteLength(serialized(next)) > budgetBytes) break;
    for (const index of group) selectedIndices.add(index);
  }
  const selected = messages.filter((_, index) => selectedIndices.has(index));
  const text = JSON.stringify(selected);
  return {
    selected, omittedTurns: messages.length - selected.length,
    metrics: comparisonMetrics(JSON.stringify(messages), text, {
      selectedTurns: selected.length, omittedTurns: messages.length - selected.length,
      budgetBytes, overBudget: Buffer.byteLength(text) > budgetBytes,
    }),
  };
}

module.exports = { measureText, comparisonMetrics, selectContext, selectHistory };
