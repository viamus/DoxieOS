const size = value => Buffer.byteLength(JSON.stringify(value), 'utf8');
const textSize = value => Buffer.byteLength(value, 'utf8');
const has = (value, key) => Object.prototype.hasOwnProperty.call(value, key);

function budget(value) {
  if (!Number.isSafeInteger(value) || value < 256) throw new RangeError('Memory previews require a budget of at least 256 bytes.');
  return value;
}
function excerpt(value, maximum) {
  const text = String(value || '');
  if (textSize(text) <= maximum) return text;
  let result = '';
  let bytes = 0;
  for (const character of text) {
    const cost = textSize(character);
    if (bytes + cost > maximum) break;
    result += character;
    bytes += cost;
  }
  return result;
}
function sourcePreview(source) {
  const result = {};
  for (const [key, value] of Object.entries(source || {})) {
    if (textSize(key) > 80) continue;
    if (typeof value === 'string') result[key] = excerpt(value, 160);
    else if (value === null || typeof value === 'boolean' || (typeof value === 'number' && Number.isFinite(value))) result[key] = value;
  }
  return result;
}

function boundedMemoryNode(node, maxBytes = 16000) {
  if (!node) throw new Error('Memory not found.');
  maxBytes = budget(maxBytes);
  const content = String(node.content ?? node.summary ?? '');
  const parameters = node.parameters && typeof node.parameters === 'object' && !Array.isArray(node.parameters) ? node.parameters : null;
  const keys = [...new Set([...(node.availableParameterKeys || []), ...Object.keys(parameters || {})])];
  const provenance = Array.isArray(node.provenance) ? node.provenance : [];
  const tags = Array.isArray(node.tags) ? node.tags : [];
  const source = node.source && typeof node.source === 'object' ? node.source : provenance[0] || {};
  const totalParameterKeys = node.totalParameterKeys ?? keys.length;
  const totalProvenance = node.totalProvenance ?? provenance.length;
  const totalTags = node.totalTags ?? tags.length;
  const result = { id: node.id, title: node.title, kind: node.kind,
    content: excerpt(content, Math.max(32, Math.floor(maxBytes / 3))),
    totalContentBytes: node.totalContentBytes ?? textSize(content),
    tags: [], parameters: parameters ? {} : null, source: sourcePreview(source),
    provenance: provenance.slice(0, 3).map(sourcePreview),
  };
  if (parameters || keys.length || node.parametersTruncated) {
    result.availableParameterKeys = [...keys];
    result.totalParameterKeys = totalParameterKeys;
  }
  const metadata = ['libraryId', 'catalogId', 'createdAt', 'updatedAt', 'supersededBy', 'archivedAt', 'score', 'semanticScore', 'label',
    'origin', 'status', 'lifecycle', 'reviewAfter', 'reviewedAt', 'confidence', 'correlationScore', 'distance'];
  for (const key of metadata) if (node[key] !== undefined) result[key] = node[key];
  const selected = {};
  for (const key of Object.keys(parameters || {}).sort((left, right) => size({ [left]: parameters[left] }) - size({ [right]: parameters[right] }))) {
    const next = { ...selected, [key]: parameters[key] };
    if (size(next) <= maxBytes / 3) selected[key] = parameters[key];
  }
  if (parameters) result.parameters = selected;
  for (const tag of tags) {
    if (size([...result.tags, tag]) > maxBytes / 12) break;
    result.tags.push(tag);
  }
  function flags() {
    result.contentTruncated = Boolean(node.contentTruncated || result.content !== content);
    if (has(node, 'excerpt')) result.excerpt = result.content;
    if (has(node, 'summary')) result.summary = result.content;
    if (parameters || keys.length || node.parametersTruncated) {
      result.parametersTruncated = Boolean(node.parametersTruncated || Object.keys(result.parameters || {}).length < totalParameterKeys);
      result.availableParameterKeysTruncated = Boolean(node.availableParameterKeysTruncated || result.availableParameterKeys.length < totalParameterKeys);
    }
    result.totalProvenance = totalProvenance;
    result.provenanceTruncated = Boolean(node.provenanceTruncated || result.provenance.length < totalProvenance
      || result.provenance.some((item, index) => JSON.stringify(item) !== JSON.stringify(provenance[index])));
    result.sourceTruncated = Boolean(node.sourceTruncated || JSON.stringify(result.source) !== JSON.stringify(source));
    result.totalTags = totalTags;
    result.tagsTruncated = Boolean(node.tagsTruncated || result.tags.length < totalTags);
  }
  flags();
  // Shrink projections, never parameter values. The complete values remain in
  // store.get(id); every preview carries the omission counts/flags.
  while (size(result) > maxBytes) {
    if (result.provenance.length) result.provenance.pop();
    else if (Object.keys(result.source).length) delete result.source[Object.keys(result.source).at(-1)];
    else if (result.availableParameterKeys?.length) result.availableParameterKeys.pop();
    else if (result.tags.length) result.tags.pop();
    else if (Object.keys(result.parameters || {}).length) delete result.parameters[Object.keys(result.parameters).at(-1)];
    else if (result.content) result.content = excerpt(result.content, Math.floor(textSize(result.content) / 2));
    else if (metadata.some(key => has(result, key))) {
      delete result[metadata.findLast(key => has(result, key))];
      result.metadataTruncated = true;
    } else if (result.title) {
      const compact = { id: result.id, title: node.title, kind: result.kind, content: '', contentTruncated: true,
        totalContentBytes: result.totalContentBytes, provenanceTruncated: Boolean(totalProvenance || node.provenanceTruncated),
        sourceTruncated: Boolean(Object.keys(source).length || node.sourceTruncated), tagsTruncated: Boolean(totalTags || node.tagsTruncated),
        ...(parameters || keys.length || node.parametersTruncated ? { parameters: null, parametersTruncated: true,
          availableParameterKeys: [], availableParameterKeysTruncated: Boolean(totalParameterKeys) } : {}),
      };
      if (size(compact) <= maxBytes) return compact;
      result.title = excerpt(String(result.title), Math.floor(textSize(String(result.title)) / 2));
      result.titleTruncated = true;
    } else {
      const minimal = { id: result.id, title: result.title, kind: result.kind, content: '', contentTruncated: true,
        totalContentBytes: result.totalContentBytes, truncated: true };
      if (size(minimal) > maxBytes) throw new RangeError('The memory identity exceeds the requested preview budget.');
      return minimal;
    }
    flags();
  }
  // Use remaining space for readable content without displacing complete keys.
  let low = Array.from(result.content).length;
  const characters = Array.from(content);
  let high = characters.length;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    const previous = result.content;
    result.content = characters.slice(0, middle).join('');
    flags();
    if (size(result) <= maxBytes) low = middle;
    else { high = middle - 1; result.content = previous; flags(); }
  }
  result.content = characters.slice(0, low).join('');
  flags();
  return result;
}

function boundedMemoryGraph(graph, maxBytes = 16000) {
  maxBytes = budget(maxBytes);
  const nodes = Array.isArray(graph?.nodes) ? graph.nodes : [];
  const edges = Array.isArray(graph?.edges) ? graph.edges : [];
  const result = { nodes: [], edges: [], truncated: Boolean(graph?.truncated),
    totalNodes: graph?.totalNodes ?? nodes.length, totalEdges: graph?.totalEdges ?? edges.length };
  const nodeAllowance = Math.max(256, Math.floor(maxBytes * 0.65));
  for (const node of nodes) {
    const parameterKeys = node.availableParameterKeys || Object.keys(node.parameters || {});
    const preview = boundedMemoryNode({ ...node, content: excerpt(node.summary || node.content || '', 160),
      contentTruncated: Boolean(node.contentTruncated || textSize(node.summary || node.content || '') > 160),
      totalContentBytes: node.totalContentBytes ?? textSize(node.content ?? node.summary ?? ''),
      parameters: null, parametersTruncated: Boolean(node.parametersTruncated || parameterKeys.length),
      availableParameterKeys: parameterKeys.slice(0, 8), totalParameterKeys: node.totalParameterKeys ?? parameterKeys.length,
      provenance: [], totalProvenance: node.totalProvenance ?? node.provenance?.length ?? 0,
      provenanceTruncated: Boolean(node.provenance?.length || node.provenanceTruncated), source: {}, sourceTruncated: Boolean(Object.keys(node.source || {}).length),
    }, Math.min(1200, Math.max(256, nodeAllowance)));
    if (size({ ...result, nodes: [...result.nodes, preview] }) > nodeAllowance) { result.truncated = true; break; }
    result.nodes.push(preview);
  }
  const ids = new Set(result.nodes.map(node => node.id));
  for (const edge of edges) {
    const fromId = edge.fromId ?? edge.from;
    const toId = edge.toId ?? edge.to;
    if (!ids.has(fromId) || !ids.has(toId)) { result.truncated = true; continue; }
    const preview = { id: edge.id, fromId, toId, from: fromId, to: toId, type: edge.type,
      ...(edge.label === undefined ? {} : { label: excerpt(edge.label, 160), ...(textSize(edge.label) > 160 ? { labelTruncated: true } : {}) }),
      ...(edge.createdAt ? { createdAt: edge.createdAt } : {}) };
    for (const key of ['weight', 'semanticScore', 'correlationScore', 'distance']) {
      if (typeof edge[key] === 'number' && Number.isFinite(edge[key])) preview[key] = edge[key];
    }
    if (typeof edge.inferred === 'boolean') preview.inferred = edge.inferred;
    if (typeof edge.model === 'string') preview.model = excerpt(edge.model, 160);
    if (size({ ...result, edges: [...result.edges, preview] }) > maxBytes) { result.truncated = true; break; }
    result.edges.push(preview);
  }
  result.truncated ||= result.nodes.length < result.totalNodes || result.edges.length < result.totalEdges;
  return result;
}

module.exports = { boundedMemoryNode, boundedMemoryGraph };
