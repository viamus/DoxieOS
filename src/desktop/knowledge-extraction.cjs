const { createHash } = require('node:crypto');

// Extraction is a projection of literal Brain evidence. It never reads files,
// invokes a model, resolves a name, or treats a detected statement as verified.
const defaults = {
  maxInputBytes: 256 * 1024,
  maxEntities: 64,
  maxParameters: 64,
  maxValueBytes: 12000,
  maxEvidenceBytes: 600,
  maxKeyBytes: 1024,
};
const secretKey =
  /(?:password|passwd|secret|credential|authorization|private.?key|connection.?string|(?:api|access|refresh|auth|bearer)[_-]?(?:key|token)|^(?:token|account.?key|sas.?token|signature|sig)$)/i;
const secretValue =
  /-----BEGIN [\w ]*PRIVATE KEY-----|\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[A-Z0-9]{16}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.)|\bBearer\s+[A-Za-z0-9._~-]{12,}/i;
const unsafeKey = (key) => ['__proto__', 'constructor', 'prototype'].includes(key) || secretKey.test(key);
const bytes = (value) => Buffer.byteLength(typeof value === 'string' ? value : JSON.stringify(value));
const fingerprint = (value) => createHash('sha256').update(value).digest('hex').slice(0, 24);
const fold = (value) =>
  String(value)
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase();
const canonical = (value) =>
  Array.isArray(value)
    ? value.map(canonical)
    : value && typeof value === 'object'
      ? Object.fromEntries(
          Object.keys(value)
            .sort()
            .map((key) => [key, canonical(value[key])]),
        )
      : value;
const overlaps = (span, ranges) => ranges.some((range) => span.start < range.end && span.end > range.start);
const quoted = (value) => /^(["'`]).*\1$/s.test(value.trim());
const unwrap = (value) => (quoted(value) ? value.trim().slice(1, -1) : value.trim());

function safeUrl(value) {
  try {
    const url = new URL(value);
    return (
      ['https:', 'http:'].includes(url.protocol) &&
      url.hostname &&
      !url.username &&
      !url.password &&
      [...url.searchParams.keys()].every(
        (key) => !secretKey.test(key) && !/^(?:key|x-amz-signature|x-goog-signature|sessionid)$/i.test(key),
      ) &&
      !secretValue.test(value)
    );
  } catch {
    return false;
  }
}
function safeString(value) {
  if (
    secretValue.test(value) ||
    /(?:password|passwd|secret|credential|authorization|private.?key|connection.?string|(?:api|access|refresh|auth|bearer)[_-]?(?:key|token)|\btoken)\s*[=:]\s*\S+/i.test(
      value,
    )
  )
    return false;
  const urls = value.match(/https?:\/\/[^\s<>"'`]+/giu) || [];
  return urls.every(safeUrl);
}
function excerpt(text, maximum) {
  let result = '',
    size = 0;
  for (const character of text) {
    const cost = bytes(character);
    if (size + cost > maximum) break;
    result += character;
    size += cost;
  }
  return result;
}

// A small JSON reader retains exact property spans, including nested objects.
// JSON.parse validates every scalar; executable JavaScript is never evaluated.
function jsonTree(text, start) {
  let cursor = start,
    count = 0;
  const whitespace = () => {
    while (/\s/.test(text[cursor] || '') && cursor < text.length) cursor++;
  };
  function string() {
    const begin = cursor++;
    while (cursor < text.length) {
      if (text[cursor] === '\\') {
        cursor += 2;
        continue;
      }
      if (text[cursor++] === '"') return { value: JSON.parse(text.slice(begin, cursor)), start: begin, end: cursor };
    }
    throw new Error('Incomplete JSON string.');
  }
  function read(depth = 0) {
    if (++count > 1000 || depth > 12) throw new Error('JSON structure limit.');
    whitespace();
    const begin = cursor;
    if (text[cursor] === '"') return string();
    if (text[cursor] === '{') {
      cursor++;
      whitespace();
      const entries = [];
      while (text[cursor] !== '}') {
        if (text[cursor] !== '"') throw new Error('Expected JSON key.');
        const key = string();
        whitespace();
        if (text[cursor++] !== ':') throw new Error('Expected JSON colon.');
        const child = read(depth + 1);
        entries.push({ key: key.value, start: key.start, child });
        whitespace();
        if (text[cursor] === '}') break;
        if (text[cursor++] !== ',') throw new Error('Expected JSON separator.');
        whitespace();
      }
      if (text[cursor++] !== '}') throw new Error('Incomplete JSON object.');
      return {
        start: begin,
        end: cursor,
        entries,
        value: Object.fromEntries(entries.map((entry) => [entry.key, entry.child.value])),
      };
    }
    if (text[cursor] === '[') {
      cursor++;
      whitespace();
      const items = [];
      while (text[cursor] !== ']') {
        items.push(read(depth + 1));
        whitespace();
        if (text[cursor] === ']') break;
        if (text[cursor++] !== ',') throw new Error('Expected JSON separator.');
      }
      if (text[cursor++] !== ']') throw new Error('Incomplete JSON array.');
      return { start: begin, end: cursor, items, value: items.map((item) => item.value) };
    }
    const token = text.slice(cursor).match(/^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/)?.[0];
    if (!token) throw new Error('Expected literal JSON value.');
    cursor += token.length;
    return { start: begin, end: cursor, value: JSON.parse(token) };
  }
  return read();
}

function extractKnowledge(memory, options = {}) {
  if (!memory || typeof memory.content !== 'string')
    throw new TypeError('Extract knowledge from a Brain memory with text content.');
  const limits = Object.fromEntries(
    Object.entries(defaults).map(([key, maximum]) => [
      key,
      Number.isSafeInteger(options[key]) && options[key] > 0 ? Math.min(options[key], maximum) : maximum,
    ]),
  );
  const text = memory.content;
  const sourceMemoryId = typeof memory.id === 'string' && memory.id ? memory.id : `text-${fingerprint(text)}`;
  if (bytes(sourceMemoryId) > 1024 || !safeString(sourceMemoryId))
    throw new TypeError('Invalid Brain source memory identity.');
  const result = { entities: [], parameters: [], truncated: false };
  if (bytes(text) > limits.maxInputBytes) return { ...result, truncated: true };
  const seen = { entities: new Set(), parameters: new Set() };
  const blockedUrls = [],
    urlRanges = [],
    structured = [];
  const lineStarts = [0];
  for (let offset = 0; offset < text.length; offset++) if (text[offset] === '\n') lineStarts.push(offset + 1);
  function lineAt(offset) {
    let low = 0,
      high = lineStarts.length;
    while (low + 1 < high) {
      const middle = (low + high) >> 1;
      if (lineStarts[middle] <= offset) low = middle;
      else high = middle;
    }
    return low + 1;
  }
  function span(start, end) {
    const snippet = excerpt(text.slice(start, end), limits.maxEvidenceBytes);
    const boundedEnd = start + snippet.length;
    return {
      memoryId: sourceMemoryId,
      start,
      end: boundedEnd,
      startLine: lineAt(start),
      endLine: lineAt(Math.max(start, boundedEnd - 1)),
      excerpt: snippet,
    };
  }
  function evidence(ranges) {
    const safe = ranges.filter(
      (range) => safeString(text.slice(range.start, range.end)) && !overlaps(range, blockedUrls),
    );
    if (!safe.length) return null;
    const spans = safe.slice(0, 8).map((range) => span(range.start, range.end));
    return { ...spans[0], ...(spans.length > 1 ? { spans } : {}) };
  }
  function addEntity(entityType, rawValue, range) {
    const value = rawValue.normalize('NFC').trim();
    if (!value || !safeString(value) || overlaps(range, blockedUrls)) return;
    if (bytes(value) > 1024) {
      result.truncated = true;
      return;
    }
    const key = `entity-${fingerprint(`${entityType}\0${value}`)}`;
    if (seen.entities.has(key)) return;
    if (result.entities.length >= limits.maxEntities) {
      result.truncated = true;
      return;
    }
    const proof = evidence([range]);
    if (!proof) return;
    seen.entities.add(key);
    result.entities.push({
      key,
      title: excerpt(`${entityType}: ${value}`, 220),
      entityType,
      value,
      content: `${entityType}: ${value}`,
      tags: ['extracted', entityType],
      sourceMemoryId,
      evidence: proof,
    });
  }
  function sanitize(node, depth = 0) {
    if (depth > 8) return { rejected: true };
    if (node.entries) {
      const values = [],
        ranges = [];
      let changed = false;
      for (const entry of node.entries) {
        if (unsafeKey(entry.key) || bytes(entry.key) > limits.maxKeyBytes) {
          changed = true;
          continue;
        }
        const child = sanitize(entry.child, depth + 1);
        if (child.rejected) {
          changed = true;
          continue;
        }
        values.push([entry.key, child.value]);
        changed ||= child.changed;
        ranges.push(...(child.changed ? child.ranges : [{ start: entry.start, end: entry.child.end }]));
      }
      if (!values.length) return { rejected: true };
      return { value: Object.fromEntries(values), changed, ranges };
    }
    if (node.items) {
      if (node.items.length > 128) return { rejected: true };
      const items = node.items.map((item) => sanitize(item, depth + 1));
      if (items.some((item) => item.rejected || item.changed)) return { rejected: true };
      return { value: items.map((item) => item.value), changed: false, ranges: [{ start: node.start, end: node.end }] };
    }
    if (typeof node.value === 'string' && (!safeString(node.value) || bytes(node.value) > limits.maxValueBytes))
      return { rejected: true };
    if (typeof node.value === 'number' && !Number.isFinite(node.value)) return { rejected: true };
    return { value: node.value, changed: false, ranges: [{ start: node.start, end: node.end }] };
  }
  function addParameter(values, ranges) {
    if (!values || Array.isArray(values) || typeof values !== 'object' || !Object.keys(values).length) return;
    if (Object.keys(values).some((key) => !key || unsafeKey(key) || bytes(key) > limits.maxKeyBytes)) return;
    const normalized = canonical(values),
      serialized = JSON.stringify(normalized);
    if (bytes(serialized) > limits.maxValueBytes) {
      result.truncated = true;
      return;
    }
    if (!safeString(serialized)) return;
    const key = `parameter-${fingerprint(serialized)}`;
    if (seen.parameters.has(key)) return;
    if (result.parameters.length >= limits.maxParameters) {
      result.truncated = true;
      return;
    }
    const proof = evidence(ranges);
    if (!proof) return;
    seen.parameters.add(key);
    const names = Object.keys(normalized);
    const title =
      names.length <= 3
        ? `Parameters: ${names.join(', ')}`
        : `Parameters: ${names.slice(0, 2).join(', ')} (+${names.length - 2})`;
    result.parameters.push({
      key,
      title: excerpt(title, 220),
      values: normalized,
      parameters: normalized,
      content: `Explicit parameters: ${serialized}`,
      tags: ['extracted', 'literal-parameters'],
      sourceMemoryId,
      evidence: proof,
    });
  }

  // Record complete URL ranges before scanning paths; credentials in a URL
  // cannot reappear as an apparently safe hostname or file-path entity.
  for (const match of text.matchAll(/\bhttps?:\/\/[^\s<>"'`\]}]+/giu)) {
    const raw = match[0].replace(/[),.;!?]+$/, '');
    const range = { start: match.index, end: match.index + raw.length };
    urlRanges.push(range);
    if (!safeUrl(raw)) {
      blockedUrls.push(range);
      continue;
    }
    const url = new URL(raw),
      parts = url.pathname.split('/').filter(Boolean);
    let entityType = 'endpoint',
      value = url.href;
    if (['github.com', 'bitbucket.org'].includes(url.hostname) && parts.length >= 2) {
      entityType = 'repository';
      value = `${url.origin}/${parts[0]}/${parts[1].replace(/\.git$/i, '')}`;
    } else if (url.hostname === 'gitlab.com' && parts.length >= 2) {
      entityType = 'repository';
      value = `${url.origin}/${url.pathname
        .split('/-/')[0]
        .replace(/^\/+|\/+$/g, '')
        .replace(/\.git$/i, '')}`;
    } else if (parts.indexOf('_git') >= 1 && parts[parts.indexOf('_git') + 1]) {
      entityType = 'repository';
      value = `${url.origin}/${parts.slice(0, parts.indexOf('_git') + 2).join('/')}`;
    } else if (url.hostname === 'www.npmjs.com' && parts[0] === 'package' && parts.length >= 2) {
      entityType = 'package';
      value = decodeURIComponent(parts.slice(1).join('/'));
    }
    addEntity(entityType, value, range);
  }

  // Parse complete JSON objects, including fenced code and assignment values.
  // A value containing credential fields keeps only literal safe fields and
  // points its evidence at their precise, credential-free source spans.
  for (let offset = 0; offset < text.length; offset++) {
    if (text[offset] !== '{') continue;
    const prefix = text.slice(Math.max(0, text.lastIndexOf('\n', offset) + 1), offset).trim();
    if (prefix && !/[=:]\s*$/.test(prefix)) continue;
    try {
      const tree = jsonTree(text, offset);
      const safe = sanitize(tree);
      structured.push({ start: offset, end: tree.end });
      if (!safe.rejected) addParameter(safe.value, safe.changed ? safe.ranges : [{ start: offset, end: tree.end }]);
      offset = tree.end - 1;
    } catch (error) {
      if (/limit/i.test(error.message)) result.truncated = true;
      // Incomplete or executable snippets are not JSON facts.
    }
  }

  const parameterName =
    /(?:url|endpoint|host|port|model|provider|region|language|format|command|directory|path|timeout|retri|retry|max|min|limit|batch|review|required|enabled|interval|size|temperature|top[_-]?p|tentativ|prazo|limite|revisao|obrigator|ativo|habilitad|formato|caminho|diretorio)/i;
  const booleanName = /(?:required|enabled|disabled|allow|deny|review|obrigator|ativo|habilitad|revisao)/i;
  function scalar(raw, key, explicitType) {
    let value = raw.trim().replace(/[,;]\s*$/, '');
    if (!value || !safeString(value) || /\$\{|process\.env\b|os\.environ\b/.test(value)) return undefined;
    if (quoted(value)) {
      if (value.startsWith('"')) {
        try {
          return JSON.parse(value);
        } catch {
          return undefined;
        }
      }
      return value.slice(1, -1);
    }
    try {
      const parsed = JSON.parse(value);
      if (parsed === null || ['number', 'boolean'].includes(typeof parsed))
        return Number.isFinite(parsed) || typeof parsed !== 'number' ? parsed : undefined;
      if (Array.isArray(parsed) || typeof parsed === 'object') {
        const safe = sanitize(jsonTree(value, 0));
        return safe.rejected || safe.changed ? undefined : safe.value;
      }
    } catch {
      /* Plain strings require an explicit setting-like name. */
    }
    if ((explicitType && /^(?:bool|boolean|booleano)$/i.test(explicitType)) || booleanName.test(fold(key))) {
      if (/^(?:yes|sim|on)$/i.test(value)) return true;
      if (/^(?:no|nao|não|off)$/i.test(value)) return false;
    }
    if (explicitType && /^(?:int|integer|number|float|double|numero|número)$/i.test(explicitType)) return undefined;
    if (explicitType && /^(?:string|text|texto)$/i.test(explicitType)) return value;
    if (
      !parameterName.test(fold(key)) ||
      value.length > 1024 ||
      /[{};]|(?:=>|\bfunction\b|\breturn\b)|\b[A-Za-z_$][\w.$]*\s*\(/.test(value)
    )
      return undefined;
    return value;
  }

  const labels = new Map([
    ['organization', 'organization'],
    ['organisation', 'organization'],
    ['org', 'organization'],
    ['company', 'organization'],
    ['empresa', 'organization'],
    ['organizacao', 'organization'],
    ['project', 'project'],
    ['projeto', 'project'],
    ['repository', 'repository'],
    ['repo', 'repository'],
    ['repositorio', 'repository'],
    ['package', 'package'],
    ['pacote', 'package'],
    ['tool', 'tool'],
    ['ferramenta', 'tool'],
    ['executable', 'executable'],
    ['executavel', 'executable'],
    ['cli', 'executable'],
  ]);
  const lines = text.split(/\r?\n/);
  let lineOffset = 0,
    table;
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index],
      range = { start: lineOffset, end: lineOffset + line.length };
    if (line.includes('|')) {
      let cellOffset = lineOffset;
      const cells = line.split('|').map((raw) => {
        const cell = { raw: raw.trim(), start: cellOffset + raw.indexOf(raw.trim()) };
        cellOffset += raw.length + 1;
        return cell;
      });
      if (!cells[0]?.raw) cells.shift();
      if (!cells.at(-1)?.raw) cells.pop();
      const headings = cells.map((cell) => fold(unwrap(cell.raw)));
      const keyColumn = headings.findIndex((value) =>
        /^(?:key|name|parameter|setting|chave|nome|parametro|configuracao)$/.test(value),
      );
      const valueColumn = headings.findIndex((value) => /^(?:value|valor|default|padrao)$/.test(value));
      if (keyColumn >= 0 && valueColumn >= 0)
        table = {
          key: keyColumn,
          value: valueColumn,
          type: headings.findIndex((value) => /^(?:type|tipo)$/.test(value)),
        };
      else if (table && !cells.every((cell) => /^:?-+:?$/.test(cell.raw))) {
        const key = unwrap(cells[table.key]?.raw || ''),
          cell = cells[table.value];
        if (key && cell && !unsafeKey(key) && bytes(key) <= limits.maxKeyBytes) {
          const value = scalar(unwrap(cell.raw), key, unwrap(cells[table.type]?.raw || ''));
          if (value !== undefined)
            addParameter({ [key]: value }, [
              {
                start: Math.min(cells[table.key].start, cell.start),
                end: Math.max(cells[table.key].start + cells[table.key].raw.length, cell.start + cell.raw.length),
              },
            ]);
        }
      }
    } else table = undefined;

    const labelled = line.match(/^\s*(?:[-*]\s+)?([\p{L} ]{2,30})\s*[:=]\s*(.+?)\s*$/u);
    if (labelled) {
      const entityType = labels.get(fold(labelled[1].trim()));
      const value = unwrap(labelled[2]);
      const start = lineOffset + line.lastIndexOf(labelled[2]);
      if (
        entityType &&
        value.length <= 200 &&
        !/^(?:none|unknown|n\/a|tbd|a confirmar)$/i.test(value) &&
        !/^https?:\/\//i.test(value)
      )
        addEntity(entityType, value, { start, end: start + labelled[2].length });
    }

    if (!overlaps(range, structured)) {
      const assignment = line.match(
        /^\s*(?:[-*]\s+)?(?:export\s+)?(?:(?:const|let|var|set)\s+)?(?:\$env:)?["'`]?([\p{L}_][\p{L}\p{N}_. -]*?)["'`]?\s*([:=])(?![=>])\s*(.+?)\s*$/u,
      );
      if (assignment) {
        const key = assignment[1].trim(),
          raw = assignment[3];
        if (!unsafeKey(key) && bytes(key) <= limits.maxKeyBytes) {
          const value = scalar(/^\s*`/.test(line) ? raw.replace(/`\s*$/, '') : raw, key);
          if (value !== undefined) addParameter({ [key]: value }, [range]);
        }
      }
      for (const match of line.matchAll(/`([\p{L}_][\p{L}\p{N}_.-]*)\s*=(?!=)\s*([^`\r\n]+)`/gu)) {
        if (unsafeKey(match[1]) || bytes(match[1]) > limits.maxKeyBytes) continue;
        const value = scalar(match[2], match[1]);
        if (value !== undefined)
          addParameter({ [match[1]]: value }, [
            { start: lineOffset + match.index, end: lineOffset + match.index + match[0].length },
          ]);
      }
    }
    lineOffset += line.length + (text.slice(lineOffset + line.length, lineOffset + line.length + 2) === '\r\n' ? 2 : 1);
  }

  for (const match of text.matchAll(/\bgit@[a-z0-9.-]+:[\w./-]+\.git\b/giu))
    addEntity('repository', match[0], { start: match.index, end: match.index + match[0].length });
  const quotedPaths = [];
  for (const match of text.matchAll(
    /["'`]((?:[A-Za-z]:[\\/]|\.\.?\/|~\/|\/(?:home|usr|opt|var|etc|tmp|workspace|workspaces|app|src|data|mnt)\/)[^"'`\r\n]+)["'`]/gu,
  )) {
    const range = { start: match.index + 1, end: match.index + 1 + match[1].length };
    if (!overlaps(range, urlRanges)) {
      quotedPaths.push(range);
      addEntity('file', match[1], range);
    }
  }
  for (const match of text.matchAll(
    /(?:[A-Za-z]:[\\/][^\s"'`<>|]+|(?:\.\.?\/|~\/|\/(?:home|usr|opt|var|etc|tmp|workspace|workspaces|app|src|data|mnt)\/)[\p{L}\p{N}_@./\\-]+|\b(?:[\p{L}\p{N}_@.-]+\/)+[\p{L}\p{N}_@.-]+\.(?:cjs|mjs|jsx?|tsx?|json|ya?ml|toml|xml|ini|md|txt|csv|py|cs|sh|ps1|bat))(?=$|[\s"'`),;])/gmu,
  )) {
    const range = { start: match.index, end: match.index + match[0].length };
    if (!overlaps(range, [...urlRanges, ...quotedPaths])) addEntity('file', match[0].replace(/[),;.]$/, ''), range);
  }
  const executables =
    'node|npm|npx|pnpm|yarn|bun|deno|python3?|pip3?|uv|git|dotnet|codex|claude|docker|kubectl|helm|az|gh|curl|wget|pwsh|powershell|bash|sh|make|cmake|go|cargo|rustc';
  const shellBlocks = [
    ...text.matchAll(/```(?:sh|shell|bash|powershell|pwsh|console|cmd|bat)\s*\r?\n[\s\S]*?```/giu),
  ].map((match) => ({ start: match.index, end: match.index + match[0].length }));
  for (const match of text.matchAll(
    new RegExp('(?:^|\\n)\\s*(?:\\$|>)?\\s*(' + executables + ')(?=\\s+[\\w./@-])', 'gi'),
  )) {
    const start = match.index + match[0].lastIndexOf(match[1]);
    const range = { start, end: start + match[1].length };
    const tail = text
      .slice(range.end, text.indexOf('\n', range.end) < 0 ? text.length : text.indexOf('\n', range.end))
      .trim();
    if (
      /[\$>]/.test(match[0]) ||
      overlaps(range, shellBlocks) ||
      /^(?:run|install|test|ci|exec|start|build|audit|clone|status|commit|checkout|switch|fetch|pull|push|diff|mod|add|restore|publish|--?[\w-]+)\b/.test(
        tail,
      )
    )
      addEntity('executable', match[1].toLowerCase(), range);
  }
  for (const match of text.matchAll(
    /\brequire\s*\(\s*["']([^"'\r\n]+)["']\s*\)|(?:^|\n)\s*(?:import|export)\b[^\r\n]*?\bfrom\s+["']([^"'\r\n]+)["']/gu,
  )) {
    const value = match[1] || match[2];
    const range = {
      start: match.index + match[0].indexOf(value),
      end: match.index + match[0].indexOf(value) + value.length,
    };
    if (!/^(?:[./]|[A-Za-z]:[\\/]|https?:)/.test(value)) addEntity('package', value, range);
  }
  for (const match of text.matchAll(/\b(?:mcp__[\w]+|memory_(?:search|get|remember|relate|graph|review))(?=\s*\(|`)/gu))
    addEntity('tool', match[0], { start: match.index, end: match.index + match[0].length });

  return result;
}

module.exports = { extractKnowledge };
