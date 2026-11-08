const { randomUUID, createHash } = require('node:crypto');
const { mkdirSync, lstatSync } = require('node:fs');
const { dirname, join, resolve } = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const { boundedMemoryNode, boundedMemoryGraph } = require('./memory-view.cjs');

const kinds = new Set(['memory', 'entity', 'parameter']);
const relationTypes = new Set(['supports', 'contradicts', 'supersedes', 'related', 'mentions']);
const credentialKey = /(?:password|passwd|secret|credential|authorization|private.?key|connection.?string|(?:api|access|refresh|auth|bearer)[_-]?(?:key|token))/i;
const credentialValue = /-----BEGIN [\w ]*PRIVATE KEY-----|\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.)|https?:\/\/[^\s/@:]+:[^\s/@]+@/;
const now = () => new Date().toISOString();
const hash = value => createHash('sha256').update(value).digest('hex');
const byteLength = value => Buffer.byteLength(typeof value === 'string' ? value : JSON.stringify(value), 'utf8');
const limitValue = (value, fallback, maximum) => Number.isSafeInteger(Number(value)) && Number(value) > 0 ? Math.min(Number(value), maximum) : fallback;
const scopeValue = value => value === undefined || value === null || value === '' ? null : String(value).slice(0, 200);

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
    if (Array.isArray(item)) return item.map(child => validate(child, depth + 1));
    if (item && typeof item === 'object' && [Object.prototype, null].includes(Object.getPrototypeOf(item))) {
      const result = Object.create(null);
      for (const key of Object.keys(item).sort()) {
        if (['__proto__', 'constructor', 'prototype'].includes(key) || credentialKey.test(key)) throw new Error('Credentials cannot be saved as learned parameters.');
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
  if (source !== undefined && (source === null || typeof source !== 'object' || Array.isArray(source))) throw new Error('Memory provenance must be an object.');
  const result = {};
  for (const key of ['provider', 'path', 'runId', 'section', 'kind', 'label', 'url']) {
    if (source?.[key] !== undefined) {
      if (typeof source[key] !== 'string' || source[key].length > 4096 || credentialValue.test(source[key])) throw new Error('Invalid memory provenance.');
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
  return { source: result, identity: JSON.stringify([result.provider, pathKey, result.section || '', result.chunk ?? null]) };
}

function createMemoryStore({ root, databasePath } = {}) {
  if (typeof root !== 'string' || !root.trim()) throw new Error('A memory-store root is required.');
  const file = databasePath || join(resolve(root), '.memory', 'knowledge.db');
  if (file !== ':memory:') {
    const directory = dirname(resolve(file));
    if (lstatSync(directory, { throwIfNoEntry: false })?.isSymbolicLink() || lstatSync(file, { throwIfNoEntry: false })?.isSymbolicLink()) throw new Error('The memory database cannot be a symbolic link.');
    mkdirSync(directory, { recursive: true });
  }
  const db = new DatabaseSync(file);
  db.exec(`PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;
    CREATE TABLE IF NOT EXISTS memories (
      id TEXT PRIMARY KEY, kind TEXT NOT NULL, title TEXT NOT NULL, content TEXT NOT NULL,
      library_id TEXT, catalog_id TEXT, tags_json TEXT NOT NULL, parameters_json TEXT,
      fingerprint TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, superseded_by TEXT REFERENCES memories(id), archived_at TEXT
    );
    CREATE INDEX IF NOT EXISTS memory_scope ON memories(catalog_id,library_id,kind,superseded_by);
    CREATE INDEX IF NOT EXISTS memory_fingerprint ON memories(fingerprint);
    CREATE TABLE IF NOT EXISTS memory_origins (
      id INTEGER PRIMARY KEY, memory_id TEXT NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
      origin_key TEXT NOT NULL, provider TEXT NOT NULL, path TEXT, source_json TEXT NOT NULL, imported_at TEXT NOT NULL,
      UNIQUE(memory_id,origin_key)
    );
    CREATE INDEX IF NOT EXISTS memory_origin_identity ON memory_origins(origin_key);
    CREATE TABLE IF NOT EXISTS memory_relations (
      id TEXT PRIMARY KEY, from_id TEXT NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
      to_id TEXT NOT NULL REFERENCES memories(id) ON DELETE CASCADE, type TEXT NOT NULL, created_at TEXT NOT NULL,
      UNIQUE(from_id,to_id,type)
    );
    CREATE INDEX IF NOT EXISTS memory_edge_from ON memory_relations(from_id);
    CREATE INDEX IF NOT EXISTS memory_edge_to ON memory_relations(to_id);
    CREATE TABLE IF NOT EXISTS memory_embeddings (
      memory_id TEXT NOT NULL REFERENCES memories(id) ON DELETE CASCADE, model TEXT NOT NULL,
      vector_json TEXT NOT NULL, dimensions INTEGER NOT NULL, created_at TEXT NOT NULL,
      PRIMARY KEY(memory_id,model)
    );
    CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(title,content,tags_json,content='memories',content_rowid='rowid',tokenize='unicode61 remove_diacritics 2');
    CREATE TRIGGER IF NOT EXISTS memory_insert AFTER INSERT ON memories BEGIN
      INSERT INTO memory_fts(rowid,title,content,tags_json) VALUES(new.rowid,new.title,new.content,new.tags_json);
    END;
    CREATE TRIGGER IF NOT EXISTS memory_delete AFTER DELETE ON memories BEGIN
      INSERT INTO memory_fts(memory_fts,rowid,title,content,tags_json) VALUES('delete',old.rowid,old.title,old.content,old.tags_json);
    END;
    CREATE TRIGGER IF NOT EXISTS memory_update AFTER UPDATE ON memories BEGIN
      INSERT INTO memory_fts(memory_fts,rowid,title,content,tags_json) VALUES('delete',old.rowid,old.title,old.content,old.tags_json);
      INSERT INTO memory_fts(rowid,title,content,tags_json) VALUES(new.rowid,new.title,new.content,new.tags_json);
    END;`);
  if (!db.prepare('PRAGMA table_info(memories)').all().some(column => column.name === 'archived_at')) db.exec('ALTER TABLE memories ADD COLUMN archived_at TEXT');

  function write(action) {
    db.exec('BEGIN IMMEDIATE');
    try { const result = action(); db.exec('COMMIT'); return result; }
    catch (error) { db.exec('ROLLBACK'); throw error; }
  }
  function node(row, { previewBytes } = {}) {
    if (!row) return null;
    const provenance = db.prepare('SELECT source_json,imported_at FROM memory_origins WHERE memory_id=? ORDER BY id DESC').all(row.id)
      .map(item => ({ ...JSON.parse(item.source_json), importedAt: item.imported_at }));
    const content = previewBytes === undefined ? row.content : shorten(row.content, previewBytes);
    return { id: row.id, kind: row.kind, title: row.title, content, libraryId: row.library_id, catalogId: row.catalog_id,
      tags: JSON.parse(row.tags_json), parameters: row.parameters_json ? JSON.parse(row.parameters_json) : null,
      source: provenance[0] || {}, provenance: previewBytes === undefined ? provenance : provenance.slice(0, 3),
      createdAt: row.created_at, updatedAt: row.updated_at, supersededBy: row.superseded_by, archivedAt: row.archived_at || null,
      contentTruncated: content !== row.content, totalContentBytes: byteLength(row.content) };
  }
  const get = id => node(db.prepare('SELECT * FROM memories WHERE id=?').get(String(id)));
  function filters(options = {}, alias = 'm') {
    const clauses = [];
    const params = [];
    if (!options.includeSuperseded) clauses.push(`${alias}.superseded_by IS NULL`);
    if (!options.includeArchived) clauses.push(`${alias}.archived_at IS NULL`);
    for (const [key, column] of [['libraryId', 'library_id'], ['catalogId', 'catalog_id'], ['kind', 'kind']]) {
      if (options[key] === undefined || options[key] === '') continue;
      if (options[key] === null) clauses.push(`${alias}.${column} IS NULL`);
      else { clauses.push(`${alias}.${column}=?`); params.push(String(options[key])); }
    }
    return { sql: clauses.length ? clauses.join(' AND ') : '1=1', params };
  }
  function list(options = {}) {
    const filter = filters(options);
    const rows = db.prepare(`SELECT m.* FROM memories m WHERE ${filter.sql} ORDER BY m.updated_at DESC,m.rowid DESC LIMIT ?`)
      .all(...filter.params, limitValue(options.limit, 100, 500));
    return rows.map(row => boundedMemoryNode({ ...node(row), content: shorten(row.content, 1200),
      contentTruncated: byteLength(row.content) > 1200 }, 4000));
  }
  function insertEdge(fromId, toId, type) {
    if (fromId === toId) throw new Error('A memory cannot relate to itself.');
    if (!db.prepare('SELECT 1 FROM memories WHERE id=?').get(fromId) || !db.prepare('SELECT 1 FROM memories WHERE id=?').get(toId)) throw new Error('Both related memories must exist.');
    if (type === 'supersedes') {
      const cycle = db.prepare(`WITH RECURSIVE ancestors(id) AS (SELECT to_id FROM memory_relations WHERE from_id=? AND type='supersedes'
        UNION SELECT r.to_id FROM memory_relations r JOIN ancestors a ON r.from_id=a.id WHERE r.type='supersedes') SELECT 1 FROM ancestors WHERE id=? LIMIT 1`).get(toId, fromId);
      if (cycle) throw new Error('Memory revisions cannot form a cycle.');
    }
    db.prepare('INSERT OR IGNORE INTO memory_relations(id,from_id,to_id,type,created_at) VALUES(?,?,?,?,?)').run(randomUUID(), fromId, toId, type, now());
    if (type === 'supersedes') db.prepare('UPDATE memories SET superseded_by=? WHERE id=?').run(fromId, toId);
    const edge = db.prepare('SELECT * FROM memory_relations WHERE from_id=? AND to_id=? AND type=?').get(fromId, toId, type);
    return { id: edge.id, fromId, toId, from: fromId, to: toId, type, createdAt: edge.created_at };
  }
  function relate({ fromId, toId, type = 'related' } = {}) {
    if (type === 'supported') type = 'supports';
    if (!relationTypes.has(type)) throw new Error('Unknown memory relation type.');
    return write(() => insertEdge(String(fromId), String(toId), type));
  }
  function remember(input = {}) {
    const kind = input.kind || 'memory';
    if (!kinds.has(kind)) throw new Error('Memory kind must be memory, entity, or parameter.');
    if (typeof input.title !== 'string' || !input.title.trim() || input.title.length > 240) throw new Error('A memory title of at most 240 characters is required.');
    const title = input.title.trim();
    const parameters = parametersJson(input.parameters);
    const content = input.content ?? (parameters || '');
    if (typeof content !== 'string' || !content.trim() || byteLength(content) > 256 * 1024) throw new Error('Memory content must be text of at most 256 KiB.');
    if (kind === 'parameter' && parameters === null) throw new Error('A parameter memory needs typed parameters.');
    if (input.tags !== undefined && (!Array.isArray(input.tags) || input.tags.length > 40 || input.tags.some(tag => typeof tag !== 'string' || tag.length > 100))) throw new Error('Memory tags must be a short text list.');
    const tags = [...new Set((input.tags || []).map(tag => tag.trim()).filter(Boolean))];
    const libraryId = scopeValue(input.libraryId);
    const catalogId = scopeValue(input.catalogId);
    const { source, identity } = normalizedSource(input.source, title);
    const originKey = hash(JSON.stringify([catalogId, libraryId, identity]));
    const fingerprint = hash(JSON.stringify([kind, title, content, parameters, [...tags].sort()]));
    return write(() => {
      const previous = input.id ? db.prepare('SELECT * FROM memories WHERE id=?').get(String(input.id))
        : db.prepare('SELECT m.* FROM memories m JOIN memory_origins o ON o.memory_id=m.id WHERE o.origin_key=? ORDER BY o.id DESC LIMIT 1').get(originKey);
      if (input.id && !previous) throw new Error('The memory to revise was not found.');
      if (previous?.fingerprint === fingerprint) return node(previous);
      const existing = db.prepare(`SELECT * FROM memories WHERE fingerprint=? AND library_id IS ? AND catalog_id IS ?
        AND superseded_by IS NULL AND archived_at IS NULL ORDER BY rowid DESC LIMIT 1`).get(fingerprint, libraryId, catalogId);
      const id = existing?.id || randomUUID();
      if (!existing) db.prepare(`INSERT INTO memories(id,kind,title,content,library_id,catalog_id,tags_json,parameters_json,fingerprint,created_at,updated_at)
        VALUES(?,?,?,?,?,?,?,?,?,?,?)`).run(id, kind, title, content, libraryId, catalogId, JSON.stringify(tags), parameters, fingerprint, now(), now());
      db.prepare('INSERT OR IGNORE INTO memory_origins(memory_id,origin_key,provider,path,source_json,imported_at) VALUES(?,?,?,?,?,?)')
        .run(id, originKey, source.provider, source.path || null, JSON.stringify(source), now());
      if (previous && previous.id !== id) insertEdge(id, previous.id, 'supersedes');
      return get(id);
    });
  }
  function search({ query = '', limit = 8, maxBytes = 16000, ...options } = {}) {
    if (typeof query !== 'string' || query.length > 512) throw new Error('A search query of at most 512 characters is required.');
    const tokens = query.match(/[\p{L}\p{N}_-]+/gu) || [];
    const count = limitValue(limit, 8, 100);
    const budget = limitValue(maxBytes, 16000, 256 * 1024);
    const responseQuery = shorten(query, Math.max(0, budget - 512));
    const queryMetadata = responseQuery === query ? {} : { queryTruncated: true };
    const resultBudget = budget - byteLength({ query: responseQuery, results: [], truncated: false, bytes: budget, ...queryMetadata }) + 2;
    if (!tokens.length) return { query: responseQuery, results: [], truncated: responseQuery !== query, bytes: 2, ...queryMetadata };
    const ftsQuery = tokens.slice(0, 30).map(token => `"${token.replace(/"/g, '""')}"*`).join(' OR ');
    const filter = filters(options);
    const rows = db.prepare(`SELECT m.*,bm25(memory_fts,5.0,1.0,2.0) AS rank FROM memory_fts JOIN memories m ON m.rowid=memory_fts.rowid
      WHERE memory_fts MATCH ? AND ${filter.sql} ORDER BY rank,m.rowid DESC LIMIT ?`).all(ftsQuery, ...filter.params, count + 1);
    const results = [];
    let truncated = rows.length > count || responseQuery !== query;
    for (const row of rows.slice(0, count)) {
      const position = Math.max(0, row.content.toLocaleLowerCase().indexOf(tokens[0].toLocaleLowerCase()));
      const window = row.content.slice(Math.max(0, position - 160));
      const remaining = resultBudget - byteLength(results) - 2;
      if (remaining < 256) { truncated = true; break; }
      const allowance = Math.min(remaining, Math.max(256, Math.floor(resultBudget / count) - 4));
      const preview = shorten(window, 2000);
      const hit = boundedMemoryNode({ ...node(row), content: preview, excerpt: preview,
        contentTruncated: preview !== row.content, score: -row.rank }, allowance);
      if (byteLength([...results, hit]) > resultBudget) { truncated = true; break; }
      results.push(hit);
      if (hit.contentTruncated || hit.parametersTruncated || hit.provenanceTruncated || hit.tagsTruncated) truncated = true;
    }
    return { query: responseQuery, results, truncated, bytes: byteLength(results), ...queryMetadata };
  }
  function graph({ query = '', limit = 80, includeHistory = false, ...options } = {}) {
    const maximum = limitValue(limit, 80, 300);
    const graphOptions = { ...options, includeSuperseded: true, includeArchived: includeHistory || options.includeArchived };
    const seed = query ? search({ query, ...graphOptions, limit: maximum, maxBytes: 256 * 1024 }).results : list({ ...graphOptions, limit: maximum + 1 });
    const ids = new Set(seed.slice(0, maximum).map(item => item.id));
    let truncated = seed.length > maximum;
    if (query && ids.size) {
      const marks = [...ids].map(() => '?').join(',');
      const neighbors = db.prepare(`SELECT from_id,to_id FROM memory_relations WHERE from_id IN (${marks}) OR to_id IN (${marks}) LIMIT ?`).all(...ids, ...ids, maximum * 4);
      const filter = filters(graphOptions);
      for (const edge of neighbors) for (const id of [edge.from_id, edge.to_id]) {
        if (ids.has(id)) continue;
        if (ids.size >= maximum) { truncated = true; continue; }
        if (db.prepare(`SELECT 1 FROM memories m WHERE m.id=? AND ${filter.sql}`).get(id, ...filter.params)) ids.add(id);
      }
    }
    if (!ids.size) return boundedMemoryGraph({ nodes: [], edges: [], truncated });
    const marks = [...ids].map(() => '?').join(',');
    const nodes = db.prepare(`SELECT * FROM memories WHERE id IN (${marks})`).all(...ids).map(row => {
      const result = node(row);
      return { ...result, summary: shorten(result.content, 300) };
    });
    const matchingEdges = db.prepare(`SELECT * FROM memory_relations WHERE from_id IN (${marks}) AND to_id IN (${marks}) ORDER BY created_at LIMIT ?`).all(...ids, ...ids, maximum * 8 + 1);
    if (matchingEdges.length > maximum * 8) truncated = true;
    const edges = matchingEdges.slice(0, maximum * 8)
      .map(row => ({ id: row.id, fromId: row.from_id, toId: row.to_id, from: row.from_id, to: row.to_id, type: row.type, createdAt: row.created_at }));
    return boundedMemoryGraph({ nodes, edges, truncated }, limitValue(options.maxBytes, 16000, 16000));
  }
  function stats() {
    const counts = Object.fromEntries(db.prepare('SELECT kind,COUNT(*) AS count FROM memories WHERE superseded_by IS NULL AND archived_at IS NULL GROUP BY kind').all().map(row => [row.kind, row.count]));
    return { memories: counts.memory || 0, entities: counts.entity || 0, parameters: counts.parameter || 0,
      total: db.prepare('SELECT COUNT(*) AS count FROM memories').get().count,
      relations: db.prepare('SELECT COUNT(*) AS count FROM memory_relations').get().count,
      sources: db.prepare('SELECT COUNT(DISTINCT origin_key) AS count FROM memory_origins').get().count,
      libraries: db.prepare('SELECT COUNT(DISTINCT library_id) AS count FROM memories WHERE library_id IS NOT NULL').get().count };
  }
  function sources() {
    return db.prepare(`SELECT provider,path,COUNT(DISTINCT memory_id) AS memories,MAX(imported_at) AS lastImportedAt
      FROM memory_origins GROUP BY provider,path ORDER BY lastImportedAt DESC LIMIT 1000`).all().map(row => ({ ...row }));
  }
  function archive(id) {
    if (!get(id)) throw new Error('The memory to archive was not found.');
    db.prepare('UPDATE memories SET archived_at=COALESCE(archived_at,?),updated_at=? WHERE id=?').run(now(), now(), id);
    return get(id);
  }
  function importText(input = {}) {
    if (typeof input.content !== 'string') throw new Error('Imported memory content must be text.');
    const chunks = [];
    let content = '';
    let size = 0;
    for (const character of input.content) {
      const bytes = byteLength(character);
      if (size + bytes > 16000 && content) { chunks.push(content); content = ''; size = 0; }
      content += character;
      size += bytes;
    }
    if (content.trim()) chunks.push(content);
    const title = String(input.title || '').trim().slice(0, 220);
    return chunks.map((chunk, index) => remember({ ...input, id: undefined, title: `${title}${chunks.length > 1 ? ` (${index + 1}/${chunks.length})` : ''}`,
      content: chunk, source: { ...input.source, chunk: index } }));
  }
  function embeddingModel(model) {
    if (typeof model !== 'string' || !model.trim() || model.length > 240) throw new Error('An embedding model identifier is required.');
    return model.trim();
  }
  function setEmbedding(id, vector, { model } = {}) {
    model = embeddingModel(model);
    if (!Array.isArray(vector) && !(ArrayBuffer.isView(vector) && !(vector instanceof DataView))) throw new Error('An embedding must be a numeric vector.');
    const values = Array.from(vector);
    if (!values.length || values.length > 4096 || values.some(value => typeof value !== 'number' || !Number.isFinite(value)) || !values.some(value => value !== 0)) throw new Error('An embedding must contain 1–4096 finite numeric values and a nonzero norm.');
    if (!get(id)) throw new Error('The memory to embed was not found.');
    db.prepare(`INSERT INTO memory_embeddings(memory_id,model,vector_json,dimensions,created_at) VALUES(?,?,?,?,?)
      ON CONFLICT(memory_id,model) DO UPDATE SET vector_json=excluded.vector_json,dimensions=excluded.dimensions,created_at=excluded.created_at`)
      .run(id, model, JSON.stringify(values), values.length, now());
    return { id, model, dimensions: values.length };
  }
  function getEmbeddings({ model, limit = 1000, ...options } = {}) {
    model = embeddingModel(model);
    const filter = filters(options);
    return db.prepare(`SELECT e.memory_id,e.model,e.vector_json FROM memory_embeddings e JOIN memories m ON m.id=e.memory_id
      WHERE e.model=? AND ${filter.sql} ORDER BY m.updated_at DESC LIMIT ?`).all(model, ...filter.params, limitValue(limit, 1000, 10000))
      .map(row => ({ id: row.memory_id, model: row.model, vector: JSON.parse(row.vector_json) }));
  }
  function unembedded({ model, limit = 50, ...options } = {}) {
    model = embeddingModel(model);
    const filter = filters(options);
    return db.prepare(`SELECT m.* FROM memories m WHERE ${filter.sql}
      AND NOT EXISTS (SELECT 1 FROM memory_embeddings e WHERE e.memory_id=m.id AND e.model=?) ORDER BY m.rowid LIMIT ?`)
      .all(...filter.params, model, limitValue(limit, 50, 500)).map(row => node(row));
  }
  function embeddingStats({ model, ...options } = {}) {
    model = embeddingModel(model);
    const filter = filters(options);
    const total = db.prepare(`SELECT COUNT(*) AS count FROM memories m WHERE ${filter.sql}`).get(...filter.params).count;
    const indexed = db.prepare(`SELECT COUNT(*) AS count FROM memory_embeddings e JOIN memories m ON m.id=e.memory_id
      WHERE e.model=? AND ${filter.sql}`).get(model, ...filter.params).count;
    return { indexed, pending: total - indexed, total };
  }
  function syncCatalog(storage) {
    let imported = 0;
    let duplicates = 0;
    let skipped = 0;
    for (const library of storage.list('libraries')) for (const memory of library.memories || []) {
      if (!(memory.content || memory.body)?.trim()) { skipped++; continue; }
      const before = stats().total;
      const copies = importText({ title: memory.name || memory.fileName || library.name, content: memory.content || memory.body,
        libraryId: library.id, catalogId: library.catalogId, kind: 'memory', tags: [memory.type, memory.priority].filter(Boolean),
        source: { provider: 'catalog', path: join(library.path, memory.relativePath || memory.fileName || memory.id), label: library.name } });
      const added = stats().total - before;
      imported += added;
      duplicates += copies.length - added;
    }
    return { imported, duplicates, skipped };
  }
  return { stats, list, get, remember, archive, search, relate, graph, sources, importText, syncCatalog,
    setEmbedding, getEmbeddings, unembedded, embeddingStats, close: () => db.close() };
}

module.exports = { createMemoryStore };
