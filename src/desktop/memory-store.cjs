const { randomUUID, createHash } = require('node:crypto');
const { mkdirSync, lstatSync } = require('node:fs');
const { dirname, join, resolve } = require('node:path');
const { boundedMemoryNode, boundedMemoryGraph } = require('./memory-view.cjs');

const kinds = new Set(['memory', 'entity', 'parameter']);
const relationTypes = new Set(['supports', 'contradicts', 'supersedes', 'related', 'mentions']);
const credentialKey =
  /(?:password|passwd|secret|credential|authorization|private.?key|connection.?string|(?:api|access|refresh|auth|bearer)[_-]?(?:key|token))/i;
const credentialValue =
  /-----BEGIN [\w ]*PRIVATE KEY-----|\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.)|https?:\/\/[^\s/@:]+:[^\s/@]+@/;
const now = () => new Date().toISOString();
const hash = (value) => createHash('sha256').update(value).digest('hex');
const byteLength = (value) => Buffer.byteLength(typeof value === 'string' ? value : JSON.stringify(value), 'utf8');
const limitValue = (value, fallback, maximum) =>
  Number.isSafeInteger(Number(value)) && Number(value) > 0 ? Math.min(Number(value), maximum) : fallback;
const scopeValue = (value) =>
  value === undefined || value === null || value === '' ? null : String(value).slice(0, 200);

function shorten(text, maxBytes) {
  if (byteLength(text) <= maxBytes) return text;
  let result = '';
  let bytes = 0;
  for (const character of text) {
    const cost = byteLength(character);
    if (bytes + cost > maxBytes) break;
    result += character;
    bytes += cost;
  }
  return result;
}

function parametersJson(value) {
  if (value === undefined || value === null) return null;
  if (typeof value !== 'object' || Array.isArray(value)) throw new Error('Learned parameters must be a JSON object.');
  let entries = 0;
  function validate(item, depth = 0) {
    if (depth > 8 || ++entries > 1000) throw new Error('Learned parameters exceed the structure limit.');
    if (item === null || typeof item === 'boolean') return item;
    if (typeof item === 'number' && Number.isFinite(item)) return item;
    if (typeof item === 'string' && !credentialValue.test(item)) return item;
    if (Array.isArray(item)) return item.map((child) => validate(child, depth + 1));
    if (item && typeof item === 'object' && [Object.prototype, null].includes(Object.getPrototypeOf(item))) {
      const result = Object.create(null);
      for (const key of Object.keys(item).sort()) {
        if (['__proto__', 'constructor', 'prototype'].includes(key) || credentialKey.test(key))
          throw new Error('Credentials cannot be saved as learned parameters.');
        result[key] = validate(item[key], depth + 1);
      }
      return result;
    }
    throw new Error('Learned parameters must contain JSON values without credentials.');
  }
  const result = JSON.stringify(validate(value));
  if (byteLength(result) > 16 * 1024) throw new Error('Learned parameters are limited to 16 KiB.');
  return result;
}

function normalizedSource(source, title) {
  if (source !== undefined && (source === null || typeof source !== 'object' || Array.isArray(source)))
    throw new Error('Memory provenance must be an object.');
  const result = {};
  for (const key of ['provider', 'path', 'runId', 'section', 'kind', 'label', 'url']) {
    if (source?.[key] !== undefined) {
      if (typeof source[key] !== 'string' || source[key].length > 4096 || credentialValue.test(source[key]))
        throw new Error('Invalid memory provenance.');
      result[key] = source[key];
    }
  }
  if (source?.chunk !== undefined) {
    if (!Number.isSafeInteger(source.chunk) || source.chunk < 0) throw new Error('Invalid memory source chunk.');
    result.chunk = source.chunk;
  }
  result.provider ||= 'doxie';
  if (result.provider === 'claude') result.provider = 'claude-code';
  const location = result.path || result.runId || result.label || `manual:${title}`;
  const pathKey = /^[a-z]:[\\/]/i.test(location) ? location.replace(/\\/g, '/').toLowerCase() : location;
  return {
    source: result,
    identity: JSON.stringify([result.provider, pathKey, result.section || '', result.chunk ?? null]),
  };
}

const { Database, Connection } = require('./brain-runtime.cjs');
const { existsSync } = require('node:fs');

const normalizeText = (value) =>
  String(value || '')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase();
const tokenize = (value) => normalizeText(value).match(/[\p{L}\p{N}_-]+/gu) || [];
const activeStatus = (row) =>
  row.archivedAt
    ? 'archived'
    : row.supersededBy
      ? 'superseded'
      : row.status === 'stale' || Date.parse(row.reviewAfter) <= Date.now()
        ? 'stale'
        : 'current';
const validateDate = (value) => {
  if (typeof value !== 'string' || !Number.isFinite(Date.parse(value)))
    throw new Error('Memory reviewAfter must be an ISO date.');
  return new Date(value).toISOString();
};
const unit = (value, fallback = 1) => {
  if (value === undefined) return fallback;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1)
    throw new Error('Memory weights and confidence must be between 0 and 1.');
  return value;
};

function createMemoryStore({ root, databasePath } = {}) {
  if (typeof root !== 'string' || !root.trim()) throw new Error('A memory-store root is required.');
  const file = databasePath || join(resolve(root), '.memory', 'brain.lbdb');
  const legacyFile = join(resolve(root), '.memory', 'knowledge.db');
  if (file !== ':memory:') {
    const directory = dirname(resolve(file));
    if (
      lstatSync(directory, { throwIfNoEntry: false })?.isSymbolicLink() ||
      lstatSync(file, { throwIfNoEntry: false })?.isSymbolicLink()
    )
      throw new Error('The memory database cannot be a symbolic link.');
    mkdirSync(directory, { recursive: true });
  }
  // A single main-process owner serves import jobs and agent MCP clients.
  // All application writers use that service, including the WASM filesystem.
  const database = new Database(file, 128 * 1024 * 1024);
  const connection = new Connection(database, 2);
  const statements = new Map();
  const rows = new Map();
  const postings = new Map();
  let transaction = false;
  let closed = false;
  function query(cypher, params) {
    if (closed) throw new Error('The Brain database is closed.');
    let result;
    if (params) {
      let statement = statements.get(cypher);
      if (!statement) {
        statement = connection.prepareSync(cypher);
        if (!statement.isSuccess()) throw new Error(statement.getErrorMessage());
        statements.set(cypher, statement);
      }
      result = connection.executeSync(statement, params);
    } else result = connection.querySync(cypher);
    try {
      return result.getAllSync();
    } finally {
      result.close();
    }
  }
  try {
    query(`CREATE NODE TABLE IF NOT EXISTS BrainMemory(id STRING PRIMARY KEY, kind STRING, title STRING, content STRING,
      libraryId STRING, catalogId STRING, tagsJson STRING, parametersJson STRING, fingerprint STRING,
      createdAt STRING, updatedAt STRING, supersededBy STRING, archivedAt STRING, origin STRING,
      status STRING, reviewAfter STRING, reviewedAt STRING, confidence DOUBLE, reviewEvidence STRING)`);
    query(`CREATE NODE TABLE IF NOT EXISTS BrainOrigin(id STRING PRIMARY KEY, memoryId STRING, originKey STRING,
      provider STRING, path STRING, sourceJson STRING, importedAt STRING)`);
    query(`CREATE NODE TABLE IF NOT EXISTS BrainEmbedding(id STRING PRIMARY KEY, memoryId STRING, model STRING,
      vector DOUBLE[], dimensions INT64, createdAt STRING)`);
    query(`CREATE REL TABLE IF NOT EXISTS BrainLink(FROM BrainMemory TO BrainMemory,
      id STRING, type STRING, createdAt STRING, weight DOUBLE, semanticScore DOUBLE,
      correlationScore DOUBLE, distance DOUBLE, inferred BOOLEAN, model STRING)`);
    query('CREATE NODE TABLE IF NOT EXISTS BrainMeta(id STRING PRIMARY KEY, value STRING)');
  } catch (error) {
    connection.closeSync();
    database.closeSync();
    throw error;
  }
  const properties = [
    'id',
    'kind',
    'title',
    'content',
    'libraryId',
    'catalogId',
    'tagsJson',
    'parametersJson',
    'fingerprint',
    'createdAt',
    'updatedAt',
    'supersededBy',
    'archivedAt',
    'origin',
    'status',
    'reviewAfter',
    'reviewedAt',
    'confidence',
    'reviewEvidence',
  ];
  function addToIndex(row) {
    rows.set(row.id, row);
    const words = new Map();
    for (const [text, multiplier] of [
      [row.title, 5],
      [row.content, 1],
      [JSON.parse(row.tagsJson || '[]').join(' '), 2],
    ]) {
      for (const token of tokenize(text)) words.set(token, (words.get(token) || 0) + multiplier);
    }
    for (const [token, count] of words) {
      if (!postings.has(token)) postings.set(token, new Map());
      postings.get(token).set(row.id, count);
    }
  }
  function reload() {
    rows.clear();
    postings.clear();
    for (const { n } of query('MATCH (n:BrainMemory) RETURN n'))
      addToIndex(Object.fromEntries(properties.map((key) => [key, n[key] ?? null])));
  }
  function write(action) {
    if (transaction) return action();
    query('BEGIN TRANSACTION');
    transaction = true;
    try {
      const value = action();
      query('COMMIT');
      return value;
    } catch (error) {
      query('ROLLBACK');
      reload();
      throw error;
    } finally {
      transaction = false;
    }
  }
  function insertMemory(row) {
    query(`CREATE (n:BrainMemory {${properties.map((key) => `${key}:$${key}`).join(',')}})`, row);
    addToIndex(row);
  }
  function update(id, changes) {
    query(
      `MATCH (n:BrainMemory {id:$id}) SET ${Object.keys(changes)
        .map((key) => `n.${key}=$${key}`)
        .join(',')}`,
      { id, ...changes },
    );
    Object.assign(rows.get(id), changes);
  }
  function sourceFor(row) {
    return query(
      'MATCH (s:BrainOrigin) WHERE s.memoryId=$id RETURN s.sourceJson AS sourceJson,s.importedAt AS importedAt ORDER BY s.importedAt DESC,s.id DESC',
      { id: row.id },
    ).map((item) => ({ ...JSON.parse(item.sourceJson), importedAt: item.importedAt }));
  }
  function node(row) {
    if (!row) return null;
    const provenance = sourceFor(row);
    const status = activeStatus(row);
    return {
      id: row.id,
      kind: row.kind,
      title: row.title,
      content: row.content,
      libraryId: row.libraryId,
      catalogId: row.catalogId,
      tags: JSON.parse(row.tagsJson || '[]'),
      parameters: row.parametersJson ? JSON.parse(row.parametersJson) : null,
      source: provenance[0] || {},
      provenance,
      createdAt: row.createdAt,
      updatedAt: row.updatedAt,
      supersededBy: row.supersededBy,
      archivedAt: row.archivedAt,
      origin: row.origin,
      status,
      reviewAfter: row.reviewAfter,
      reviewedAt: row.reviewedAt,
      confidence: row.confidence,
      lifecycle: {
        status,
        reviewAfter: row.reviewAfter,
        reviewedAt: row.reviewedAt,
        confidence: row.confidence,
        ...(row.reviewEvidence ? { evidence: row.reviewEvidence } : {}),
      },
      contentTruncated: false,
      totalContentBytes: byteLength(row.content),
    };
  }
  const get = (id) => node(rows.get(String(id)));
  function matches(row, options = {}) {
    const status = activeStatus(row);
    if (options.status && status !== options.status) return false;
    if (!options.includeSuperseded && status === 'superseded' && options.status !== 'superseded') return false;
    if (!options.includeArchived && status === 'archived' && options.status !== 'archived') return false;
    if (!options.includeStale && status === 'stale' && options.status !== 'stale') return false;
    for (const key of ['libraryId', 'catalogId', 'kind']) {
      if (options[key] === undefined || options[key] === '') continue;
      if (row[key] !== options[key]) return false;
    }
    return true;
  }
  function sorted(options = {}) {
    return [...rows.values()]
      .filter((row) => matches(row, options))
      .reverse()
      .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  }
  function list(options = {}) {
    return sorted(options)
      .slice(0, limitValue(options.limit, 100, 500))
      .map((row) =>
        boundedMemoryNode(
          { ...node(row), content: shorten(row.content, 1200), contentTruncated: byteLength(row.content) > 1200 },
          4000,
        ),
      );
  }
  function edge(row, fromId, toId) {
    const result = {
      id: row.id,
      fromId,
      toId,
      from: fromId,
      to: toId,
      type: row.type,
      createdAt: row.createdAt,
      weight: row.weight ?? 1,
      inferred: Boolean(row.inferred),
      distance: row.distance ?? 0,
    };
    for (const key of ['semanticScore', 'correlationScore', 'model'])
      if (row[key] !== null && row[key] !== undefined) result[key] = row[key];
    return result;
  }
  function insertEdge(fromId, toId, type, values = {}) {
    if (fromId === toId) throw new Error('A memory cannot relate to itself.');
    if (!rows.has(fromId) || !rows.has(toId)) throw new Error('Both related memories must exist.');
    for (const key of ['weight', 'semanticScore', 'correlationScore', 'distance'])
      if (values[key] !== undefined) unit(values[key]);
    if (values.model !== undefined) embeddingModel(values.model);
    if (type === 'supersedes') {
      const seen = new Set();
      const pending = [toId];
      while (pending.length) {
        const id = pending.pop();
        if (id === fromId) throw new Error('Memory revisions cannot form a cycle.');
        if (seen.has(id)) continue;
        seen.add(id);
        for (const { id: ancestor } of query(
          "MATCH (a:BrainMemory {id:$id})-[r:BrainLink]->(b:BrainMemory) WHERE r.type='supersedes' RETURN b.id AS id",
          { id },
        ))
          pending.push(ancestor);
      }
    }
    const existing = query(
      `MATCH (a:BrainMemory {id:$fromId})-[r:BrainLink]->(b:BrainMemory {id:$toId})
        WHERE r.type=$type AND r.inferred=$inferred
        AND (r.model=$model OR (r.model IS NULL AND $model IS NULL)) RETURN r`,
      { fromId, toId, type, inferred: Boolean(values.inferred), model: values.model || null },
    );
    if (existing.length) {
      const previous = existing[0].r;
      const changes = Object.fromEntries(
        ['weight', 'semanticScore', 'correlationScore', 'distance']
          .filter((key) => values[key] !== undefined)
          .map((key) => [key, values[key]]),
      );
      if (changes.weight !== undefined && changes.distance === undefined) changes.distance = 1 - changes.weight;
      if (Object.keys(changes).length) {
        query(
          `MATCH ()-[r:BrainLink]->() WHERE r.id=$id
          SET ${Object.keys(changes)
            .map((key) => `r.${key}=$${key}`)
            .join(',')}`,
          { id: previous.id, ...changes },
        );
      }
      return edge({ ...previous, ...changes }, fromId, toId);
    }
    const weight = unit(values.weight);
    const fields = {
      id: randomUUID(),
      type,
      createdAt: now(),
      weight,
      semanticScore: values.semanticScore === undefined ? null : unit(values.semanticScore),
      correlationScore: values.correlationScore === undefined ? null : unit(values.correlationScore),
      distance: values.distance === undefined ? 1 - weight : unit(values.distance),
      inferred: Boolean(values.inferred),
      model: values.model || null,
    };
    query(
      `MATCH (a:BrainMemory {id:$fromId}),(b:BrainMemory {id:$toId}) CREATE (a)-[:BrainLink {${Object.keys(fields)
        .map((key) => `${key}:$${key}`)
        .join(',')}}]->(b)`,
      { fromId, toId, ...fields },
    );
    if (type === 'supersedes') update(toId, { supersededBy: fromId });
    return edge(fields, fromId, toId);
  }
  function relate({ fromId, toId, type = 'related', ...values } = {}) {
    if (type === 'supported') type = 'supports';
    if (!relationTypes.has(type)) throw new Error('Unknown memory relation type.');
    return write(() => insertEdge(String(fromId), String(toId), type, values));
  }
  function sourceTip(originKey) {
    const candidates = query(
      'MATCH (s:BrainOrigin) WHERE s.originKey=$originKey RETURN s.memoryId AS id ORDER BY s.importedAt DESC,s.id DESC',
      { originKey },
    );
    if (candidates.length < 2) return rows.get(candidates[0]?.id);
    const ids = candidates.map((item) => item.id);
    const superseded = new Set(
      query(
        `MATCH (successor:BrainMemory)-[r:BrainLink]->(predecessor:BrainMemory)
        WHERE r.type='supersedes' AND successor.id IN $ids AND predecessor.id IN $ids
        RETURN predecessor.id AS id`,
        { ids },
      ).map((item) => item.id),
    );
    // Deduplicated memories can branch when separate sources later disagree.
    // Only revisions carrying this source's provenance can advance its tip.
    // Graph ordering also resolves batched imports whose timestamps are equal.
    return candidates.map((item) => rows.get(item.id)).find((row) => !superseded.has(row.id));
  }
  function remember(input = {}) {
    const kind = input.kind || 'memory';
    if (!kinds.has(kind)) throw new Error('Memory kind must be memory, entity, or parameter.');
    if (typeof input.title !== 'string' || !input.title.trim() || input.title.length > 240)
      throw new Error('A memory title of at most 240 characters is required.');
    const title = input.title.trim();
    const parameters = parametersJson(input.parameters);
    const content = input.content ?? (parameters || '');
    if (typeof content !== 'string' || !content.trim() || byteLength(content) > 256 * 1024)
      throw new Error('Memory content must be text of at most 256 KiB.');
    if (kind === 'parameter' && parameters === null) throw new Error('A parameter memory needs typed parameters.');
    if (
      input.tags !== undefined &&
      (!Array.isArray(input.tags) ||
        input.tags.length > 40 ||
        input.tags.some((tag) => typeof tag !== 'string' || tag.length > 100))
    )
      throw new Error('Memory tags must be a short text list.');
    const tags = [...new Set((input.tags || []).map((tag) => tag.trim()).filter(Boolean))];
    if ([title, content, ...tags].some((value) => credentialValue.test(value)))
      throw new Error('Credentials cannot be saved in memory text or tags.');
    const libraryId = scopeValue(input.libraryId),
      catalogId = scopeValue(input.catalogId);
    const { source, identity } = normalizedSource(input.source, title);
    const origin =
      input.origin ||
      (['imported', 'optimized', 'learned'].includes(source.kind)
        ? source.kind
        : ['codex', 'claude-code', 'catalog'].includes(source.provider)
          ? 'imported'
          : 'learned');
    if (!['imported', 'learned', 'optimized'].includes(origin)) throw new Error('Unknown memory origin.');
    const reviewAfter =
      input.reviewAfter === undefined
        ? new Date(Date.now() + (origin === 'imported' ? 90 : 180) * 86400000).toISOString()
        : validateDate(input.reviewAfter);
    const confidence = unit(input.confidence, 0.8);
    const originKey = hash(JSON.stringify([catalogId, libraryId, identity]));
    const fingerprint = hash(JSON.stringify([kind, title, content, parameters, [...tags].sort()]));
    return write(() => {
      const previous = input.id ? rows.get(String(input.id)) : sourceTip(originKey);
      if (input.id && !previous) throw new Error('The memory to revise was not found.');
      if (previous?.fingerprint === fingerprint) return node(previous);
      const existing = [...rows.values()]
        .reverse()
        .find(
          (row) =>
            row.fingerprint === fingerprint &&
            row.libraryId === libraryId &&
            row.catalogId === catalogId &&
            !row.supersededBy &&
            !row.archivedAt,
        );
      const id = existing?.id || randomUUID();
      const timestamp = now();
      if (!existing)
        insertMemory({
          id,
          kind,
          title,
          content,
          libraryId,
          catalogId,
          tagsJson: JSON.stringify(tags),
          parametersJson: parameters,
          fingerprint,
          createdAt: timestamp,
          updatedAt: timestamp,
          supersededBy: null,
          archivedAt: null,
          origin,
          status: 'current',
          reviewAfter,
          reviewedAt: null,
          confidence,
          reviewEvidence: null,
        });
      const sourceId = hash(`${id}\0${originKey}`);
      query(
        'MERGE (s:BrainOrigin {id:$id}) ON CREATE SET s.memoryId=$memoryId,s.originKey=$originKey,s.provider=$provider,s.path=$path,s.sourceJson=$sourceJson,s.importedAt=$importedAt',
        {
          id: sourceId,
          memoryId: id,
          originKey,
          provider: source.provider,
          path: source.path || null,
          sourceJson: JSON.stringify({ ...source, kind: origin }),
          importedAt: timestamp,
        },
      );
      if (previous && previous.id !== id) insertEdge(id, previous.id, 'supersedes');
      return get(id);
    });
  }
  function search({ query: input = '', limit = 8, maxBytes = 16000, ...options } = {}) {
    if (typeof input !== 'string' || input.length > 512)
      throw new Error('A search query of at most 512 characters is required.');
    const tokens = [...new Set(tokenize(input).slice(0, 30))];
    const count = limitValue(limit, 8, 100);
    const budget = limitValue(maxBytes, 16000, 256 * 1024);
    const responseQuery = shorten(input, Math.max(0, budget - 512));
    const queryMetadata = responseQuery === input ? {} : { queryTruncated: true };
    const resultBudget =
      budget - byteLength({ query: responseQuery, results: [], truncated: false, bytes: budget, ...queryMetadata }) + 2;
    const scores = new Map();
    for (const [term, documents] of postings)
      if (tokens.some((token) => term.startsWith(token))) {
        const idf = Math.log(1 + rows.size / (1 + documents.size));
        for (const [id, frequency] of documents)
          if (matches(rows.get(id), options)) scores.set(id, (scores.get(id) || 0) + idf * (1 + Math.log(frequency)));
      }
    const candidates = [...scores].sort((a, b) => b[1] - a[1]);
    const results = [];
    let truncated = candidates.length > count || responseQuery !== input;
    for (const [id, score] of candidates.slice(0, count)) {
      const row = rows.get(id);
      const position = Math.max(0, normalizeText(row.content).indexOf(tokens[0]));
      const window = row.content.slice(Math.max(0, position - 160));
      const remaining = resultBudget - byteLength(results) - 2;
      if (remaining < 256) {
        truncated = true;
        break;
      }
      const preview = shorten(window, 2000);
      const allowance = Math.min(remaining, Math.max(256, Math.floor(resultBudget / count) - 4));
      const hit = boundedMemoryNode(
        { ...node(row), content: preview, excerpt: preview, contentTruncated: preview !== row.content, score },
        allowance,
      );
      if (byteLength([...results, hit]) > resultBudget) {
        truncated = true;
        break;
      }
      results.push(hit);
      truncated ||= hit.contentTruncated || hit.parametersTruncated || hit.provenanceTruncated || hit.tagsTruncated;
    }
    return { query: responseQuery, results, truncated, bytes: byteLength(results), ...queryMetadata };
  }
  function graph({
    query: input = '',
    limit = 80,
    includeHistory = false,
    nodeId,
    minWeight = 0,
    relationType,
    ...options
  } = {}) {
    const maximum = limitValue(limit, 80, 300);
    unit(minWeight, 0);
    if (relationType && relationType !== 'semantic' && !relationTypes.has(relationType))
      throw new Error('Unknown memory relation type.');
    const scope = {
      ...options,
      includeSuperseded: includeHistory || options.includeSuperseded !== false,
      includeArchived: includeHistory || options.includeArchived,
      includeStale: includeHistory || options.includeStale,
    };
    const seed = nodeId
      ? [rows.get(nodeId)].filter((row) => row && matches(row, scope)).map(node)
      : input
        ? search({ query: input, ...scope, limit: maximum, maxBytes: 256 * 1024 }).results
        : sorted(scope)
            .slice(0, maximum + 1)
            .map(node);
    const ids = new Set(seed.slice(0, maximum).map((item) => item.id));
    let truncated = seed.length > maximum;
    if ((input || nodeId) && ids.size) {
      const neighbors = query(
        `MATCH (a:BrainMemory)-[r:BrainLink]->(b:BrainMemory)
          WHERE (a.id IN $ids OR b.id IN $ids) AND r.weight >= $minWeight
          AND ($relationType='' OR ($relationType='semantic' AND r.inferred=true) OR r.type=$relationType)
          RETURN a.id AS fromId,b.id AS toId,r ORDER BY r.weight DESC LIMIT $limit`,
        { ids: [...ids], minWeight, relationType: relationType || '', limit: maximum * 8 },
      );
      for (const item of neighbors)
        for (const id of [item.fromId, item.toId])
          if (!ids.has(id) && matches(rows.get(id), scope)) {
            if (ids.size >= maximum) truncated = true;
            else ids.add(id);
          }
    }
    if (!ids.size)
      return boundedMemoryGraph({ nodes: [], edges: [], truncated }, limitValue(options.maxBytes, 16000, 256 * 1024));
    const links = query(
      `MATCH (a:BrainMemory)-[r:BrainLink]->(b:BrainMemory)
        WHERE a.id IN $ids AND b.id IN $ids AND r.weight >= $minWeight
        AND ($relationType='' OR ($relationType='semantic' AND r.inferred=true) OR r.type=$relationType)
        RETURN a.id AS fromId,b.id AS toId,r ORDER BY r.weight DESC LIMIT $limit`,
      { ids: [...ids], minWeight, relationType: relationType || '', limit: maximum * 8 + 1 },
    );
    truncated ||= links.length > maximum * 8;
    return boundedMemoryGraph(
      {
        nodes: [...ids].map((id) => ({ ...get(id), summary: shorten(rows.get(id).content, 300) })),
        edges: links.slice(0, maximum * 8).map((item) => edge(item.r, item.fromId, item.toId)),
        truncated,
      },
      limitValue(options.maxBytes, 16000, 256 * 1024),
    );
  }
  function stats() {
    const all = [...rows.values()],
      active = all.filter((row) => !row.supersededBy && !row.archivedAt);
    const counts = (kind) => active.filter((row) => row.kind === kind).length;
    return {
      memories: counts('memory'),
      entities: counts('entity'),
      parameters: counts('parameter'),
      total: all.length,
      relations: Number(query('MATCH ()-[r:BrainLink]->() RETURN COUNT(r) AS count')[0].count),
      sources: Number(query('MATCH (s:BrainOrigin) RETURN COUNT(DISTINCT s.originKey) AS count')[0].count),
      libraries: new Set(all.map((row) => row.libraryId).filter(Boolean)).size,
      current: active.filter((row) => activeStatus(row) === 'current').length,
      stale: active.filter((row) => activeStatus(row) === 'stale').length,
      archived: all.filter((row) => row.archivedAt).length,
      superseded: all.filter((row) => row.supersededBy).length,
      imported: active.filter((row) => row.origin === 'imported').length,
      learned: active.filter((row) => row.origin === 'learned').length,
      optimized: active.filter((row) => row.origin === 'optimized').length,
      storage: 'LadybugDB',
      database: file,
      vectorSearch: 'exact-cosine',
    };
  }
  function sources() {
    return query(
      'MATCH (s:BrainOrigin) RETURN s.provider AS provider,s.path AS path,COUNT(DISTINCT s.memoryId) AS memories,MAX(s.importedAt) AS lastImportedAt ORDER BY lastImportedAt DESC LIMIT 1000',
    ).map((row) => ({ ...row, memories: Number(row.memories) }));
  }
  function review({ id, status = 'current', reviewAfter, evidence } = {}) {
    const row = rows.get(String(id));
    if (!row) throw new Error('The memory to review was not found.');
    if (!['current', 'stale', 'archived'].includes(status))
      throw new Error('Memory review status must be current, stale, or archived.');
    if (row.supersededBy && status === 'current')
      throw new Error('A superseded memory cannot become current; revise its active successor.');
    if (
      evidence !== undefined &&
      (typeof evidence !== 'string' || evidence.length > 4096 || credentialValue.test(evidence))
    )
      throw new Error('Invalid memory review evidence.');
    const date =
      reviewAfter === undefined
        ? status === 'current'
          ? new Date(Date.now() + (row.origin === 'imported' ? 90 : 180) * 86400000).toISOString()
          : row.reviewAfter
        : validateDate(reviewAfter);
    return write(() => {
      update(String(id), {
        status,
        reviewAfter: date,
        reviewedAt: now(),
        updatedAt: now(),
        archivedAt: status === 'archived' ? row.archivedAt || now() : null,
        reviewEvidence: evidence || null,
      });
      return get(id);
    });
  }
  function archive(id) {
    const row = rows.get(String(id));
    if (!row) throw new Error('The memory to archive was not found.');
    if (row.archivedAt) return get(id);
    return review({ id, status: 'archived' });
  }
  function reconcileImport({ provider, path, activeIds = [] } = {}) {
    const keep = new Set(activeIds);
    return write(() => {
      const matches = query(
        'MATCH (s:BrainOrigin) WHERE s.provider=$provider AND s.path=$path RETURN s.memoryId AS id',
        { provider, path },
      );
      let archived = 0;
      for (const { id } of matches) {
        const row = rows.get(id);
        if (row && !row.supersededBy && !row.archivedAt && !keep.has(id)) {
          // Deduplicated knowledge can have several independent sources. A
          // shortened file may withdraw only its own sole-source chunks.
          const otherOrigins = sourceFor(row).some((source) => source.provider !== provider || source.path !== path);
          if (otherOrigins) continue;
          archive(id);
          archived++;
        }
      }
      return { archived };
    });
  }
  function importText(input = {}) {
    if (typeof input.content !== 'string') throw new Error('Imported memory content must be text.');
    const chunks = [];
    let content = '',
      size = 0;
    for (const character of input.content) {
      const bytes = byteLength(character);
      if (size + bytes > 16000 && content) {
        chunks.push(content);
        content = '';
        size = 0;
      }
      content += character;
      size += bytes;
    }
    if (content.trim()) chunks.push(content);
    const title = String(input.title || '')
      .trim()
      .slice(0, 220);
    return write(() =>
      chunks.map((chunk, index) =>
        remember({
          ...input,
          id: undefined,
          title: `${title}${chunks.length > 1 ? ` (${index + 1}/${chunks.length})` : ''}`,
          content: chunk,
          source: { ...input.source, kind: 'imported', chunk: index },
        }),
      ),
    );
  }
  function embeddingModel(model) {
    if (typeof model !== 'string' || !model.trim() || model.length > 240)
      throw new Error('An embedding model identifier is required.');
    return model.trim();
  }
  function setEmbedding(id, vector, { model } = {}) {
    model = embeddingModel(model);
    if (!Array.isArray(vector) && !(ArrayBuffer.isView(vector) && !(vector instanceof DataView)))
      throw new Error('An embedding must be a numeric vector.');
    const values = Array.from(vector);
    if (
      !values.length ||
      values.length > 4096 ||
      values.some((value) => typeof value !== 'number' || !Number.isFinite(value)) ||
      !values.some((value) => value !== 0)
    )
      throw new Error('An embedding must contain 1–4096 finite numeric values and a nonzero norm.');
    if (!rows.has(id)) throw new Error('The memory to embed was not found.');
    query(
      'MERGE (e:BrainEmbedding {id:$id}) SET e.memoryId=$memoryId,e.model=$model,e.vector=$vector,e.dimensions=$dimensions,e.createdAt=$createdAt',
      { id: hash(`${id}\0${model}`), memoryId: id, model, vector: values, dimensions: values.length, createdAt: now() },
    );
    return { id, model, dimensions: values.length };
  }
  function getEmbeddings({ model, limit = 1000, ...options } = {}) {
    model = embeddingModel(model);
    const valid = new Set([...rows.values()].filter((row) => matches(row, options)).map((row) => row.id));
    return query(
      'MATCH (e:BrainEmbedding) WHERE e.model=$model RETURN e.memoryId AS id,e.model AS model,e.vector AS vector',
      { model },
    )
      .filter((item) => valid.has(item.id))
      .slice(0, limitValue(limit, 1000, 10000));
  }
  function unembedded({ model, limit = 50, ...options } = {}) {
    model = embeddingModel(model);
    const indexed = new Set(
      query('MATCH(e:BrainEmbedding) WHERE e.model=$model RETURN e.memoryId AS id', { model }).map((item) => item.id),
    );
    return [...rows.values()]
      .filter((row) => matches(row, options) && !indexed.has(row.id))
      .slice(0, limitValue(limit, 50, 500))
      .map(node);
  }
  function embeddingStats({ model, ...options } = {}) {
    model = embeddingModel(model);
    const eligible = [...rows.values()].filter((row) => matches(row, options));
    const ids = new Set(eligible.map((row) => row.id));
    const indexed = query('MATCH(e:BrainEmbedding) WHERE e.model=$model RETURN e.memoryId AS id', { model }).filter(
      (item) => ids.has(item.id),
    ).length;
    return { indexed, pending: eligible.length - indexed, total: eligible.length };
  }
  function nearest(vector, { model, limit = 40, minSimilarity = 0.15, ...options } = {}) {
    model = embeddingModel(model);
    const values = Array.from(vector || []);
    if (
      !values.length ||
      values.length > 4096 ||
      values.some((value) => !Number.isFinite(value)) ||
      !values.some((value) => value !== 0)
    )
      throw new Error('Invalid query embedding.');
    const ids = [...rows.values()].filter((row) => matches(row, options)).map((row) => row.id);
    if (!ids.length) return [];
    return query(
      `MATCH(e:BrainEmbedding) WHERE e.model=$model AND e.dimensions=$dimensions AND e.memoryId IN $ids
      WITH e,array_cosine_similarity(CAST(e.vector AS DOUBLE[${values.length}]),CAST($vector AS DOUBLE[${values.length}])) AS similarity
      WHERE similarity >= $minSimilarity RETURN e.memoryId AS id,similarity ORDER BY similarity DESC LIMIT $limit`,
      { model, dimensions: values.length, ids, vector: values, minSimilarity, limit: limitValue(limit, 40, 10000) },
    ).map((item) => ({ id: item.id, similarity: Math.max(0, Math.min(1, item.similarity)) }));
  }
  function clearSemanticRelations(model, ids) {
    if (ids)
      query(
        'MATCH(a:BrainMemory)-[r:BrainLink]->(b:BrainMemory) WHERE r.inferred=true AND r.model=$model AND (a.id IN $ids OR b.id IN $ids) DELETE r',
        { model, ids },
      );
    else query('MATCH()-[r:BrainLink]->() WHERE r.inferred=true AND r.model=$model DELETE r', { model });
  }
  function rebuildSemanticRelations({ model, ids, threshold = 0.62, neighbors = 6, append = false } = {}) {
    model = embeddingModel(model);
    unit(threshold);
    neighbors = limitValue(neighbors, 6, 20);
    const eligible = new Set([...rows.values()].filter((row) => matches(row)).map((row) => row.id));
    const vectors = query('MATCH(e:BrainEmbedding) WHERE e.model=$model RETURN e.memoryId AS id,e.vector AS vector', {
      model,
    }).filter((item) => eligible.has(item.id) && (!ids || ids.includes(item.id)));
    return write(() => {
      if (!append) clearSemanticRelations(model, ids);
      let relations = 0;
      for (const item of vectors) {
        const from = rows.get(item.id);
        const left = new Set(tokenize(`${from.title} ${from.tagsJson}`));
        for (const match of nearest(item.vector, { model, limit: neighbors + 1, minSimilarity: threshold })) {
          if (match.id === item.id) continue;
          const to = rows.get(match.id),
            right = new Set(tokenize(`${to.title} ${to.tagsJson}`));
          const common = [...left].filter((word) => right.has(word)).length;
          const overlap = common / Math.max(1, new Set([...left, ...right]).size);
          const sharedSource =
            query(
              'MATCH(a:BrainOrigin),(b:BrainOrigin) WHERE a.memoryId=$fromId AND b.memoryId=$toId AND a.provider=b.provider AND a.path IS NOT NULL AND a.path=b.path RETURN COUNT(*) AS count',
              { fromId: item.id, toId: match.id },
            )[0].count > 0
              ? 1
              : 0;
          const correlationScore = 0.7 * overlap + 0.3 * sharedSource;
          const weight = 0.8 * match.similarity + 0.2 * correlationScore;
          const [fromId, toId] = [item.id, match.id].sort();
          insertEdge(fromId, toId, 'related', {
            weight,
            semanticScore: match.similarity,
            correlationScore,
            distance: 1 - weight,
            inferred: true,
            model,
          });
          relations++;
        }
      }
      return { relations, processed: vectors.length, model, threshold, neighbors };
    });
  }
  async function rebuildSemanticRelationsAsync({
    model,
    ids,
    threshold = 0.62,
    neighbors = 6,
    shouldContinue = () => true,
    onProgress = () => {},
  } = {}) {
    model = embeddingModel(model);
    unit(threshold);
    const selected = new Set(ids);
    const allIds = query('MATCH(e:BrainEmbedding) WHERE e.model=$model RETURN e.memoryId AS id', { model })
      .map((item) => item.id)
      .filter((id) => matches(rows.get(id)) && (!ids || selected.has(id)));
    const result = { relations: 0, processed: 0, model, threshold, neighbors };
    if (closed || !shouldContinue()) return { ...result, aborted: true };
    write(() => clearSemanticRelations(model, ids));
    for (let offset = 0; offset < allIds.length; offset += 10) {
      if (closed || !shouldContinue()) return { ...result, aborted: true };
      const batch = rebuildSemanticRelations({
        model,
        ids: allIds.slice(offset, offset + 10),
        threshold,
        neighbors,
        append: true,
      });
      result.relations += batch.relations;
      result.processed += batch.processed;
      onProgress({ completed: result.processed, total: allIds.length });
      // Native exact nearest-neighbor scans stay bounded to ten source nodes
      // per transaction so rendering and shutdown can run between batches.
      await new Promise((resolve) => setImmediate(resolve));
    }
    return result;
  }
  function syncCatalog(storage) {
    let imported = 0,
      duplicates = 0,
      skipped = 0;
    return write(() => {
      for (const library of storage.list('libraries'))
        for (const memory of library.memories || []) {
          if (!(memory.content || memory.body)?.trim()) {
            skipped++;
            continue;
          }
          const before = rows.size;
          const copies = importText({
            title: memory.name || memory.fileName || library.name,
            content: memory.content || memory.body,
            libraryId: library.id,
            catalogId: library.catalogId,
            kind: 'memory',
            tags: [memory.type, memory.priority].filter(Boolean),
            source: {
              provider: 'catalog',
              path: join(library.path, memory.relativePath || memory.fileName || memory.id),
              label: library.name,
            },
          });
          const added = rows.size - before;
          imported += added;
          duplicates += copies.length - added;
        }
      return { imported, duplicates, skipped };
    });
  }
  function migrateLegacy() {
    if (
      file === ':memory:' ||
      file === legacyFile ||
      !existsSync(legacyFile) ||
      query("MATCH(m:BrainMeta {id:'sqlite-migration-v1'}) RETURN m").length
    )
      return;
    if (lstatSync(legacyFile).isSymbolicLink())
      throw new Error('The legacy memory database cannot be a symbolic link.');
    // SQLite is an input adapter only. This source remains untouched so the
    // migration can be retried or independently backed up.
    const { DatabaseSync } = require('node:sqlite');
    const legacy = new DatabaseSync(legacyFile, { readOnly: true });
    try {
      const memories = legacy.prepare('SELECT * FROM memories').all();
      const origins = legacy.prepare('SELECT * FROM memory_origins ORDER BY id').all();
      const links = legacy.prepare('SELECT * FROM memory_relations').all();
      const vectors = legacy.prepare('SELECT * FROM memory_embeddings').all();
      write(() => {
        for (const old of memories)
          if (!rows.has(old.id)) {
            const origin = origins.some(
              (source) => source.memory_id === old.id && ['codex', 'claude-code', 'catalog'].includes(source.provider),
            )
              ? 'imported'
              : 'learned';
            insertMemory({
              id: old.id,
              kind: old.kind,
              title: old.title,
              content: old.content,
              libraryId: old.library_id,
              catalogId: old.catalog_id,
              tagsJson: old.tags_json,
              parametersJson: old.parameters_json,
              fingerprint: old.fingerprint,
              createdAt: old.created_at,
              updatedAt: old.updated_at,
              supersededBy: old.superseded_by,
              archivedAt: old.archived_at || null,
              origin,
              status: 'current',
              reviewAfter: new Date(
                Date.parse(old.updated_at) + (origin === 'imported' ? 90 : 180) * 86400000,
              ).toISOString(),
              reviewedAt: null,
              confidence: 0.8,
              reviewEvidence: null,
            });
          }
        for (const old of origins)
          query(
            'MERGE(s:BrainOrigin {id:$id}) ON CREATE SET s.memoryId=$memoryId,s.originKey=$originKey,s.provider=$provider,s.path=$path,s.sourceJson=$sourceJson,s.importedAt=$importedAt',
            {
              id: hash(`${old.memory_id}\0${old.origin_key}`),
              memoryId: old.memory_id,
              originKey: old.origin_key,
              provider: old.provider,
              path: old.path,
              sourceJson: old.source_json,
              importedAt: old.imported_at,
            },
          );
        for (const old of links) {
          const values = {
            id: old.id,
            type: old.type,
            createdAt: old.created_at,
            weight: 1,
            semanticScore: null,
            correlationScore: null,
            distance: 0,
            inferred: false,
            model: null,
          };
          query(
            `MATCH(a:BrainMemory {id:$fromId}),(b:BrainMemory {id:$toId}) CREATE(a)-[:BrainLink {${Object.keys(values)
              .map((key) => `${key}:$${key}`)
              .join(',')}}]->(b)`,
            { fromId: old.from_id, toId: old.to_id, ...values },
          );
        }
        for (const old of vectors) setEmbedding(old.memory_id, JSON.parse(old.vector_json), { model: old.model });
        query("CREATE(:BrainMeta {id:'sqlite-migration-v1',value:$value})", {
          value: JSON.stringify({ at: now(), memories: memories.length, source: legacyFile }),
        });
      });
    } finally {
      legacy.close();
    }
  }
  try {
    reload();
    migrateLegacy();
  } catch (error) {
    connection.closeSync();
    database.closeSync();
    throw error;
  }
  function close() {
    if (closed) return;
    closed = true;
    connection.closeSync();
    database.closeSync();
  }
  return {
    stats,
    list,
    get,
    remember,
    archive,
    review,
    search,
    relate,
    graph,
    sources,
    importText,
    syncCatalog,
    reconcileImport,
    setEmbedding,
    getEmbeddings,
    unembedded,
    embeddingStats,
    nearest,
    rebuildSemanticRelations,
    rebuildSemanticRelationsAsync,
    batch: write,
    close,
  };
}
module.exports = { createMemoryStore };
