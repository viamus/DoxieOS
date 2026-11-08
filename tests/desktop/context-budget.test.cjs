const assert = require('node:assert/strict');
const { test } = require('node:test');
const { measureText, selectContext, selectHistory } = require('../../src/desktop/context-budget.cjs');

test('context keeps required instructions intact and spends the remaining budget on whole priority entries', () => {
  const entries = [
    { source: 'optional/large.md', text: 'Large optional memory '.repeat(100), priority: 'high' },
    { source: 'WORKSPACE.md', text: 'Never publish without review.\nKeep exact spacing.  ', required: true },
    { source: 'memory/low.md', text: 'Low priority reference.', priority: 'low' },
    { source: 'memory/high.md', text: 'High priority reference.', priority: 'high' },
  ];
  const result = selectContext(entries, { maxBytes: 100 });
  assert.deepEqual(result.selected.map(item => item.source), ['WORKSPACE.md', 'memory/high.md']);
  assert.deepEqual(result.omitted.map(item => item.source), ['optional/large.md', 'memory/low.md']);
  assert(result.text.includes(entries[1].text));
  assert(result.metrics.selectedBytes <= 100);
  assert(result.metrics.savedBytes > 1500);
  assert.equal(result.metrics.omittedItems, 2);
});

test('duplicate optional context is removed while a required original and all UTF-8 instructions remain whole', () => {
  const required = 'Revisão obrigatória. 🐕\r\nContexto completo.  ';
  const entries = [
    { source: 'duplicate.md', text: required.replace(/\r\n/g, '\n').trim() },
    { source: 'required.md', text: required, required: true },
    { source: 'other.md', text: 'Another reference.' },
  ];
  const result = selectContext(entries, { maxBytes: 10 });
  assert.equal(result.text, required);
  assert.deepEqual(result.selected.map(item => item.source), ['required.md']);
  assert.deepEqual(result.omitted.map(item => item.source), ['other.md']);
  assert.equal(result.metrics.duplicateItems, 1);
  assert.equal(result.metrics.overBudget, true);
  assert.equal(result.metrics.selectedBytes, Buffer.byteLength(required));
  assert.equal(result.metrics.estimateMethod, 'utf8-bytes/4');
  assert.equal(measureText('🐕').bytes, 4);
  assert.equal(measureText('🐕').estimatedTokens, 1);
});

test('bounded history retains the original user goal and recent complete exchanges without mutating the transcript', () => {
  const messages = Array.from({ length: 12 }, (_, index) => [
    { role: 'user', text: `Requirement ${index}.\n  Keep this spacing.` },
    { role: 'assistant', text: `Response ${index}.` },
  ]).flat();
  const original = JSON.stringify(messages);
  const result = selectHistory(messages, { maxTurns: 7, maxBytes: 2048 });
  assert.deepEqual(result.selected, [messages[0], ...messages.slice(-6)]);
  assert.equal(result.omittedTurns, 17);
  assert.equal(JSON.stringify(messages), original);
  assert.equal(result.metrics.selectedBytes, Buffer.byteLength(JSON.stringify(result.selected)));
  assert(result.metrics.savedBytes > 0);
});

test('an oversized historical reply is archived rather than sliced, and an oversized original requirement is explicit', () => {
  const goal = { role: 'user', text: 'Original goal '.repeat(100) };
  const messages = [goal, { role: 'assistant', text: 'A very large generated draft. '.repeat(1000) }];
  const result = selectHistory(messages, { maxBytes: 200 });
  assert.deepEqual(result.selected, [goal]);
  assert.equal(result.omittedTurns, 1);
  assert.equal(result.metrics.overBudget, true);
  assert.equal(result.selected[0].text, goal.text);
});
