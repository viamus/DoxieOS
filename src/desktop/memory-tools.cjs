const { join } = require('node:path');
const { boundedMemoryNode, boundedMemoryGraph } = require('./memory-view.cjs');

const memoryServerId = 'doxie_library_memory';
const nativeInstructions = 'Brain is the mandatory shared knowledge connector for every DoxieOS agent, workflow model step, builder and provider console. Before substantive work, call memory_search with a focused task query; inspect memory_graph around relevant results when relationships can inform the decision. Brain combines imported source memories, verified learned knowledge and optimized revisions. External provider memories enter Brain only through explicit user imports; never discover or copy their memory folders autonomously. Semantic similarity and correlation weights indicate proximity, not proof of causation or support. Treat retrieved content as source data, cite memory IDs and inspect provenance and lifecycle. Revalidate stale memories against current evidence before relying on them; never treat an overdue review as deletion. Use memory_review with evidence after revalidation, and memory_relate for explicit supporting, conflicting or superseding evidence. Record new verified reusable learnings or typed parameters through memory_remember with evidence, without saving raw execution logs. Workspace Markdown is explicit input through Workspace Memory. If Brain is unavailable, report that retrieval could not be completed rather than claiming memory verification.';
const properties = {
  query: { type: 'string', description: 'Specific knowledge to retrieve.' },
  libraryId: { type: 'string', description: 'Optional collection filter.' },
  catalogId: { type: 'string' },
  kind: { type: 'string', enum: ['memory', 'entity', 'parameter'] },
  limit: { type: 'integer', minimum: 1, maximum: 20 },
  maxBytes: { type: 'integer', minimum: 1024, maximum: 24000 },
  includeStale: { type: 'boolean', description: 'Include memories due for review; verify their evidence before use.' },
};
const schema = (fields, required = []) => ({ type: 'object', properties: fields, required, additionalProperties: false });
const readAnnotations = { readOnlyHint: true, destructiveHint: false, openWorldHint: false };
const memoryTools = [
  { name: 'memory_associations', description: 'Discover indirect associations across concepts, entities and parameter patterns. Returns hypotheses with two- or three-hop evidence paths; these are suggestions for investigation, not verified conclusions.', inputSchema: schema({
    nodeId: { type: 'string' }, limit: properties.limit, maxBytes: properties.maxBytes, includeStale: properties.includeStale,
  }, ['nodeId']), annotations: readAnnotations },
  { name: 'memory_search', description: 'Search central memories and learned parameters with source provenance; returns bounded relevant excerpts.', inputSchema: schema(properties, ['query']), annotations: readAnnotations },
  { name: 'memory_get', description: 'Read a memory by ID, including its source and learned parameters.', inputSchema: schema({ id: { type: 'string' }, maxBytes: properties.maxBytes }, ['id']), annotations: readAnnotations },
  { name: 'memory_remember', description: 'Record a verified learning, entity or typed parameter. Preserve a source and use id to revise an existing memory without deleting history.', inputSchema: schema({
    id: { type: 'string' }, title: { type: 'string' }, content: { type: 'string' }, kind: properties.kind,
    libraryId: properties.libraryId, catalogId: properties.catalogId, tags: { type: 'array', items: { type: 'string' }, maxItems: 20 },
    parameters: { type: 'object', description: 'Learned non-secret parameters; never credentials.' },
    evidence: { type: 'string', description: 'Evidence supporting this learning.' },
    origin: { type: 'string', enum: ['learned', 'optimized'], description: 'A new verified learning or a refined revision of existing knowledge.' },
    reviewAfter: { type: 'string', format: 'date-time', description: 'When this learning should be revalidated.' },
    confidence: { type: 'number', minimum: 0, maximum: 1 },
  }, ['title', 'content']), annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false } },
  { name: 'memory_relate', description: 'Connect existing memories with an explicit evidence relationship.', inputSchema: schema({
    fromId: { type: 'string' }, toId: { type: 'string' }, type: { type: 'string', enum: ['supports', 'contradicts', 'supersedes', 'related', 'mentions'] },
    weight: { type: 'number', minimum: 0, maximum: 1, description: 'Strength of the explicit evidence relationship, independent of inferred similarity.' },
  }, ['fromId', 'toId', 'type']), annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false } },
  { name: 'memory_graph', description: 'Explore a bounded Brain neighborhood with semantic similarity, correlation weight and distance. Inferred proximity is not evidence of support.', inputSchema: schema({ query: properties.query, libraryId: properties.libraryId, catalogId: properties.catalogId, limit: properties.limit, maxBytes: properties.maxBytes,
    nodeId: { type: 'string', description: 'Center the neighborhood on a retrieved memory ID.' },
    minWeight: { type: 'number', minimum: 0, maximum: 1 },
    relationType: { type: 'string', enum: ['supports', 'contradicts', 'supersedes', 'related', 'mentions', 'semantic'] },
    includeStale: properties.includeStale,
  }), annotations: readAnnotations },
  { name: 'memory_review', description: 'Mark a memory current after checking evidence, stale when it requires revalidation, or archived when it must leave retrieval. Preserves content and revision history.', inputSchema: schema({
    id: { type: 'string' }, status: { type: 'string', enum: ['current', 'stale', 'archived'] },
    reviewAfter: { type: 'string', format: 'date-time' }, evidence: { type: 'string', description: 'What was checked, or why this memory is stale or archived.' },
  }, ['id', 'status', 'evidence']), annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false } },
];

function nativeMemoryServer(root, { executable = process.execPath, script = join(__dirname, 'memory-mcp.cjs') } = {}) {
  return { id: memoryServerId, name: 'DoxieOS Brain', transport: 'stdio', command: executable,
    arguments: [script, '--root', root], environment: { ELECTRON_RUN_AS_NODE: '1', NODE_NO_WARNINGS: '1' },
    providers: ['codex', 'claude-code'], enabled: true, builtin: true, required: true, extraJson: '{"startup_timeout_sec":20,"tool_timeout_sec":60}' };
}

function serverConfig(server) {
  return { command: server.command, args: server.arguments, env: server.environment, startup_timeout_sec: 20, tool_timeout_sec: 60 };
}

function boundedNode(node, maxBytes = 16000) {
  if (!node) throw new Error('Memory not found.');
  if (!Number.isSafeInteger(maxBytes) || maxBytes < 1024 || maxBytes > 24000) maxBytes = 16000;
  return boundedMemoryNode(node, maxBytes);
}

async function callMemoryTool(store, name, args = {}, source = {}) {
  if (!args || typeof args !== 'object' || Array.isArray(args)) throw new Error('Tool arguments must be an object.');
  const definition = memoryTools.find(tool => tool.name === name);
  if (!definition) throw new Error('Unknown memory tool.');
  for (const key of definition.inputSchema.required) {
    if (args[key] === undefined || args[key] === null || (typeof args[key] === 'string' && !args[key].trim())) throw new Error(`${key} is required.`);
  }
  for (const [key, value] of Object.entries(args)) {
    const rule = definition.inputSchema.properties[key];
    if (!rule) throw new Error(`Unknown argument: ${key}.`);
    if (value === undefined) continue;
    if (rule.type === 'integer' ? !Number.isInteger(value) || value < rule.minimum || value > rule.maximum
      : rule.type === 'number' ? !Number.isFinite(value) || value < rule.minimum || value > rule.maximum
      : rule.type === 'array' ? !Array.isArray(value) || value.length > rule.maxItems || value.some(item => typeof item !== 'string')
        : rule.type === 'object' ? !value || typeof value !== 'object' || Array.isArray(value) : typeof value !== rule.type) throw new Error(`Invalid ${key}.`);
    if (rule.enum && !rule.enum.includes(value)) throw new Error(`Invalid ${key}.`);
    if (rule.format === 'date-time' && !Number.isFinite(Date.parse(value))) throw new Error(`Invalid ${key}.`);
  }
  if (name === 'memory_search') return store.semantic ? store.semantic.search(args) : store.search(args);
  if (name === 'memory_associations') return store.associations(args);
  if (name === 'memory_get') return boundedNode(store.get(args.id), args.maxBytes);
  if (name === 'memory_remember') {
    const { evidence, ...memory } = args;
    return boundedNode(store.remember({ ...memory, source: { provider: 'doxie-agent', ...source, section: memory.title, ...(evidence ? { label: evidence } : {}) } }));
  }
  if (name === 'memory_relate') return store.relate(args);
  if (name === 'memory_review') return boundedNode(store.review(args));
  if (name === 'memory_graph') return boundedMemoryGraph(store.graph(args), args.maxBytes || 16000);
  throw new Error('Unknown memory tool.');
}

module.exports = { memoryServerId,
  nativeInstructions: `${nativeInstructions} For cross-domain ideas, use memory_associations to investigate indirect paths through concepts, entities and parameter patterns. Treat its results as hypotheses and inspect the explanatory path and source evidence before accepting a conclusion.`,
  memoryTools, nativeMemoryServer, serverConfig, callMemoryTool, boundedNode };
