const assert = require('node:assert/strict');
const { test } = require('node:test');
const { extractKnowledge } = require('../../src/desktop/knowledge-extraction.cjs');

const extract = (content, options) => extractKnowledge({ id: 'source-fixture', content }, options);
function assertEvidence(result, content, id = 'source-fixture') {
  for (const item of [...result.entities, ...result.parameters]) {
    assert.equal(item.sourceMemoryId, id);
    for (const span of item.evidence.spans || [item.evidence]) {
      assert.equal(span.memoryId, id);
      assert.equal(content.slice(span.start, span.end), span.excerpt);
      assert.equal(span.startLine, content.slice(0, span.start).split('\n').length);
      assert.equal(span.endLine, content.slice(0, Math.max(span.start, span.end - 1)).split('\n').length);
      assert(!span.excerpt.includes('\uFFFD'));
    }
  }
}

test('explicit repository and JSON configuration become canonical entities and typed parameters with source spans', () => {
  const content =
    'Repository: https://github.com/example/service\n\n```json\n{"retryLimit":5,"maxTokens":8000,"reviewRequired":true}\n```';
  const result = extract(content);
  assert.equal(result.entities.length, 1);
  assert.equal(result.entities[0].entityType, 'repository');
  assert.equal(result.entities[0].content, 'repository: https://github.com/example/service');
  assert.equal(result.parameters.length, 1);
  assert.deepEqual(result.parameters[0].values, { maxTokens: 8000, retryLimit: 5, reviewRequired: true });
  assert.deepEqual(result.parameters[0].parameters, result.parameters[0].values);
  assert.equal(result.truncated, false);
  assertEvidence(result, content);
  assert.deepEqual(extract(content), result);
});

test('Portuguese and English explicit labels and directives preserve literal names and types', () => {
  const content = [
    'Organização: AB InBev',
    'Projeto: DoxieOS',
    'Organization: Example Labs',
    'Project: Brain',
    'Ferramenta: memory_search',
    'CLI: codex',
    'retryLimit = 5',
    'maxTokens: 8000',
    'revisãoObrigatória: sim',
    'reviewRequired: no',
    'API base URL: https://api.example.test/v1',
    'displayName = "São Paulo 🧠"',
  ].join('\r\n');
  const result = extract(content);
  for (const [entityType, value] of [
    ['organization', 'AB InBev'],
    ['project', 'DoxieOS'],
    ['organization', 'Example Labs'],
    ['project', 'Brain'],
    ['tool', 'memory_search'],
    ['executable', 'codex'],
  ])
    assert(result.entities.some((entity) => entity.entityType === entityType && entity.value === value));
  const values = Object.assign({}, ...result.parameters.map((item) => item.values));
  assert.equal(values.retryLimit, 5);
  assert.equal(values.maxTokens, 8000);
  assert.equal(values.revisãoObrigatória, true);
  assert.equal(values.reviewRequired, false);
  assert.equal(values['API base URL'], 'https://api.example.test/v1');
  assert.equal(values.displayName, 'São Paulo 🧠');
  assertEvidence(result, content);
});

test('nested JSON and code assignments preserve objects, arrays, nulls, booleans and numbers without evaluation', () => {
  const configuration = {
    service: { baseUrl: 'https://service.example.test/v2', retryLimit: 3, regions: ['BR', 'PT'], enabled: true },
    fallback: null,
    threshold: 0.75,
  };
  const content = `\`\`\`javascript\nconst configuration = ${JSON.stringify(configuration, null, 2)};\n\`\`\`\nconst retryLimit = 8;\nallowedFormats = ["json","md"]\nreviewRequired = false`;
  const result = extract(content);
  assert(
    result.parameters.some(
      (item) =>
        JSON.stringify(item.values) ===
        JSON.stringify({
          fallback: null,
          service: { baseUrl: 'https://service.example.test/v2', enabled: true, regions: ['BR', 'PT'], retryLimit: 3 },
          threshold: 0.75,
        }),
    ),
  );
  assert(result.parameters.some((item) => item.values.retryLimit === 8));
  assert(result.parameters.some((item) => JSON.stringify(item.values.allowedFormats) === '["json","md"]'));
  assert(result.parameters.some((item) => item.values.reviewRequired === false));
  assertEvidence(result, content);
  assert.equal(
    extract('retryLimit = process.env.RETRIES\nmaxTokens = calculateBudget()\nif (retryLimit === 5) {}').parameters
      .length,
    0,
  );
});

test('Markdown parameter tables use named columns and explicit types in either language', () => {
  const content = [
    '| Chave | Tipo | Valor |',
    '|---|---|---|',
    '| retryLimit | integer | 5 |',
    '| revisãoObrigatória | boolean | sim |',
    '| outputFormat | string | markdown |',
    '',
    '| Value | Parameter | Type |',
    '|---|---|---|',
    '| false | enabled | boolean |',
  ].join('\n');
  const result = extract(content);
  const values = Object.assign({}, ...result.parameters.map((item) => item.values));
  assert.deepEqual(values, { retryLimit: 5, revisãoObrigatória: true, outputFormat: 'markdown', enabled: false });
  assertEvidence(result, content);
  assert.equal(extract('| Person | City |\n|---|---|\n| Alice | London |').parameters.length, 0);
});

test('literal code references identify packages, files, executables and invoked tools without generic capitalization', () => {
  const content = [
    '```js',
    "import React from 'react';",
    "const toml = require('@iarna/toml');",
    "const helper = require('./src/helper.cjs');",
    '```',
    'Use `src/desktop/main.cjs` and "C:\\Program Files\\Tool\\run.exe".',
    'The configuration lives at /home/test/config.json and remains local.',
    '```bash',
    'npm run build',
    'go test ./...',
    '```',
    '$ git status',
    'Call `memory_search` or mcp__sample__lookup().',
    'Monday Windows Memory Map Brain Claude and Codex are mentioned in prose.',
    'make sure to review.',
    'go to settings.',
    "She came from 'London'.",
  ].join('\n');
  const result = extract(content);
  for (const [entityType, value] of [
    ['package', 'react'],
    ['package', '@iarna/toml'],
    ['file', 'src/desktop/main.cjs'],
    ['file', 'C:\\Program Files\\Tool\\run.exe'],
    ['file', '/home/test/config.json'],
    ['executable', 'npm'],
    ['executable', 'go'],
    ['executable', 'git'],
    ['tool', 'memory_search'],
    ['tool', 'mcp__sample__lookup'],
  ])
    assert(
      result.entities.some((entity) => entity.entityType === entityType && entity.value === value),
      `${entityType}: ${value}`,
    );
  assert(!result.entities.some((entity) => ['London', 'Windows', 'Monday', 'make'].includes(entity.value)));
  assert(!result.entities.some((entity) => entity.entityType === 'package' && entity.value.startsWith('./')));
  assertEvidence(result, content);
});

test('facts deduplicate deterministically while conflicting typed values from different sources remain independent', () => {
  const left = extractKnowledge({ id: 'left', content: 'Project: Brain\nretryLimit=3\nretryLimit=3' });
  const right = extractKnowledge({ id: 'right', content: '\n\nProject: Brain\nretryLimit=5' });
  assert.equal(left.parameters.length, 1);
  assert.equal(left.entities[0].key, right.entities[0].key);
  assert.equal(left.entities[0].content, right.entities[0].content);
  assert.notEqual(left.parameters[0].key, right.parameters[0].key);
  assert.notEqual(left.parameters[0].content, right.parameters[0].content);
  assert.equal(left.parameters[0].values.retryLimit, 3);
  assert.equal(right.parameters[0].values.retryLimit, 5);
  const first = extract('{"retryLimit":3,"maxTokens":8000}').parameters[0];
  const reordered = extract('{"maxTokens":8000,"retryLimit":3}').parameters[0];
  assert.equal(first.key, reordered.key);
  assert.equal(first.content, reordered.content);
});

test('credential fields and concrete secret values are excluded recursively without poisoning safe facts or evidence', () => {
  const concrete = 'sk' + '-' + 'x'.repeat(30);
  const content = JSON.stringify({
    apiKey: 'redacted',
    accessKey: 'dummy',
    refresh_token: 'dummy',
    authToken: 'dummy',
    password: 'dummy',
    service: { port: 8080, connectionString: 'redacted', region: 'BR' },
    retryLimit: 3,
    unsafeValue: concrete,
  });
  const result = extract(content);
  assert.equal(result.parameters.length, 1);
  assert.deepEqual(result.parameters[0].values, { retryLimit: 3, service: { port: 8080, region: 'BR' } });
  const serialized = JSON.stringify(result);
  for (const omitted of ['apiKey', 'accessKey', 'refresh_token', 'authToken', 'password', 'connectionString', concrete])
    assert(!serialized.includes(omitted), omitted);
  assertEvidence(result, content);
  assert.equal(extract('apiKey=redacted\naccessKey=dummy\npassword: example').parameters.length, 0);
});

test('credential-bearing URLs never yield a safe-looking hostname, path or parameter entity', () => {
  const content = [
    'https://fixture-user:fixture-password@example.test/path/config.json',
    'baseUrl: https://api.example.test/v1?api_key=redacted',
    'https://api.example.test/v1?access_token=redacted',
    'https://api.example.test/v1?%61pi_key=redacted',
    'https://api.example.test/v1?sig=redacted',
    'https://api.example.test/v1?key=redacted',
  ].join('\n');
  const result = extract(content);
  assert.deepEqual(result.entities, []);
  assert.deepEqual(result.parameters, []);
});

test('bounds skip incomplete or oversized facts and preserve Unicode in exact evidence excerpts', () => {
  const content =
    '🧠 préface\r\nProject: Cérebro\r\n' + JSON.stringify({ displayName: 'São Paulo 🧠 '.repeat(30), maxTokens: 8000 });
  const result = extract(content, { maxEvidenceBytes: 41 });
  assert(result.entities.some((item) => item.value === 'Cérebro'));
  assert(result.parameters.some((item) => item.values.maxTokens === 8000));
  assertEvidence(result, content);
  assert([...result.entities, ...result.parameters].every((item) => Buffer.byteLength(item.evidence.excerpt) <= 41));
  const limited = extract('Project: One\nProject: Two\nProject: Three\nretryLimit=1\nmaxTokens=8000', {
    maxEntities: 2,
    maxParameters: 1,
  });
  assert.equal(limited.entities.length, 2);
  assert.equal(limited.parameters.length, 1);
  assert.equal(limited.truncated, true);
  const oversizedKey = 'a'.repeat(1025);
  assert.equal(extract(JSON.stringify({ [oversizedKey]: 1 })).parameters.length, 0);
  assert.equal(extract(`${oversizedKey}=1`).parameters.length, 0);
  assert.deepEqual(extract('Project: Brain'.repeat(50), { maxInputBytes: 10 }), {
    entities: [],
    parameters: [],
    truncated: true,
  });
  assert.equal(extract('```json\n{"retryLimit": 3').parameters.length, 0);
});

test('extraction reads only supplied Brain content and adds no verification claim', () => {
  const memory = { id: 'local-only', content: 'retryLimit=3' };
  Object.defineProperty(memory, 'codexHome', {
    get() {
      throw new Error('External homes must not be inspected.');
    },
  });
  const result = extractKnowledge(memory);
  assert.equal(result.parameters[0].values.retryLimit, 3);
  assert(!JSON.stringify(result).includes('verified'));
  assert.equal(result.parameters[0].confidence, undefined);
  assert.throws(() => extractKnowledge(null), /Brain memory/);
});
